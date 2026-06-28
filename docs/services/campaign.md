# CampaignService — Điều phối B2B

> 🟢 merged main (PR #22). Code: `src/services/Isas.CampaignService`. DB: `isas_campaign`. Gateway: `/api/v1/campaign`.
> Quy ước chung: [../architecture.md](../architecture.md) §5. Engine phỏng vấn: [interview.md](interview.md). Phân việc: [../work-division.md](../work-division.md).
>
> **Hiện trạng implement (2026-06-27):** ✅ 6 bug đã đóng · soft-delete (C9) · **lifecycle đầy đủ** (C7: guard + `POST /publish` Draft→Active + `PUT /status` Active→Closed→Archived) · **publish + `campaign_criteria` (Σweight=1)** (C8) · **`audit_logs`** (C10) · **snake_case** (§5) — build sạch, **34 unit test pass**, `isas_campaign` migrate server (4 bảng). **C8 AI thật:** publish gọi **AIService `POST /suggest-criteria`** (Gemini) → map `campaign_criteria` (Σ=1), **fallback** bộ mặc định nếu AI lỗi. ✅ **live HTTP OK** (container `aiapi` đã deploy code mới qua `docker cp`+`restart`; `POST /suggest-criteria` trả Σ=1.0). ⚠ Ephemeral — recreate/`compose up` container sẽ mất (image vẫn code cũ); permanent cần **rebuild image**. Code dùng `employer_id` (**chưa wire `org_id`** dù A1 đã có `Organization`/`org_members` trên main). ❌ chưa làm: distribution (magic-link), ranking/result/export, `session_integrity_events`, `campaign_invitations`, **JD/Criteria nhập text (`jdText`/`criteriaText` — C11)** + **tiêu chí structured HR khai thẳng (`criteria[]` — C12)** (code hiện chỉ nhận PDF + tiêu chí chỉ từ AI). DB **4/7 bảng**. *(Phần dưới mô tả thiết kế TARGET đầy đủ.)*

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

### `/api/v1/campaign` (JWT role **Employer**; `employerId` từ claim) — **JD/Criteria: text trực tiếp `jdText`/`criteriaText` 🔜 HOẶC PDF ≤ 10MB**
| Method | Path | Mô tả |
|---|---|---|
| GET | `/campaign` | Danh sách campaign của chính mình (✅ đã lọc theo `employer_id`) |
| GET | `/campaign/{id}` | Chi tiết (kèm câu hỏi) |
| POST | `/campaign` | Tạo (Draft). Body `{ title, domain, jdText?, criteriaText?, criteria?, maxCandidates?, timeLimitMinutes, antiCheatEnabled, startsAt, expiresAt, questions[] }`. **🔜 `jdText`/`criteriaText`** = JD/Criteria dạng text (set `*_text`, `*_file_url=null`); **🔜 `criteria`** = `CriterionItem[]` tiêu chí structured HR khai thẳng |
| POST | `/campaign/{id}/files` | Upload **PDF** JD/Criteria. `multipart`: `jdFile?`, `criteriaFile?` (parse PDF → `*_text`). *(Bỏ `jdFile`/`criteriaFile` nếu đã nhập `jdText`/`criteriaText`.)* |
| POST | `/campaign/{id}/files/download?fileType=jd\|criteria` | Tải file (chỉ khi JD/Criteria là file) |
| PUT | `/campaign/{id}` | Sửa campaign (check ownership). Body có thể gồm **🔜 `jdText?`/`criteriaText?`** (text) và **🔜 `criteria?`** (`CriterionItem[]` structured) để cập nhật/ghi đè JD/Criteria |
| PUT | `/campaign/{id}/files` | Thay JD/Criteria (xóa file cũ) |
| PUT | `/campaign/{id}/questions` | Thay toàn bộ câu hỏi. Body `List<QuestionItem>` |
| DELETE | `/campaign/{id}` | **Soft delete** (set `deleted_at`) — giữ lịch sử/audit; file SeaweedFS purge sau 90 ngày bằng cronjob |
| POST | `/campaign/{id}/publish` | **✅ C8** Draft→Active + sinh `campaign_criteria` (Σweight=1) + ghi `audit_logs`. **🔜 C12:** có `criteria[]` HR khai thẳng → dùng luôn (bỏ qua AI); không có → AI `/suggest-criteria` (Gemini + fallback). Sai trạng thái/thiếu câu hỏi → 409 |
| PUT | `/campaign/{id}/status` | **✅ C7** transition Active→Closed→Archived (bước sai → 409). Body `{ status }` |

`QuestionItem`: `{ questionText, source: "AiGenerated"|"CustomHr", isRequired }`.
`CriterionItem` 🔜: `{ name: string, description?: string, weight: decimal(5,4), maxScore: int }` — tiêu chí **CÓ CẤU TRÚC** HR nhập thẳng (Σweight=1).

> **🔜 Nguồn JD & Criteria — nhập text/structured, KHÔNG bắt buộc PDF.** `*_text` là nguồn chung; AI sinh câu hỏi + đề xuất tiêu chí đọc `jd_text`/`criteria_text` **bất kể nguồn**.
> - **JD** — 2 cách: (a) **text** `jdText` → `jd_text` (`jd_file_url=null`); (b) **PDF** `jdFile` → parse → `jd_text`.
> - **Criteria** — 3 cách, **ưu tiên giảm dần**: **(1) structured** `criteria[]` (`CriterionItem[]`) → HR khai thẳng `name`/`weight`/`maxScore`/`description` → lưu `campaign_criteria` (`source=HrEdited`), **publish KHỎI cần AI**; **(2) text** `criteriaText`; **(3) PDF** `criteriaFile`. Với (2)/(3) → publish gọi AI `/suggest-criteria` đề xuất cấu trúc rồi HR duyệt.
> - **Ưu tiên khi nhập trùng:** **text ƯU TIÊN file** (gửi cả hai → dùng **text**, bỏ file); Criteria: **`criteria[]` structured ưu tiên cao nhất** (có thì bỏ qua text/file/AI). Luôn quy về `campaign_criteria` có cấu trúc — **không** chấm trên text thô.

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
| jd_file_url / criteria_file_url | text? | ⚠ **lưu key, không phải full URL** (bug #1); **null nếu nhập text trực tiếp** (🔜 `jdText`/`criteriaText`) |
| jd_text / criteria_text | text? | từ **PDF parse HOẶC nhập text trực tiếp** (🔜 `jdText`/`criteriaText`) — **nguồn để AI sinh câu hỏi + đề xuất tiêu chí** (không chấm trực tiếp trên text) |
| starts_at | timestamptz | bắt buộc |
| expires_at | timestamptz? | |
| created_at / updated_at | timestamptz | `now()` |
| deleted_at | timestamptz? | **soft delete** (null = còn sống); mọi query lọc `deleted_at IS NULL` |

### `campaign_questions`
`id` · `campaign_id` (FK Cascade) · `employer_id` · `question_text` · `source` (enum `AiGenerated`/`CustomHr`) · `time_limit_seconds?` · `is_required` (mặc định true) · `created_at`.

### `campaign_criteria` (tiêu chí CÓ CẤU TRÚC — HR khai thẳng 🔜 / AI đề xuất, HR duyệt)
| Cột | Kiểu | Ràng buộc / ghi chú |
|---|---|---|
| id | uuid | PK (`gen_random_uuid()`) |
| campaign_id | uuid | FK → `campaigns` (Cascade); **index** |
| order_no | int | thứ tự hiển thị (HR sắp); **UNIQUE (campaign_id, order_no)** |
| name | varchar(255) | bắt buộc, non-empty (trim); **UNIQUE (campaign_id, name)** — chống trùng tiêu chí |
| description | text? | mô tả mức điểm (optional) |
| weight | numeric(5,4) | **0 < weight ≤ 1**; Σ/campaign **≈ 1** — **KHÔNG ép DB = 1** (làm tròn 4 chữ số khó khít, vd 0.3333×3 = 0.9999); điểm tổng **chuẩn hoá chia Σweight** ([interview.md](interview.md) §BC9) nên Σ lệch ±ε vẫn đúng |
| max_score | int | **≥ 1** |
| source | varchar(16) | enum: `AiSuggested` · `HrEdited` (HR khai `criteria[]` structured 🔜 = `HrEdited`) |
| created_at / updated_at | timestamptz | `now()` |

→ Khi tạo session, gửi sang Interview để materialize thành `rubric_criteria(campaign_id)` (id phía Interview **khác** id ở đây — ref lỏng, copy giá trị `name`/`weight`/`max_score`).

**🔜 Lưu structured `criteria[]` — CẨN THẬN:**
- **Khi nào ghi:** create/update lúc campaign còn `Draft`; sau `Active` **khóa** sửa tiêu chí (C7 → **409**).
- **Replace-all atomic:** PUT `criteria[]` = **xóa hết** `campaign_criteria` của campaign rồi **insert lại** trong **1 transaction** (như PUT questions) → không trộn bộ cũ/mới, không nửa vời. `order_no` đánh lại theo thứ tự gửi lên.
- **Validate trước khi ghi (400 nếu hỏng):** ≥ 1 tiêu chí · `name` non-empty + **không trùng** trong campaign · `0 < weight ≤ 1` · `max_score ≥ 1` · `Σweight ∈ [0.99, 1.01]` (ngoài khoảng → **400**; trong khoảng → **chuẩn hoá Σ→1** rồi lưu).
- **Audit:** ghi `audit_logs(action=EditCriteria)` mỗi lần đổi.
- **Nơi đọc/gộp điểm:** **luôn chia `Σweight`** (đừng giả định = 1 tuyệt đối) — phòng sai số làm tròn.

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
- **🔜 HR khai structured (C12 — ưu tiên cao nhất):** body có `criteria[]` (`CriterionItem[]`) → publish **dùng thẳng** làm `campaign_criteria` (`source=HrEdited`), **bỏ qua AI**; chỉ validate Σweight (lệch 1 → chuẩn hoá hoặc 400). Cách **chính xác nhất** — HR khai đúng trường (`name`/`weight`/`maxScore`/`description`), không để AI suy từ text.
- **Không** chấm trên `criteria_text` thô. **Nếu KHÔNG có `criteria[]`:** khi `Draft → Active` AI **đề xuất** bộ tiêu chí có cấu trúc từ `jd_text`/`criteria_text` (PDF **hoặc** text trực tiếp — `name`, `weight`, `max_score`, mô tả mức điểm), **HR sửa/duyệt** (HR-in-the-loop).
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
