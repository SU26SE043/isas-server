# CampaignService — Điều phối B2B

> 🟡 branch `features/campaign-service`. Code: `src/services/Isas.CampaignService`. DB: `isas_campaign`. Gateway: `/api/v1/campaign`.
> Quy ước chung: [../architecture.md](../architecture.md) §5. Engine phỏng vấn: [interview.md](interview.md). Phân việc: [../work-division.md](../work-division.md).

## Vai trò
Lớp **điều phối B2B**, không tự chạy phỏng vấn:
- Tạo **campaign** từ JD + **câu hỏi** (AI gợi ý từ JD — [ai.md](ai.md)) + **tiêu chí**. Khi **publish**: AI biến tiêu chí PDF → **bộ tiêu chí CÓ CẤU TRÚC** (name/weight/max_score), **HR duyệt** (xem rules).
- **Distribution**: phát link mời (magic-link) + email hàng loạt; ứng viên vào → gọi **InterviewService** create-or-get session gắn `campaign_id` (kèm câu hỏi + tiêu chí).
- **Credit**: **reserve** credit của **org** (chủ campaign) khi ứng viên bắt đầu (PaymentService) — [payment.md](payment.md).
- **Ranking + Result**: **nghe event `SessionScored`** → cập nhật **bảng ranking read-model trong `isas_campaign`** (không gọi HTTP đọc điểm mỗi lần) → xếp hạng, pass/fail, xuất CSV/PDF.

> Luồng B2B end-to-end xuyên service ở [../architecture.md](../architecture.md) §4.2 (file này chỉ tả phần Campaign).

---

## 🔴 Bug đang mở (sửa khi hoàn thiện)
Code: `Services/CampaignService.cs`.
1. **Lưu full URL nhưng download/delete dùng làm S3 key** → tải/xóa file hỏng. Lưu *key* (`campaigns/{id}/jd.pdf`), ghép URL khi trả response.
2. **`GET /campaign` không lọc `EmployerId`** → rò rỉ campaign của employer khác.
3. **`UpdateCampaign` luôn ghi đè `AntiCheatEnabled`** (`bool` không nullable) → đổi `bool?`.
4. Download trả sai `application/zip`/tên `.zip` cho 1 PDF; `FileNotFoundException` không catch → 500 thay vì 404.
5. Thông báo lỗi nói "PDF and DOCX" nhưng chỉ nhận PDF.
6. **Bật lại `[Authorize(Roles="Employer")]`** (đang comment hết) — role đã có ở Auth.

---

## API — `/api/v1/campaign`

> **Quy ước:** Base public `/api/v1/campaign/*` (gateway → service `/campaign/*`). Auth: **JWT role `Employer`** (org sở hữu; `employerId`+`org_id` từ claim) — *⚠ đang comment, bug #6*. JD/Criteria chỉ **PDF ≤ 10MB**. **Kiểu dữ liệu:** `uuid` · `string` · `int` · `bool` · `decimal(p,s)` · `datetime` · `enum(string)` · `T[]` · `?`. Mã lỗi chung: [../architecture.md](../architecture.md) §6. *(🟡 = có trên branch (kèm bug §🔴) · 🔜 = kế hoạch chưa build.)*

### Schemas (DTO)

```
Campaign {
  id:                uuid
  orgId:             uuid                // tổ chức sở hữu
  employerId:        uuid                // HR tạo
  title:             string
  domain:            string?
  status:            enum(string)        // Draft · Active · Closed · Archived
  maxCandidates:     int?
  timeLimitMinutes:  int?
  antiCheatEnabled:  bool                // ⚠ bug #3 → đổi bool?
  jdFileUrl:         string?             // ⚠ lưu KEY, không full URL (bug #1)
  criteriaFileUrl:   string?
  startsAt:          datetime
  expiresAt:         datetime?
  createdAt:         datetime
  updatedAt:         datetime
  deletedAt:         datetime?           // soft delete
  questions:         QuestionItem[]      // chỉ ở GET /{id}
}

QuestionItem {
  questionText:     string
  source:           enum(string)         // AiGenerated · CustomHr
  isRequired:       bool
  timeLimitSeconds: int?
}

CampaignCriterion {                      // tiêu chí CÓ CẤU TRÚC (AI đề xuất, HR duyệt)
  id:          uuid
  campaignId:  uuid
  name:        string
  description: string?
  weight:      decimal(5,4)              // Σ/campaign = 1
  maxScore:    int
  source:      enum(string)              // AiSuggested · HrEdited
}

CampaignRanking  🔜 {                    // read-model, cập nhật bằng event SessionScored
  campaignId:  uuid
  candidateId: uuid
  sessionId:   uuid
  totalScore:  decimal                   // Σ(điểm×weight) chuẩn hóa
  rank:        int
  result:      enum(string)              // Pass · Fail
  updatedAt:   datetime
}

Invitation  🔜 {
  id:             uuid
  campaignId:     uuid
  token:          string                 // magic-link, 1 lần NỘP
  candidateEmail: string
  expiresAt:      datetime
  usedAt:         datetime?
  sessionId:      uuid?                   // ref lỏng → Interview
}
```

### Campaign authoring (JWT Employer) 🟡

**`GET /campaign`** — Danh sách campaign của org *(⚠ phải lọc `employerId`/`org_id` — bug #2)*. → `Campaign[]`.
**`GET /campaign/{id}`** — Chi tiết kèm câu hỏi. → `Campaign` (có `questions`). Lỗi: **403/404**.

**`POST /campaign`** — Tạo (status `Draft`).
- Req: `{ title: string, domain: string?, maxCandidates: int?, timeLimitMinutes: int, antiCheatEnabled: bool, startsAt: datetime, expiresAt: datetime, questions: QuestionItem[] }` → Res **`201`** `Campaign`. Lỗi: **400** · **401**.

**`POST /campaign/{id}/files`** — Upload JD/Criteria. `multipart`: `jdFile: pdf?` · `criteriaFile: pdf?` (parse PDF→text). Lỗi: **400** (không phải PDF — *bug #5* message sai).
**`POST /campaign/{id}/files/download?fileType=jd|criteria`** — Tải file *(⚠ bug #1/#4: dùng key sai + sai content-type)*.

**`PUT /campaign/{id}`** — Sửa campaign (chỉ `Draft`; check ownership) → `Campaign`. *(⚠ bug #3: ghi đè `antiCheatEnabled`.)*
**`PUT /campaign/{id}/files`** — Thay JD/Criteria (xóa file cũ).
**`PUT /campaign/{id}/questions`** — Thay toàn bộ câu hỏi. Req: `QuestionItem[]`.
**`DELETE /campaign/{id}`** — **Soft delete** (`deleted_at`) → **`204`**; file SeaweedFS purge sau 90 ngày (cronjob).

### Distribution & Result (🔜 kế hoạch — cùng prefix `/campaign`)

**`POST /campaign/{id}/publish`** 🔜 — `Draft→Active`: AI đề xuất `CampaignCriterion[]` từ JD/Criteria → HR duyệt → lưu.
**`POST /campaign/{id}/invitations`** 🔜 — phát lời mời + email hàng loạt. Req: `{ emails: string[] }` → `Invitation[]`.
**`GET /invitations/{token}`** 🔜 — ứng viên vào bài (→ Interview create-or-get session gắn `campaignId`); sau submit token `used` (**403**).
**`POST /invitations/{id}/reissue`** 🔜 — Employer phát lại token (vô hiệu token cũ).
**`GET /campaign/{id}/results`** 🔜 → `CampaignRanking[]` (xếp hạng + pass/fail) · **`GET /campaign/{id}/results/export?format=csv|pdf`** 🔜 → file.

---

## DB — `isas_campaign`
```
campaigns ─┬─1──* campaign_questions
           ├─1──* campaign_criteria          (org_id, employer_id ref lỏng → Auth)
           └─1──* campaign_invitations ──► session_id (ref lỏng → Interview)
campaign_rankings · session_integrity_events · audit_logs   (theo session/org)
```

> **Quy ước kiểu DB:** `uuid·varchar(n)·text·int·numeric(p,s)·bool·timestamptz`, enum lưu **string**, `?`=nullable. Cột **snake_case**.

### `campaigns`
```
id                 uuid          PK (gen_random_uuid())
org_id             uuid          tổ chức sở hữu; ref lỏng → Auth
employer_id        uuid          HR tạo; ref lỏng → Auth
title              varchar(255)  NOT NULL
domain             varchar(100)?
status             varchar(20)   enum: Draft · Active · Closed · Archived (default Draft)
max_candidates     int?
time_limit_minutes int?
anti_cheat_enabled bool          default true (⚠ bug #3 → nên bool?)
jd_file_url        text?         ⚠ lưu KEY, không full URL (bug #1)
criteria_file_url  text?
jd_text            text?         text trích PDF — nguồn AI sinh câu hỏi + đề xuất tiêu chí
criteria_text      text?
starts_at          timestamptz   NOT NULL
expires_at         timestamptz?
created_at         timestamptz   default now()
updated_at         timestamptz
deleted_at         timestamptz?  soft delete (null=còn sống); query lọc deleted_at IS NULL
                                 index (org_id, status), (org_id, created_at)
```

### `campaign_questions`
```
id                uuid          PK
campaign_id       uuid          FK → campaigns (Cascade)
employer_id       uuid
question_text     text
source            varchar(16)   enum: AiGenerated · CustomHr
time_limit_seconds int?
is_required       bool          default true
created_at        timestamptz
```

### `campaign_criteria` — tiêu chí CÓ CẤU TRÚC (AI đề xuất, HR duyệt)
```
id          uuid          PK
campaign_id uuid          FK → campaigns (Cascade)
name        varchar
description text?
weight      numeric(5,4)  Σ/campaign = 1
max_score   int
source      varchar(16)   enum: AiSuggested · HrEdited
created_at  timestamptz
```
→ Khi tạo session, gửi sang Interview materialize thành `rubric_criteria(campaign_id)`.

### `campaign_invitations` — 🔜 kế hoạch
```
id             uuid          PK
campaign_id    uuid          FK → campaigns
token          varchar       UNIQUE, magic-link 1 lần NỘP
candidate_email varchar
expires_at     timestamptz   ≤ campaign.expires_at
used_at        timestamptz?
session_id     uuid?         ref lỏng → Interview
```

### `session_integrity_events` — anti-cheat (nếu build)
```
id         uuid          PK
session_id uuid          ref lỏng → Interview
type       varchar(20)   enum: tab_switch · focus_lost · paste · fullscreen_exit · multi_voice
at         timestamptz
```

### `campaign_rankings` — read-model (cập nhật bằng event `SessionScored`)
```
id           uuid          PK
campaign_id  uuid
candidate_id uuid
session_id   uuid          UNIQUE — upsert idempotent
total_score  numeric(6,2)  Σ(điểm×weight) chuẩn hóa (Interview tính)
rank         int
result       varchar(8)    enum: Pass · Fail
updated_at   timestamptz
```
→ dashboard đọc local, không gọi xuyên service.

### `audit_logs` — vết thao tác HR
```
id            uuid          PK
org_id        uuid
actor_user_id uuid
action        varchar(32)   enum: CreateCampaign·EditQuestions·EditCriteria·Publish·Delete·Reissue…
entity        varchar
entity_id     uuid
summary       text?
diff          jsonb?
at            timestamptz
```

---

## Business rules

### State machine — Campaign
```
Draft ──► Active ──► Closed ──► Archived
```
- Chỉ sửa câu hỏi/tiêu chí khi `Draft` (Active rồi nên khóa, tránh loạn kết quả).
- Chỉ thành viên **org sở hữu** (`org_id` khớp; OrgAdmin/HrMember) được sửa/xóa/xem kết quả.

### Tiêu chí chấm — text → CÓ CẤU TRÚC (khi publish)
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
- **Xóa = soft** (`deleted_at`), giữ lịch sử cho **audit/đối chất** (B2B ứng viên kiện kết quả). Cronjob purge file SeaweedFS sau **90 ngày** (giữ điểm + transcript để chứng minh).
- Mọi mutation quan trọng (tạo/sửa câu hỏi, đổi tiêu chí, publish, xóa, re-issue) ghi **`audit_logs`** (`actor_user_id`, action, entity, `at`).

### Exception — org hết credit / downgrade / quá hạn
- Chỉ **chặn hành động TƯƠNG LAI** (mời ứng viên mới, reserve session mới). **KHÔNG** đóng ép campaign đang `Active`, **KHÔNG** văng ứng viên đang thi (reservation đã giữ chỗ → in-flight an toàn). Xem [payment.md](payment.md) (mục *Đình chỉ / hết hạn*).
