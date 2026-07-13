> **Bản sao cho agent** của [`docs/services/campaign.md`](../../../docs/services/campaign.md) — contract (API + DB + business rules) của CampaignService. **Source of truth ở `docs/`** (sửa thiết kế tại đó rồi copy lại, đừng sửa bản này lệch). Cửa vào + ràng buộc chung: [`/AGENTS.md`](../../../AGENTS.md).
>
> **Trạng thái (2026-06-30):** ✅ ĐÃ LÀM (merged PR #22, build sạch, 34 unit test): 6 bug (C1–C6) · soft-delete (C9) · lifecycle (C7) · publish + campaign_criteria + AIService /suggest-criteria (C8) · audit_logs (C10) · snake_case. 🔜 doc TARGET mở rộng: JD/Criteria nhập text (C11) · tiêu chí structured HR khai thẳng (C12) · **lọc CV hàng loạt (C13–C15, D18/D19)**. CÒN: org_id (A1) · distribution/ranking/export (S3/S4). *(Doc đã chi tiết hoá: req/res mẫu · validation · bảng mã lỗi · sequence · index/edge.)*

---

# CampaignService — Điều phối B2B

> 🟢 merged main (PR #22). Code: `src/services/Isas.CampaignService`. DB: `isas_campaign`. Gateway: `/api/v1/campaign`.
> Quy ước chung: [../architecture.md](../architecture.md) §5. Engine phỏng vấn: [interview.md](interview.md). Phân việc: [../work-division.md](../work-division.md).
>
> **Hiện trạng implement (2026-06-27):** ✅ 6 bug đã đóng · soft-delete (C9) · **lifecycle đầy đủ** (C7: guard + `POST /publish` Draft→Active + `PUT /status` Active→Closed→Archived) · **publish + `campaign_criteria` (Σweight=1)** (C8) · **`audit_logs`** (C10) · **snake_case** (§5) — build sạch, **34 unit test pass**, `isas_campaign` migrate server (4 bảng). **C8 AI thật:** publish gọi **AIService `POST /suggest-criteria`** (Gemini) → map `campaign_criteria` (Σ=1), **fallback** bộ mặc định nếu AI lỗi. ✅ **live HTTP OK** (container `aiapi` đã deploy code mới qua `docker cp`+`restart`; `POST /suggest-criteria` trả Σ=1.0). ⚠ Ephemeral — recreate/`compose up` container sẽ mất (image vẫn code cũ); permanent cần **rebuild image**. Code dùng `employer_id` (**chưa wire `org_id`** dù A1 đã có `Organization`/`org_members` trên main). ❌ chưa làm: distribution (magic-link), ranking/result/export, `session_integrity_events`, `campaign_invitations`, **JD/Criteria nhập text (`jdText`/`criteriaText` — C11)** + **tiêu chí structured HR khai thẳng (`criteria[]` — C12)** (code hiện chỉ nhận PDF + tiêu chí chỉ từ AI), **lọc CV hàng loạt (C13–C15)**. DB **4/9 bảng**. *(Phần dưới mô tả thiết kế TARGET đầy đủ.)*

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

### Lọc ứng viên qua CV — sàng lọc hàng loạt (B2B) (🔜 C13–C15 — cùng prefix `/campaign`)
> **1 trong 2 cách lọc của app** (cách kia: phỏng vấn AI), **tùy chọn** + **MIỄN PHÍ phase 1** (D19). HR đổ **nhiều CV** ứng viên vào campaign → **lọc hybrid** (rule cứng trước, AI chấm khớp sau) → **shortlist xếp hạng** trước khi mời phỏng vấn (tiết kiệm slot). Engine phân tích = AIService `/analyze-cv` ([ai.md](ai.md)) **dùng chung với B2C**; **TÁI DÙNG** `campaign_criteria` làm rubric — **không** đụng engine phỏng vấn. State machine + luồng tiền chi tiết: §Business rules.

| Method | Path | Mô tả |
|---|---|---|
| POST | `/campaign/{id}/candidates` | **🔜 C13** Upload **nhiều PDF** CV (`multipart`: `files[]`, mỗi file ≤ 10MB). Parse → `cv_parsed_text`; chạy **hard-filter** (rule cứng) → mỗi ứng viên `Rejected(reason)`/`Filtered`; mỗi `Filtered` → đẩy job AI lên queue. Cần campaign `Active` + đã có `campaign_criteria`; **cap số CV/campaign** (chặn đốt AI vì free) → vượt **4xx** |
| GET | `/campaign/{id}/candidates` | **🔜 C14** Shortlist. Query `?status=&minScore=&skill=&sort=score\|name&page=`; mặc định `sort=score` DESC (`overall_match_score`). Lọc theo `employer_id` (chủ campaign) |
| GET | `/campaign/{id}/candidates/{candidateId}` | **🔜 C14** Chi tiết 1 ứng viên (summary, skills, điểm + reasoning từng tiêu chí) |
| POST | `/campaign/{id}/candidates/{candidateId}/invite` | **🔜 C15** `Analyzed → Invited`, bàn giao luồng magic-link (`campaign_invitations`, **D1**) |

> **Rule cứng** cấu hình trên campaign (set khi `Draft`, qua `POST`/`PUT /campaign`): `requiredSkills?` (phải có **ĐỦ**), `keywordsAny?` (có **≥1**), `minYearsExperience?` — lưu cột `required_skills`/`keywords_any`/`min_years_experience`. Chi tiết luồng ở §Business rules.

**Callback nội bộ** (worker → Campaign, **không qua gateway**, header `X-Internal-Token`):
- `POST /internal/campaign-candidates/{candidateId}/cv-result` — lưu kết quả AI lên `campaign_candidates` + `candidate_criterion_scores`; status `Analyzed`. **Idempotent** (xóa điểm cũ rồi ghi lại).
- `POST /internal/campaign-candidates/{candidateId}/cv-failed` — `{ reason }` → status `AnalysisFailed`.

### Request/Response mẫu
```
POST /api/v1/campaign   (JWT Employer)
{ "title":"Tuyển BE Java","domain":"BE","jdText":"…",
  "criteria":[{"name":"Chuyên môn","weight":0.5,"maxScore":5},{"name":"Giao tiếp","weight":0.5,"maxScore":5}],
  "maxCandidates":50,"timeLimitMinutes":30,"antiCheatEnabled":true,
  "startsAt":"2026-07-01T00:00:00Z","expiresAt":"2026-07-15T00:00:00Z",
  "questions":[{"questionText":"…","source":"CustomHr","isRequired":true}] }
→ 201 { "id":"…","status":"Draft", … }

POST /campaign/{id}/publish        → 200 { "status":"Active" }          // sinh campaign_criteria (Σ=1) + audit
POST /campaign/{id}/candidates     (multipart files[]=*.pdf)
→ 202 { "received":12, "rejected":3, "filtered":9 }                     // hard-filter đồng bộ; 9 job AI đẩy queue
GET  /campaign/{id}/candidates?sort=score&minScore=70
→ 200 [ { "candidateId":"…","fullName":"…","overallMatchScore":86,"status":"Analyzed","skills":[…] }, … ]
```

### Validation
| Field | Ràng buộc |
|---|---|
| `title` | bắt buộc, ≤255 |
| `startsAt` | bắt buộc; `expiresAt` (nếu có) > `startsAt` |
| `criteria[]` | `0<weight≤1`, `maxScore≥1`, name không trùng, `Σweight∈[0.99,1.01]`→chuẩn hoá Σ→1 (ngoài→400); sửa khi `Active`→409 |
| `questions[]` | `source∈{AiGenerated,CustomHr}`; publish cần ≥1 câu |
| files (candidates) | PDF, ≤10MB/file; vượt **cap CV/campaign** → 4xx; campaign phải `Active` |
| rule cứng | `required_skills`/`keywords_any`/`min_years_experience` chỉ set khi `Draft` |

### Bảng mã lỗi (đặc thù — chung [../architecture.md](../architecture.md) §6)
| Mã | Khi nào |
|---|---|
| 400 | input sai · file không PDF · `Σweight` ngoài [0.99,1.01] |
| 401/403 | thiếu/sai JWT · non-owner (lọc `employer_id`) · không phải `Employer` |
| 404 | campaign/candidate không tồn tại (hoặc đã soft-delete) |
| 409 | sửa câu hỏi/tiêu chí khi `Active` · publish khi thiếu câu hỏi · transition trạng thái sai |
| 4xx | vượt cap CV/campaign (sàng CV) |

## Luồng (sequence)

**Publish campaign (→ tiêu chí có cấu trúc):**
```
HR ─POST /campaign/{id}/publish─► Campaign (Draft→Active)
   ├─ có criteria[] (HrEdited)? → dùng thẳng (bỏ AI)
   └─ không → AIService /suggest-criteria (Gemini) → campaign_criteria (Σ=1); AI lỗi → fallback mặc định
   └─ ghi audit_logs(Publish)
```

**Lọc CV (tiền sàng — FREE; state machine đầy đủ ở §Business rules):**
```
HR ─upload PDFs─► Campaign ─parse(PdfPig)→hard-filter─► Rejected | Filtered
   Filtered ─publish cv_screening_queue─► AIService worker ─callback cv-result─► Analyzed (+điểm)
   HR ─GET candidates?sort=score─► shortlist ─invite top N─► Invited → D1/D2 magic-link (phỏng vấn = tính credit)
```

**Distribution (🔜 M3):**
```
HR ─POST /campaign/{id}/invitations─► token + email ─► ứng viên mở /invitations/{token}
   ─► Interview create-or-get session(campaign_id) + Campaign reserve credit org ─► phỏng vấn
```

---

## DB — `isas_campaign`
```
campaigns ─┬─1──* campaign_questions
           ├─1──* campaign_criteria          (org_id, employer_id ref lỏng → Auth)
           ├─1──* campaign_candidates ─1──* candidate_criterion_scores  (🔜 sàng CV B2B; criterion_id → campaign_criteria)
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
| required_skills | jsonb? | **🔜 C13** rule cứng sàng CV — kỹ năng **bắt buộc có ĐỦ** trong `cv_parsed_text` |
| keywords_any | jsonb? | **🔜 C13** rule cứng sàng CV — có **≥1** từ khóa |
| min_years_experience | int? | **🔜 C13** rule cứng sàng CV — số năm KN tối thiểu |
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
> ⚠ Đây là ranking **hậu phỏng vấn** (điểm session). **KHÁC** ranking **sàng CV** (`campaign_candidates.overall_match_score` — trước phỏng vấn). Hai luồng tách bạch, không trộn.

### `campaign_candidates` (🔜 C13 — sàng CV B2B; staging + kết quả AI gộp chung)
> 1 dòng / 1 CV ứng viên HR upload. Applicant là **người ngoài (chưa có account)** → lưu `full_name`/`email` parse từ CV, **không** FK sang Auth. **Không** dùng `file_records` (bảng đó của Interview, `user_id` = ứng viên — HR up hộ không khớp).

| Cột | Kiểu | Ràng buộc / ghi chú |
|---|---|---|
| id | uuid | PK (`gen_random_uuid()`) |
| campaign_id | uuid | FK → `campaigns` (Cascade); **index** |
| full_name | varchar(255)? | parse từ CV |
| email | varchar(255)? | parse; **UNIQUE (campaign_id, email)** (bỏ qua khi null) — chống trùng trong campaign |
| cv_file_url | text? | S3 key archival (`campaigns/{id}/candidates/{cid}.pdf`) — **tùy chọn**, null nếu không archive; critical path chỉ cần text |
| cv_parsed_text | text? | text parse từ PDF (PdfPig) — nguồn hard-filter + gửi AI |
| parse_status | varchar(16) | enum: `pending`·`done`·`failed` |
| status | varchar(20) | enum `CandidateStatus` (state machine ở Business rules); **index** |
| reject_reason | text? | lý do hard-filter loại (vd "thiếu kỹ năng: SQL") |
| skills | jsonb? | string[] — AI trả (null tới khi `Analyzed`) |
| years_experience | numeric(4,1)? | AI trả |
| summary | text? | AI trả |
| overall_match_score | int? | **0–100** — AI trả; **`ORDER BY` cột này = ranking shortlist** |
| last_screening_published_at | timestamptz? | cho `StuckScreeningRepublisher` |
| created_at / updated_at | timestamptz | `now()` |

### `candidate_criterion_scores` (🔜 C14 — điểm khớp từng tiêu chí; mẫu `answer_scores`)
| Cột | Kiểu | Ràng buộc / ghi chú |
|---|---|---|
| id | uuid | PK (`gen_random_uuid()`) |
| candidate_id | uuid | FK → `campaign_candidates` (Cascade); **index** |
| criterion_id | uuid | **FK → `campaign_criteria` (Restrict)** — TÁI DÙNG rubric (chặn id rác); **UNIQUE (candidate_id, criterion_id)** |
| match_score | numeric(5,2) | AI chấm, kẹp `[0, max_score]` |
| reasoning | text? | dẫn chứng từ CV |
| created_at | timestamptz | `now()` |

### `audit_logs` — vết thao tác HR
`id` · `org_id` · `actor_user_id` · `action` (`CreateCampaign`/`EditQuestions`/`EditCriteria`/`Publish`/`Delete`/`Reissue`/`ScreenCandidates`…) · `entity` · `entity_id` · `summary`/`diff?` · `at`.

### Index & ràng buộc (tổng hợp)
- **Soft-delete**: `campaigns.deleted_at` + **global query filter** `IS NULL` (mọi query tự ẩn campaign đã xoá).
- **FK on-delete**: Cascade theo `campaign_id` → `campaign_questions` · `campaign_criteria` · `campaign_candidates`. `candidate_criterion_scores` → `campaign_candidates` **Cascade**; → `campaign_criteria` **Restrict** (chặn xoá tiêu chí còn điểm tham chiếu).
- **UNIQUE**: `campaign_criteria(campaign_id, order_no)` + `(campaign_id, name)` · `campaign_candidates(campaign_id, email)` · `candidate_criterion_scores(candidate_id, criterion_id)`.
- **Index**: `campaigns(employer_id, status)` + `(employer_id, created_at)` · `campaign_candidates(campaign_id, status)` · `audit_logs(entity_id, at)`.
- **Ownership**: mọi đọc/ghi lọc `employer_id` (sau wire `org_id` → lọc theo org). Non-owner → 404.

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

### Lọc ứng viên qua CV (B2B) — hybrid filter + state machine + luồng tiền (🔜 C13–C15)
> **1 trong 2 cách lọc của app** (cách kia: phỏng vấn AI — §4.2 [architecture.md](../architecture.md)). Tiền sàng lọc **trước** phỏng vấn: HR đổ hàng loạt CV → shortlist. **Tùy chọn** (có thể mời thẳng không sàng) và **MIỄN PHÍ phase 1** (xem *Luồng tiền* dưới — **D19**). **TÁI DÙNG** `campaign_criteria` (rubric + weight Σ=1) + AIService `/analyze-cv` ([ai.md](ai.md)); **KHÔNG** đụng engine phỏng vấn (không session/answer/audio — `practice_sessions`/`answer_scores` gắn audio nên không tái dùng được).

**Lưu trữ & lọc:**
- Parse CV **tại CampaignService** (`ParserService`/PdfPig sẵn có) → `cv_parsed_text`. Critical path chỉ cần **text**; raw PDF **archival tùy chọn** lên S3 (`IFileService`, key `campaigns/{id}/candidates/{cid}.pdf` → `cv_file_url`) cho HR mở lại — **không** tạo `file_records` (bảng đó của Interview, gắn `user_id` ứng viên; HR up hộ không khớp).
- **Hybrid 2 tầng:** (1) **rule cứng** (`required_skills`/`keywords_any`/`min_years_experience`) chạy **đồng bộ** trên `cv_parsed_text` → rớt = `Rejected` + `reject_reason` (rẻ, **0 cost AI**); (2) **AI chấm khớp** mỗi `Filtered` theo `campaign_criteria` (**async**, [ai.md](ai.md) §queue `cv_screening_queue`) → `criterionMatches` + `overall_match_score`.
- **Ranking = derived:** `ORDER BY overall_match_score DESC` (hoặc `Σ(match_score×weight)` chuẩn hoá — weight `campaign_criteria` Σ=1, **luôn chia Σweight** phòng sai số). **KHÔNG** dùng `campaign_rankings` (read-model điểm **hậu phỏng vấn** từ `SessionScored`, D10 — khác mục đích).

**State machine — Candidate** (`campaign_candidates.status`; `★` = terminal/handoff — kỷ luật như [payment.md](payment.md) §State machine):
```
[POST /candidates] Pending, parse_status=pending
  ├─ parse OK  → HARD-FILTER ─ đủ rule → Filtered
  │                          └ rớt rule → Rejected (reject_reason="thiếu skill: SQL")
  └─ parse FAIL → Rejected (reject_reason="CV không đọc được — upload lại")
Filtered ─(publish cv_screening_queue; set last_screening_published_at)─► Analyzing
Analyzing ─(callback cv-result, X-Internal-Token)──────────────────────► Analyzed
          └─(callback cv-failed / lỗi vĩnh viễn)───────────────────────► AnalysisFailed
Analyzed ─(HR bấm "Mời")───────────────────────────────────────────────► Invited ★ → bàn giao D1/D2 (magic-link)
```
- **Idempotent callback** (`/internal/campaign-candidates/{id}/cv-result`): xóa `candidate_criterion_scores` cũ rồi ghi lại → retry không nhân đôi; `criterion_id` **FK → `campaign_criteria`** chặn id Gemini bịa. Chỉ `Analyzing → Analyzed`.
- **`Invited` hấp thụ (absorbing):** callback đến **muộn** sau khi đã `Invited` → **bỏ qua** (không hạ trạng thái) — như `answer` đã `Scored` thì bỏ `failed` ([interview.md](interview.md)).
- **Recover ngoài thứ tự:** `cv-result` về khi đang `AnalysisFailed` (timeout rồi worker mới callback) → **vẫn ghi điểm + set `Analyzed`** (trừ khi đã `Invited`).
- **Stuck** (`StuckScreeningRepublisher`, mẫu `StuckAnswerRepublisher`): `Filtered` quá hạn mà `last_screening_published_at=null` (publish hụt) **hoặc** `Analyzing` quá hạn không callback (worker mất tích) → đẩy lại job.
- **Retry / Re-upload:** `AnalysisFailed` → HR retry → re-publish → `Analyzing`. `Rejected` do parse → HR upload file mới.
- **Dedup:** `UNIQUE(campaign_id, email)` → trùng email → **bỏ qua + báo "đã tồn tại"** (không tạo row); `email` null (parse không ra) → cho qua, **không** dedup (rủi ro trùng người — chấp nhận phase 1).
- **Guard campaign:** chỉ upload/sàng khi campaign **`Active`** (đã có `campaign_criteria`). `Closed`/`Archived` → **chặn upload mới**; job `Analyzing` in-flight **vẫn cho callback hoàn tất** (bảo vệ in-flight — như `payment.md` không văng người đang thi).
- **Rule cứng cố định tại publish:** set khi `Draft`; sau `Active` khóa (C7). Đổi rule + sàng lại = ngoài phạm vi phase 1.
- **Audit:** mỗi lần upload/sàng ghi `audit_logs` (`action` mới, vd `ScreenCandidates`).
- **Giới hạn đã biết:** parse "số năm KN" từ text thô không chắc → nếu khó, **dời `min_years_experience` sang tầng AI** (`overall_match_score` đã ngầm xét KN).

**Luồng tiền — sàng CV MIỄN PHÍ phase 1 (D19); billing CHỈ ở phỏng vấn thật:**
- Sàng CV = **0 credit**, **KHÔNG** chạm `credit_accounts`/`credit_reservations` ([payment.md](payment.md)). Lý do: `1 credit = 1 lượt phỏng vấn có audio` — trừ credit cho việc *đọc CV* là **sai semantics** + **double-charge** nếu shortlist còn đi phỏng vấn.
- **Billing phát sinh khi `Invited` → ứng viên phỏng vấn thật:** D1/D2 magic-link → create-or-get session (`campaign_id`) → **reserve 1 credit org** → `SessionScored` → **consume** (bỏ ngang → `SessionAbandoned` → release). **NGUYÊN** luồng D7 — sàng CV **không thêm** reserve/consume nào.
- **Chặn đốt chi phí AI (vì free):** (1) **hard-filter TRƯỚC AI** (chỉ `Filtered` gọi Gemini, rớt rule = 0 cost); (2) **cap số CV/campaign** (cấu hình / gắn gói; vượt → **4xx "vượt giới hạn sàng lọc của gói"**); (3) 1 call Gemini/CV, `temperature=0`. Chi phí Gemini/CV = **giá vốn nội bộ**; phase 2 nếu lớn → cân nhắc **loại credit sàng riêng** (D19, team xác nhận lại).

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
