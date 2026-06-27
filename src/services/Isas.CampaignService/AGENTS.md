> **Bản sao cho agent** của [`docs/services/campaign.md`](../../../docs/services/campaign.md) — contract (API + DB + business rules) của CampaignService. **Source of truth ở `docs/`** (sửa thiết kế tại đó rồi copy lại, đừng sửa bản này lệch). Cửa vào + ràng buộc chung: [`docs/AGENTS.md`](../../../docs/AGENTS.md).
>
> **Trạng thái (2026-06-27):** ✅ ĐÃ LÀM (build sạch, 34 unit test pass): 6 bug (C1–C6) · soft-delete (C9) · lifecycle (C7) · **publish + campaign_criteria Σ=1 + AIService /suggest-criteria (Gemini), live HTTP OK** (C8) · **audit_logs** (C10) · snake_case. CÒN: rebuild image AIService cho permanent (container đang deploy tạm qua docker cp) · `org_id` (A1) · distribution/ranking/export (S3/S4).

---

# CampaignService — Điều phối B2B

> 🟡 branch `features/campaign-service`. Code: `src/services/Isas.CampaignService`. DB: `isas_campaign`. Gateway: `/api/v1/campaign`.
> Quy ước chung: [../architecture.md](../architecture.md) §5. Engine phỏng vấn: [interview.md](interview.md). Phân việc: [../work-division.md](../work-division.md).
>
> **Hiện trạng implement (2026-06-27):** ✅ 6 bug đã đóng · soft-delete (C9) · **lifecycle đầy đủ** (C7: guard + `POST /publish` Draft→Active + `PUT /status` Active→Closed→Archived) · **publish + `campaign_criteria` (Σweight=1)** (C8) · **`audit_logs`** (C10) · **snake_case** (§5) — build sạch, **34 unit test pass**, `isas_campaign` migrate server (4 bảng). **C8 AI thật:** publish gọi **AIService `POST /suggest-criteria`** (Gemini) → map `campaign_criteria` (Σ=1), **fallback** bộ mặc định nếu AI lỗi. ✅ **live HTTP OK** (container `aiapi` đã deploy code mới qua `docker cp`+`restart`; `POST /suggest-criteria` trả Σ=1.0). ⚠ Ephemeral — recreate/`compose up` container sẽ mất (image vẫn code cũ); permanent cần **rebuild image**. Code dùng `employer_id` (**chưa có `org_id`/org** — chờ A1). ❌ chưa làm: distribution (magic-link), ranking/result/export, `session_integrity_events`, `campaign_invitations`, AI thật cho tiêu chí. DB **4/7 bảng**. *(Phần dưới mô tả thiết kế TARGET đầy đủ.)*

## Vai trò
Lớp **điều phối B2B**, không tự chạy phỏng vấn:
- Tạo **campaign** từ JD + **câu hỏi** (AI gợi ý từ JD — [ai.md](ai.md)) + **tiêu chí**. Khi **publish**: AI biến tiêu chí PDF → **bộ tiêu chí CÓ CẤU TRÚC** (name/weight/max_score), **HR duyệt** (xem rules).
- **Distribution**: phát link mời (magic-link) + email hàng loạt; ứng viên vào → gọi **InterviewService** create-or-get session gắn `campaign_id` (kèm câu hỏi + tiêu chí).
- **Credit**: **reserve** credit của **org** (chủ campaign) khi ứng viên bắt đầu (PaymentService) — [payment.md](payment.md).
- **Ranking + Result**: **nghe event `SessionScored`** → cập nhật **bảng ranking read-model trong `isas_campaign`** (không gọi HTTP đọc điểm mỗi lần) → xếp hạng, pass/fail, xuất CSV/PDF.

> Luồng end-to-end xuyên service ở [../architecture.md](../architecture.md) §4.1 (file này chỉ tả phần Campaign).

---

## ✅ Bug đã sửa (2026-06-27)
Code: `Services/CampaignService.cs` + `Controllers/CampaignController.cs`. Build sạch.
1. ✅ **Full URL → key**: `HandleFileAsync` lưu *key* (`campaigns/{id}/jd.pdf`); download/delete dùng key. *(Response không expose URL; cần thì ghép `_file.GetUrl(key)`.)*
2. ✅ **Lọc `employer_id`**: đẩy `employerId` vào **mọi** method service + filter → non-owner = 404 (list + single + update + delete + download).
3. ✅ **AntiCheat**: `UpdateCampaignRequest.AntiCheatEnabled` → `bool?`, chỉ gán khi `HasValue`.
4. ✅ **Download**: trả `application/pdf` + tên `campaign_{id}_{type}.pdf`; `catch FileNotFoundException` → 404.
5. ✅ **Message**: còn "Only PDF is accepted."
6. ✅ **Authorize**: bật lại `[Authorize(Roles="Employer")]` toàn controller (JWT đã wired ở `Program.cs`).

---

## API

### `/api/v1/campaign` (JWT role **Employer**; `employerId` từ claim) — JD/Criteria chỉ PDF ≤ 10MB
| Method | Path | Mô tả |
|---|---|---|
| GET | `/campaign` | Danh sách campaign của chính mình (✅ đã lọc theo `employer_id`) |
| GET | `/campaign/{id}` | Chi tiết (kèm câu hỏi) |
| POST | `/campaign` | Tạo (Draft). Body `{ title, domain, maxCandidates?, timeLimitMinutes, antiCheatEnabled, startsAt, expiresAt, questions[] }` |
| POST | `/campaign/{id}/files` | Upload JD/Criteria. `multipart`: `jdFile?`, `criteriaFile?` (parse PDF → text) |
| POST | `/campaign/{id}/files/download?fileType=jd\|criteria` | Tải file |
| PUT | `/campaign/{id}` | Sửa campaign (check ownership) |
| PUT | `/campaign/{id}/files` | Thay JD/Criteria (xóa file cũ) |
| PUT | `/campaign/{id}/questions` | Thay toàn bộ câu hỏi. Body `List<QuestionItem>` |
| DELETE | `/campaign/{id}` | **Soft delete** (set `deleted_at`) — giữ lịch sử/audit; file SeaweedFS purge sau 90 ngày bằng cronjob |
| POST | `/campaign/{id}/publish` | **✅ C8** Draft→Active + sinh `campaign_criteria` (Σweight=1; ⚠ AI **stub**) + ghi `audit_logs`. Sai trạng thái/thiếu câu hỏi → 409 |
| PUT | `/campaign/{id}/status` | **✅ C7** transition Active→Closed→Archived (bước sai → 409). Body `{ status }` |

`QuestionItem`: `{ questionText, source: "AiGenerated"|"CustomHr", isRequired }`.

### Distribution / Result (❌ kế hoạch — cùng prefix `/campaign`)
- `POST /campaign/{id}/invitations` — phát lời mời + email hàng loạt.
- `GET /invitations/{token}` — ứng viên vào bài (→ Interview tạo/lấy session gắn `campaignId`).
- `POST /invitations/{id}/reissue` — Employer phát lại token (vô hiệu token cũ).
- `GET /campaign/{id}/results` + `/results/export?format=csv|pdf` — bảng kết quả, xếp hạng, xuất file.

---

## DB — `isas_campaign`
```
campaigns ─┬─1──* campaign_questions
           ├─1──* campaign_criteria          (org_id, employer_id ref lỏng → Auth)
           └─1──* campaign_invitations ──► session_id (ref lỏng → Interview)
campaign_rankings · session_integrity_events · audit_logs   (theo session/org)
```

### `campaigns`
| Cột | Kiểu | Ghi chú |
|---|---|---|
| id | uuid | PK (`gen_random_uuid()`) |
| org_id | uuid | **tổ chức sở hữu** (billing/quyền theo org); ref lỏng → Auth. ⚠ **chưa implement** — code hiện chỉ có `employer_id` (chờ Auth-Organization, A1) |
| employer_id | uuid | HR tạo campaign; ref lỏng → Auth; **index (org_id, status)**, (org_id, created_at) |
| title | varchar(255) | bắt buộc |
| domain | varchar(100)? | |
| status | varchar(20) | enum: `Draft`/`Active`/`Closed`/`Archived` (mặc định Draft) |
| max_candidates | int? | |
| time_limit_minutes | int? | |
| anti_cheat_enabled | bool | mặc định `true` |
| jd_file_url / criteria_file_url | text? | ⚠ **lưu key, không phải full URL** (bug #1) |
| jd_text / criteria_text | text? | text trích từ PDF — **nguồn để AI sinh câu hỏi + đề xuất tiêu chí** (không chấm trực tiếp trên text) |
| starts_at | timestamptz | bắt buộc |
| expires_at | timestamptz? | |
| created_at / updated_at | timestamptz | `now()` |
| deleted_at | timestamptz? | **soft delete** (null = còn sống); mọi query lọc `deleted_at IS NULL` |

### `campaign_questions`
`id` · `campaign_id` (FK Cascade) · `employer_id` · `question_text` · `source` (enum `AiGenerated`/`CustomHr`) · `time_limit_seconds?` · `is_required` (mặc định true) · `created_at`.

### `campaign_criteria` (tiêu chí CÓ CẤU TRÚC — AI đề xuất, HR duyệt)
`id` · `campaign_id` (FK Cascade) · `name` · `description?` · `weight` numeric(5,4) (Σ/campaign = 1) · `max_score` · `source` (`AiSuggested`/`HrEdited`) · `created_at`.
→ Khi tạo session, gửi sang Interview để materialize thành `rubric_criteria(campaign_id)`.

### `campaign_invitations` (❌ kế hoạch)
token 1 lần · email ứng viên · hạn dùng · `used_at` · `session_id` (ref lỏng → Interview).

### `session_integrity_events` (anti-cheat — nếu build)
`id` · `session_id` (ref lỏng) · `type` (`tab_switch`/`focus_lost`/`paste`/`fullscreen_exit`/`multi_voice`) · `at` timestamptz.

### `campaign_rankings` — read-model (cập nhật bằng event `SessionScored`)
`id` · `campaign_id` · `candidate_id` · `session_id` (**unique** — upsert idempotent) · `total_score` · `rank` · `result` (`Pass`/`Fail`) · `updated_at`. → dashboard đọc local, không gọi xuyên service.

### `audit_logs` — vết thao tác HR
`id` · `org_id` · `actor_user_id` · `action` (`CreateCampaign`/`EditQuestions`/`EditCriteria`/`Publish`/`Delete`/`Reissue`…) · `entity` · `entity_id` · `summary`/`diff?` · `at`.

---

## Business rules

### State machine — Campaign
```
Draft ──► Active ──► Closed ──► Archived
```
- Chỉ sửa câu hỏi/tiêu chí khi `Draft` (Active rồi nên khóa, tránh loạn kết quả). **✅ đã enforce (C7):** `UpdateQuestions`/`UpdateFiles` → **409** nếu `Status != Draft`. **✅ transition đầy đủ:** `POST /campaign/{id}/publish` (Draft→Active) + `PUT /campaign/{id}/status` (Active→Closed→Archived; bước sai → 409).
- Chỉ thành viên **org sở hữu** (`org_id` khớp; OrgAdmin/HrMember) được sửa/xóa/xem kết quả. *(Hiện enforce theo `employer_id`; org chưa có.)*

### Tiêu chí chấm — text → CÓ CẤU TRÚC (khi publish)
> **✅ implement (C8):** `POST /campaign/{id}/publish` (Draft→Active) sinh `campaign_criteria` (Σweight=1, `source=AiSuggested`) + ghi `audit_logs`. **Gọi AIService `POST /suggest-criteria` (Gemini) thật** → chuẩn hoá weight Σ=1; **fallback** bộ mặc định 0.4/0.3/0.3 khi AI lỗi/rỗng. ✅ live-test Gemini OK. ⚠ Còn: rebuild container Mac (route HTTP), HR-edit/duyệt tiêu chí (UI).
- **Không** chấm trên `criteria_text` thô. Khi `Draft → Active`: AI **đề xuất** bộ tiêu chí có cấu trúc từ JD/Criteria PDF (`name`, `weight`, `max_score`, mô tả mức điểm), **HR sửa/duyệt** (HR-in-the-loop).
- Lưu thành `campaign_criteria`. Khi tạo session phỏng vấn, Campaign **gửi bộ tiêu chí** sang InterviewService → materialize thành `rubric_criteria(campaign_id)` → **chấm như rubric thường** (xem [interview.md](interview.md)).
- Σ`weight` của 1 campaign nên = 1 (chuẩn hóa điểm tổng).

### Distribution — link mời "1 lần NỘP" (không phải 1 lần mở)
- Mỗi ứng viên 1 **token duy nhất**, hạn ≤ `expires_at` campaign.
- Mở token hợp lệ → tạo **hoặc lấy lại** session Interview gắn `campaign_id`. Rớt mạng → mở lại **resume** đúng session, tới khi submit.
- **Khóa sau submit**: token `used`.
- **Lỗi/hết hạn/đã dùng** → **không tự đẻ link mới**; Employer **re-issue** (vô hiệu token cũ). Rớt giữa chừng → dùng lại token cũ, không re-issue.
- Tôn trọng `max_candidates`.

### Anti-cheat (`anti_cheat_enabled`)
- **Định nghĩa "cheat" (tín hiệu, không kết luận máy móc):** FE — chuyển tab / mất focus / paste / thoát fullscreen; (tùy chọn) AI — **phát hiện nhiều giọng nói** (`multi_voice`) từ audio.
- **Hệ quả = FLAG cho HR xem xét, KHÔNG auto-hủy bài.** False-positive auto-hủy sẽ giết oan ứng viên thật → chỉ gắn cảnh báo + số liệu vào kết quả, **HR quyết định**.
- Không phải proctoring/webcam. Không kịp build → ghi rõ "chỉ là cờ" (đừng hứa suông).
- ⚠ **Rủi ro riêng (ngoài tín hiệu FE):** ứng viên **prompt-injection vào bộ chấm AI** (đọc "chấm tối đa" vào mic) → lái điểm. Chống ở tầng AIService — xem [ai.md](ai.md) §Vấn đề đã biết.

### Ranking & Result — event-driven read-model
- Nghe **`SessionScored`** (RabbitMQ) → **upsert `campaign_rankings`** theo `session_id` (idempotent) với `total_score` = `Σ(điểm_tiêu_chí × weight)` chuẩn hóa (Interview tính). **Không** gọi HTTP đọc điểm mỗi lần xem.
- Tính lại `rank` + `result` (pass/fail theo ngưỡng Employer đặt) trong campaign. Chỉ xếp hạng ứng viên đã `Scored`; dở dang để riêng.
- Dashboard/đọc CSV/PDF **đọc local** từ `campaign_rankings`. Fallback: endpoint **backfill** gọi Interview nếu nghi miss event.

### Soft delete & Audit
- **Xóa = soft** (`deleted_at`), giữ lịch sử cho **audit/đối chất** (B2B ứng viên kiện kết quả). Cronjob purge file SeaweedFS sau **90 ngày** (giữ điểm + transcript để chứng minh). **✅ đã làm (C9):** `deleted_at` + **global query filter** (`DeletedAt == null` → mọi query tự ẩn campaign đã xoá); `DeleteCampaign` set `deleted_at`, **giữ** file + câu hỏi. **✅ `audit_logs` (C10):** ghi ở Create/EditQuestions/Delete/Publish/Transition (`actor_user_id`=employer, `action`, `entity_id`, `summary`). *(Cronjob purge 90 ngày chưa làm.)*
- Mọi mutation quan trọng (tạo/sửa câu hỏi, đổi tiêu chí, publish, xóa, re-issue) ghi **`audit_logs`** (`actor_user_id`, action, entity, `at`).

### Exception — org hết credit / downgrade / quá hạn
- Chỉ **chặn hành động TƯƠNG LAI** (mời ứng viên mới, reserve session mới). **KHÔNG** đóng ép campaign đang `Active`, **KHÔNG** văng ứng viên đang thi (reservation đã giữ chỗ → in-flight an toàn). Xem [payment.md](payment.md) (mục *Đình chỉ / hết hạn*).
