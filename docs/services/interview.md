# InterviewService — Engine phỏng vấn (dùng chung B2B & B2C)

> **Engine lõi**: session → câu hỏi → ghi âm trả lời → chấm điểm theo rubric/tiêu chí. Tái dùng cho cả luyện tập B2C lẫn bài thi B2B.
> Code: `src/services/Isas.InterviewService`. DB: `isas_interview`. Gateway: `/api/v1/interview`.
> Quy ước chung: [../architecture.md](../architecture.md) §5. AIService: [ai.md](ai.md). Phân việc: [../work-division.md](../work-division.md).

## Vai trò
- Quản lý **session phỏng vấn**, câu hỏi, câu trả lời (audio), điểm, rubric, file CV/JD.
- Gọi **AIService** sinh câu hỏi (đồng bộ) + publish job chấm điểm lên **RabbitMQ**; nhận kết quả qua **callback nội bộ**.
- **Phân biệt B2B/B2C bằng `campaign_id` trên session** (null = B2C luyện tập; có giá trị = bài thi B2B của campaign). Engine + state machine **giữ nguyên** cho cả hai.
- **Danh tính ứng viên:** B2C lấy `candidateId` từ token người luyện; **B2B** vào bằng **magic-link** → provision/login account `Candidate` nhẹ (có `candidate_id` + JWT) → ownership "chủ session" dùng đúng cơ chế cũ.

---

## API — `/api/v1/interview`

> **Quy ước:** Base public `/api/v1/interview/*` (gateway StripPrefix → service `/api/practice/*`, `/api/files/*`). **Auth: JWT Bearer** (validate offline); `candidateId` = claim `sub`; chỉ **chủ** session/file thao tác được. Callback nội bộ dùng header **`X-Internal-Token`**, **KHÔNG** qua gateway. **Kiểu dữ liệu:** `uuid` · `string` · `int` · `long` (bytes) · `decimal(p,s)` · `bool` · `datetime` (ISO-8601, `timestamptz`) · `enum` lưu **string** · `T[]` = mảng · `{…}` = object lồng · `?` = optional/nullable. Mã lỗi chung: [../architecture.md](../architecture.md) §6. *(🔜 = endpoint/field thuộc phần chưa build.)*

### Schemas (DTO)

```
PracticeSessionResponse {
  id:           uuid
  status:       enum(string)            // GeneratingQuestions·Ready·InProgress·Scoring·Scored·Failed
  jobCategory:  enum(string)            // BA·BE·FE
  cvId:         uuid?                   // file CV đính kèm
  jdId:         uuid?                   // file JD đính kèm
  createdAt:    datetime
  completedAt:  datetime?               // set khi submit
  questions:    QuestionResponse[]
  result:       SessionResultResponse?  // 🔜 BC9 — chỉ khi status=Scored & campaign_id=null (B2C); null nếu chưa chấm xong
}

QuestionResponse {
  id:           uuid
  orderNo:      int
  content:      string
  timeLimitSec: int
  answer:       AnswerResponse?         // null nếu chưa trả lời
}

AnswerResponse {
  id:           uuid
  status:       enum(string)            // Uploaded·Scoring·Scored·Failed·Skipped
  durationSec:  int
  transcript:   string?
  scores:       AnswerScoreResponse[]
}

AnswerScoreResponse {
  criterionId:  uuid
  score:        decimal(5,2)
  reasoning:    string?
  rubricVersion: int
}

SessionResultResponse  🔜 {           // BC9 (số liệu) + BC10 (nhận xét) — tổng kết cả buổi (B2C), lưu DB khi Scored
  overallScore:    decimal(5,2)        // BC9 — 0–100, Σ(pct_tiêu_chí × weight)/Σweight × 100
  answeredCount:   int                 // BC9 — số câu đã chấm (có scores) — kết quả tính trên bấy nhiêu câu
  totalQuestions:  int                 // BC9 — tổng số câu của buổi (vd 5)
  criteriaScores:  CriterionScoreResponse[]   // BC9 — mỗi tiêu chí được bao nhiêu điểm
  needsImprovement: uuid[]             // BC9 — criterionId của tiêu chí dưới ngưỡng (yếu → ưu tiên cải thiện)
  overallComment:  string?             // 🔜 BC10 — NHẬN XÉT CHUNG cả buổi (AI sinh); null nếu chưa sinh / AI lỗi
}

CriterionScoreResponse  🔜 {           // BC9 — điểm "mỗi trường tiêu chí cỡ nhiêu điểm"
  criterionId:  uuid
  name:         string                 // tên tiêu chí (vd "Giao tiếp", "Chiều sâu kỹ thuật")
  averageScore: decimal(5,2)           // điểm ĐẠT ĐƯỢC của tiêu chí (TB qua các câu đã chấm)
  maxScore:     int                    // điểm TỐI ĐA của tiêu chí → hiển thị "averageScore/maxScore"
  percentage:   decimal(5,2)           // averageScore / maxScore × 100 (0–100)
  weight:       decimal(5,4)           // trọng số khi gộp vào overallScore
}

PracticeSessionSummary {
  id:           uuid
  status:       enum(string)
  jobCategory:  enum(string)
  createdAt:    datetime
  completedAt:  datetime?
  overallScore: decimal(5,2)?           // 🔜 BC9 — điểm tổng 0–100 nếu đã Scored (B2C); null nếu chưa → list lịch sử hiện điểm
}

FileRecord {
  id:           uuid
  fileType:     enum(string)            // cv·jd·answer-audio
  originalName: string
  mimeType:     string
  fileSize:     long                    // bytes
  parseStatus:  enum(string)            // pending·done·failed
  createdAt:    datetime
}

CvAnalysisResponse  🔜 {
  id:           uuid
  cvId:         uuid
  jdId:         uuid?
  jobCategory:  enum(string)
  summary:      string
  strengths:    string[]
  weaknesses:   string[]
  suggestions:  string[]
  jdMatch: {                            // chỉ khi request có jdId
    score:         int                  // 0–100
    matchedSkills: string[]
    missingSkills: string[]
  }?
  createdAt:    datetime
}
```

### Practice — `/api/v1/interview/practice/sessions` (JWT Candidate)

**`POST /sessions`** — Tạo session + sinh câu hỏi (gọi AI đồng bộ).
- Req `application/json`: `{ "cvId": uuid?, "jdId": uuid?, "jobCategory": "BA"|"BE"|"FE" }` — `cvId`/`jdId` optional (parse sẵn ở Files); `jobCategory` **bắt buộc**.
- 🔜 *B2C:* trước khi gọi AI → **reserve 1 credit ví cá nhân**; hết → **402** (không tạo session).
- Res **`201`** `PracticeSessionResponse` (`status="Ready"`, `questions` đã sinh):
```json
{ "id":"…","status":"Ready","jobCategory":"BE","cvId":"…","jdId":null,
  "createdAt":"2026-06-27T03:00:00Z","completedAt":null,
  "questions":[{"id":"…","orderNo":1,"content":"…","timeLimitSec":120,"answer":null}] }
```
- Lỗi: **400** (CV/JD không đọc được nội dung · AI trả rỗng) · **401** · **402** 🔜 (hết credit) · **502** (AIService lỗi).

**`GET /sessions/history`** — Lịch sử của chính user. Res **`200`** `PracticeSessionSummary[]` (mới nhất trước; 🔜 BC9: kèm `overallScore` nếu buổi đã `Scored`). Lỗi: **401**.

**`GET /sessions/{sessionId}`** — Chi tiết (câu hỏi + bài nộp + điểm). Res **`200`** `PracticeSessionResponse` (mỗi câu kèm `answer` + `scores` nếu có; 🔜 BC9: kèm `result` tổng kết khi `status=Scored` & B2C). Lỗi: **401** · **403** (không phải buổi của bạn) · **404**.

**`POST /sessions/{sessionId}/submit`** — Chốt sổ (đi chấm nốt). Res **`204`**. Lỗi: **400** (chưa trả lời câu nào · trạng thái không cho submit) · **401** · **403** · **404**.

**`POST /sessions/{sessionId}/answers`** — Upload audio trả lời.
- Req `multipart/form-data`: `questionId: uuid` · `file: audio ≤50MB` · `durationSec: int`.
- **Idempotent**: upload lại cùng `questionId` = ghi đè (reset transcript, publish lại chấm).
- Res **`200/201`** `AnswerResponse` (`status="Uploaded"` → publish → `Scoring`). Answer đầu tiên: session `Ready→InProgress`.
- Lỗi: **400** (thiếu field · file quá lớn) · **401** · **403** · **404** (session/câu không có) · **409** (session đã `Scoring`/`Scored`).

### Files — `/api/v1/interview/files` (JWT) — chỉ `.pdf`, `fileType ∈ {cv,jd}`

**`POST /upload?fileType=cv|jd`** — Upload PDF (≤10MB) + parse text.
- Req `multipart/form-data`: `file: pdf`. Query `fileType`.
- Res **`201`** `FileRecord` (`parseStatus` = `done`/`pending`). Lỗi: **400** (không phải PDF · quá lớn · `fileType` sai) · **401**.

**`GET /{id}`** → `FileRecord` · **`GET /{id}/download`** → bytes (`Content-Type` theo mime; **404** nếu thiếu) · **`GET /{id}/parsed-text`** → `{ id, parsedText, parseStatus }`.
**`GET /files`** → `FileRecord[]` của user.
**`PUT /{id}`** (multipart, thay file) → `FileRecord` mới · **`DELETE /{id}`** → **`204`** (xóa record + key S3).
Lỗi chung Files: **401** · **403** (không phải file của bạn) · **404**.

### CV Analysis — `/api/v1/interview/practice/cv-analysis` (JWT) — **B2C BC4, 🔜 chưa build**

**`POST /cv-analysis`** — Phân tích CV (parse → AIService `/analyze-cv` đồng bộ → lưu `cv_analyses`).
- Req `application/json`: `{ "cvId": uuid, "jdId": uuid? }`. Có `jdId` → kết quả thêm `jdMatch`.
- Res **`201`** `CvAnalysisResponse`. Lỗi: **400** (CV không đọc được) · **401** · **403** (không phải file của bạn) · **404** (`cvId`/`jdId` không có) · **502** (AI lỗi).
- **Đồng bộ HTTP**, không qua RabbitMQ. **Miễn phí (không trừ credit) phase 1** (D17). Mục (c) "CV vs câu trả lời" sau khi `Scored` = task `BC8`.

**`GET /cv-analysis/{id}`** → `CvAnalysisResponse` (403/404) · **`GET /cv-analysis`** → `CvAnalysisResponse[]` của user.

### Callback nội bộ (worker → InterviewService) — **không qua gateway**, header `X-Internal-Token`

**`POST /internal/answers/{answerId}/result`** — lưu transcript + điểm → answer `Scored`.
- Req: `{ "transcript": string, "rubricVersion": int, "scores": [{ "criterionId": uuid, "score": number, "reasoning": string? }] }`.
- **Idempotent**: xóa điểm cũ cùng `(attemptNo, rubricVersion)` rồi ghi lại. Res **`200/204`**. Lỗi: **401** (sai token) · **404**.

**`POST /internal/answers/{answerId}/failed`** — đánh dấu `Failed` (lỗi chấm vĩnh viễn).
- Req: `{ "reason": string }`. Nếu answer đã `Scored` → **bỏ qua** (không hạ `Failed`). Res **`200/204`**. Lỗi: **401** · **404**.

---

## DB — `isas_interview`
```
practice_sessions 1──* practice_questions 1──1 practice_answers 1──* answer_scores
        │                                                              │
        ├──*? file_records (cv_id, jd_id)            rubric_criteria ──┘
        └──* session_criterion_scores (🔜 BC9, B2C)
rubric_criteria 1──* rubric_levels 1──* rubric_anchors
```

> **Quy ước kiểu DB:** `uuid` · `varchar(n)` · `text` · `int` · `bigint` · `numeric(p,s)` · `bool` · `timestamptz` · `jsonb` · enum lưu **string**. `?` = nullable. Cột **snake_case**.

### `practice_sessions`
```
id            uuid          PK
candidate_id  uuid          NOT NULL, index; ref lỏng → Auth
campaign_id   uuid?         null=B2C · có=bài thi campaign (ref lỏng → Campaign)
cv_id         uuid?         FK → file_records (Restrict)
jd_id         uuid?         FK → file_records (Restrict)
job_category  varchar(8)    enum: BA·BE·FE
status        varchar(32)   enum SessionStatus (state machine bên dưới)
created_at    timestamptz   NOT NULL
completed_at  timestamptz?  set khi submit
overall_score numeric(5,2)? 🔜 BC9 — điểm tổng 0–100, set khi `Scored` (B2C); null khi chưa/B2B
answered_count int?         🔜 BC9 — số câu đã chấm lúc tính kết quả (snapshot)
overall_comment text?       🔜 BC10 — nhận xét chung (AI sinh khi `Scored`, best-effort); null nếu chưa/AI lỗi/B2B
```

### `practice_questions`
```
id             uuid          PK
session_id     uuid          FK → practice_sessions (Cascade)
order_no       int
content        text
time_limit_sec int           default 120
created_at     timestamptz
                             UNIQUE (session_id, order_no)
```

### `practice_answers`
```
id                        uuid          PK (= fileId audio)
session_id                uuid          FK → practice_sessions (Cascade)
question_id               uuid          FK → practice_questions (Restrict), 1–1
audio_object_key          varchar       key SeaweedFS
transcript                text?
status                    varchar(32)   enum AnswerStatus
duration_sec              int
last_scoring_published_at timestamptz?
created_at                timestamptz
                                        UNIQUE (session_id, question_id) — tối đa 1 answer/câu
```

### `answer_scores`
```
id             uuid          PK
answer_id      uuid          FK → practice_answers (Cascade)
criterion_id   uuid          FK → rubric_criteria (Restrict)
attempt_no     int           default 1
score          numeric(5,2)
reasoning      text?
rubric_version int
created_at     timestamptz
                             UNIQUE (answer_id, criterion_id, attempt_no)
```

### `session_criterion_scores` — 🔜 BC9 (B2C; ghi khi session `Scored`)
```
id                uuid          PK
session_id        uuid          FK → practice_sessions (Cascade)
criterion_id      uuid          FK → rubric_criteria (Restrict)
criterion_name    varchar       snapshot tên tiêu chí lúc tính (rubric có thể đổi version)
average_score     numeric(5,2)  điểm TB tiêu chí qua các câu đã chấm
max_score         int           snapshot maxScore
percentage        numeric(5,2)  average_score / max_score × 100 (0–100)
weight            numeric(5,4)  snapshot weight
needs_improvement bool          percentage < ngưỡng (mặc định 50%)
created_at        timestamptz
                                UNIQUE (session_id, criterion_id)
```
> Điểm tổng buổi nằm ở `practice_sessions.overall_score` (+ `answered_count`); breakdown mỗi tiêu chí ở bảng này. `needsImprovement[]` = các row `needs_improvement = true`. **Chỉ B2C** (B2B không ghi).

### `rubric_criteria`
```
id           uuid          PK
name         varchar
description  text?
weight       numeric(5,4)
max_score    int
is_active    bool
job_category varchar(8)    enum: BA·BE·FE
campaign_id  uuid?         B2B: tiêu chí theo campaign thay job_category · null=rubric B2C
version      int
                           index (job_category, version, is_active)
```

### `rubric_levels`
```
id           uuid   PK
criterion_id uuid   FK → rubric_criteria (Cascade)
score        int    0..max_score
descriptor   text
                    UNIQUE (criterion_id, score)
```

### `rubric_anchors`
```
id             uuid   PK
level_id       uuid   FK → rubric_levels (Cascade)
example_answer text
```

### `file_records`
```
id             uuid          PK
user_id        uuid          ref lỏng → Auth
file_type      varchar(16)   enum: cv·jd·answer-audio
original_name  varchar
storage_path   varchar       key SeaweedFS (KHÔNG lưu full URL)
storage_bucket varchar       isas-files
mime_type      varchar
file_size      bigint        bytes
parsed_text    text?
parse_status   varchar(16)   enum: pending·done·failed
created_at     timestamptz
updated_at     timestamptz
```

### `cv_analyses` — **B2C BC4, 🔜 chưa build** (D17)
```
id           uuid          PK
candidate_id uuid          index; ref lỏng → Auth
cv_id        uuid          FK → file_records (Restrict)
jd_id        uuid?         FK → file_records
job_category varchar(8)    enum: BA·BE·FE
summary      text
strengths    jsonb         string[]
weaknesses   jsonb         string[]
suggestions  jsonb         string[]
jd_match     jsonb?        { score, matchedSkills[], missingSkills[] } — chỉ khi có jd_id
created_at   timestamptz
```
AIService trả kết quả → InterviewService **lưu ở đây** (AI không ghi DB).

---

## Business rules

### State machine — Session
```
GeneratingQuestions ──► Ready ──► InProgress ──► Scoring ──► Scored
        └──► Failed (sinh câu hỏi lỗi)
```
- Chỉ `Ready`/`InProgress` mới **submit** được; phải có ≥1 answer.
- Submit → `Scoring` + `CompletedAt`. Nếu mọi answer đã xong → đóng thẳng `Scored`.
- Đóng `Scored` khi đang `Scoring` **và** mọi answer ∈ {Scored, Skipped, Failed}.
- `Completed` có trong enum nhưng **không dùng**.
- **B2B — chống reservation treo:** session `InProgress` quá `expires_at`/time-limit → **auto-submit** (có ≥1 answer → đi `Scoring`→`Scored` → consume credit) hoặc **0 answer → `SessionAbandoned`** (release credit). **Resume**: mở lại token chỉ cho làm **các câu CHƯA nộp** (answer 1-per-question, câu đã nộp giữ nguyên).

### State machine — Answer
```
Uploaded ──(publish OK)──► Scoring ──(result)──► Scored
   │                          └──(failed)──► Failed
   └─(publish hụt)→ giữ Uploaded → republisher đẩy lại
```
`Skipped` dự phòng; `Transcribing`/`Transcribed` có trong enum nhưng không dùng.

### Upload câu trả lời
- **Tối đa 1 answer/câu** (unique `session_id+question_id`); upload lại = ghi đè (idempotent, `fileId=answerId`, cùng key).
- Upload lại reset `transcript=null`, `status=Uploaded`, publish lại.
- Answer đầu tiên: session `Ready → InProgress`.
- Không upload khi session `Completed`/`Scoring`/`Scored`. Chỉ chủ session (`candidateId` khớp).

### Chấm điểm dần (incremental scoring)
- Publish job chấm **ngay khi upload** (không đợi submit) → trải tải.
- Submit **không** publish lại (tránh chấm trùng).
- Publish lỗi **không** hỏng upload — republisher xử lý sau.
- Mỗi job kèm **rubric active** của `JobCategory` + `RubricVersion`. Không có rubric active → bỏ publish (log warning).

### Republish answer kẹt (`StuckAnswerRepublisher`)
Quét mỗi **2 phút**, chỉ session `InProgress`/`Scoring`, answer có audio:
- `Uploaded` + `last_scoring_published_at=null` quá **2 phút** (CreatedAt) → publish hụt → đẩy lại.
- đã `Scoring` quá **15 phút** không callback → worker mất tích → đẩy lại.
- Đẩy lại OK → set `Scoring` + dời `last_scoring_published_at=now`. Answer `Failed`/`Scored` **không** bị nhặt.

### Idempotency callback
- **result**: xoá điểm cũ cùng `(attemptNo, rubricVersion)` rồi ghi lại → retry không nhân đôi.
- **failed**: nếu answer đã `Scored` → bỏ qua, **không** hạ `Failed`.
- Sau khi lưu → thử đóng session.

### Rubric / tiêu chí & điểm
- **Nguồn tiêu chí tùy mode:** B2C dùng **rubric theo `JobCategory`** (`version` + `is_active`; 1 nghề chung 1 version); **B2B dùng tiêu chí campaign CÓ CẤU TRÚC** — Campaign gửi kèm khi tạo session, Interview materialize thành `rubric_criteria(campaign_id)`. **Pipeline chấm + `answer_scores` giữ NGUYÊN**, chỉ đổi *nguồn tiêu chí* (không chấm trên `criteria_text` thô). **✅ I1:** `PracticeService.CreateCampaignSessionAsync(candidateId, { campaignId, jobCategory, questions[], criteria[] })` → session gắn `campaign_id` + materialize criteria → `rubric_criteria(campaign_id)`, **idempotent theo `campaign_id`** (dùng chung mọi session của campaign). HTTP entry (magic-link/internal) chờ **D2**.
  - **✅ E1 (chọn tiêu chí khi build job chấm):** branch theo `campaign_id` của session — B2B (`campaign_id` có) → tiêu chí `rubric_criteria(campaign_id)`; B2C (`campaign_id` null) → rubric theo `job_category` **VÀ `campaign_id IS NULL`** (criteria campaign cũng mang `job_category` nên phải lọc thêm để không rò sang chấm B2C). Áp ở **cả** publish (`AnswerService.TryPublishScoringJobAsync`) lẫn republish (`StuckAnswerRepublisher`). Message shape + worker Python **KHÔNG đổi** (D9). Kết quả: session B2B `Scored` → `answer_scores.criterion_id` trỏ tiêu chí campaign.
- Worker chấm đủ **mọi** tiêu chí; thiếu → lỗi vĩnh viễn. Điểm **kẹp** `[0, maxScore]`. Bỏ tiêu chí Gemini bịa; chống trùng. `answer_scores` gắn `rubric_version` lúc chấm. Hiển thị: mỗi tiêu chí lấy **attempt mới nhất**.
- **Điểm tổng/session** (khi `Scored`) = `Σ điểm_tiêu_chí × weight` chuẩn hóa — dùng cho ranking.
- **🔜 Tổng kết điểm B2C (BC9):** spec đầy đủ ở **§Tổng kết điểm buổi luyện B2C (BC9)** ngay dưới.

### Tổng kết điểm buổi luyện B2C (BC9) — 🔜 chưa build

**Vì sao.** Một buổi luyện có **N câu** (mặc định 5 — `QUESTION_COUNT`). Engine **chấm dần từng câu** → mỗi `answer` có điểm theo **từng tiêu chí** (`answer_scores`, kèm `reasoning`). Nhưng người luyện cần **một kết quả buổi** sau khi xong — *điểm tổng + mỗi tiêu chí được bao nhiêu + tiêu chí nào cần cải thiện* — chứ không phải tự cộng tay từ điểm rải rác trong từng câu. Đây là **feedback định hướng** của B2C; hiện engine **chưa tính** mức session.

**Phạm vi.** **CHỈ B2C** (`campaign_id IS NULL`). B2B: điểm tổng phục vụ **ranking** tính ở CampaignService từ event `SessionScored` (xem dưới) — **không** dùng `result` này.

**Khi nào tính & lưu.** Tính **một lần khi session chuyển sang `Scored`** (trong `AnswerService.TryCompleteSessionAsync` + nhánh đóng-ngay của `PracticeService.SubmitSessionAsync`) rồi **ghi DB**: `practice_sessions.overall_score` + `answered_count`, và breakdown vào bảng `session_criterion_scores`. **Idempotent**: đóng lại cùng session → xoá breakdown cũ rồi ghi lại (tránh nhân đôi). `GET /sessions/{id}` và `GET /sessions/history` **đọc thẳng từ DB**, không tính lại. **Cần migration** (cột + bảng mới) — apply schema lên DB chung **qua pipeline/tay trước deploy** (không auto-migrate).

**Đầu vào.** (1) `answer_scores` — mỗi `(answer, criterion)` lấy **attempt mới nhất** (giống cách hiển thị hiện tại); chỉ tính answer `Scored` (có điểm). (2) `rubric_criteria` của rubric `JobCategory` **active** (`campaign_id IS NULL`) — đúng bộ tiêu chí đã chấm (theo E1): lấy `weight`, `max_score`, `name`.

**Công thức.**
1. Với mỗi tiêu chí `c`: `averageScore_c` = trung bình điểm `c` qua các **answer đã chấm**; `percentage_c = averageScore_c / max_score_c × 100`.
2. `overallScore = ( Σ_c percentage_c × weight_c ) / ( Σ_c weight_c )` — chuẩn hoá theo `Σweight` (không phụ thuộc Σweight=1), **kẹp `[0,100]`**.
3. `needs_improvement_c` = `percentage_c < ngưỡng` (mặc định **50%**, cấu hình `Scoring:ImprovementThresholdPct`) — lưu cột `session_criterion_scores.needs_improvement`; API `needsImprovement[]` = các row `= true`. Ngưỡng **chốt lúc tính** (đổi ngưỡng sau **không** hồi tố kết quả đã lưu).
4. `answeredCount` / `totalQuestions`: câu `Skipped`/`Failed`/chưa trả lời **không có** `answer_scores` → **loại khỏi trung bình**; trả 2 con số để biết kết quả tính trên bao nhiêu câu (vd `4/5`).

**Edge cases.**
- `status ≠ Scored` → `result = null` (buổi chưa chốt).
- `answeredCount = 0` (mọi câu Failed/Skipped) → `overallScore = 0`, mỗi `criteriaScores[].percentage = 0`, `needsImprovement` = **tất cả** tiêu chí.
- `Σweight = 0` (dữ liệu rubric lỗi) → fallback **trọng số đều** `1/K` để tránh chia 0; log warning.
- Điểm đã **kẹp `[0, maxScore]`** ở callback chấm → không âm/vượt trần.
- Session B2B (`campaign_id` có) → **không** tính/ghi (không áp BC9); `result = null`.
- **Chấm lại sau khi đã `Scored`** (callback đến muộn — hiếm): kết quả đã lưu **không tự cập nhật** (đóng session chỉ chạy 1 lần). Ngoài phạm vi BC9; cần thì backfill/recompute thủ công.

**Ảnh hưởng (impact).**
- **DB:** **migration mới** — thêm cột `practice_sessions.overall_score`/`answered_count` + bảng `session_criterion_scores` (xem §DB). Apply qua pipeline/tay (không auto-migrate).
- **DTO:** `PracticeSessionResponse` thêm `result?` + `PracticeSessionSummary` thêm `overallScore?`, **đọc từ** `practice_sessions`/`session_criterion_scores`. Đều **nullable, thêm field** → **không phá** client cũ.
- **Code:** hàm tính+ghi (vd `ComputeAndStoreSessionResult(...)`) gọi tại **điểm đóng session → `Scored`** (`AnswerService.TryCompleteSessionAsync` + `SubmitSessionAsync`), **chỉ khi** `campaign_id = null`; `GetSessionAsync`/`GetHistoryAsync` chỉ **đọc**. **Không** AI, **không** infra mới.
- **FE:** màn kết quả buổi luyện hiển thị điểm tổng + bảng điểm/tiêu chí + danh sách "cần cải thiện"; list lịch sử hiện điểm tổng.
- **Liên quan:** **BC8** ("CV vs câu trả lời") dùng lại `result` này. **B2B KHÔNG ảnh hưởng** (ranking tính riêng ở Campaign).

**Xác minh (3 lớp).** L1 `dotnet build` (gồm migration). L2 `dotnet test` — unit test: đóng session B2C → `Scored` (2 tiêu chí weight 0.4/0.6, maxScore 5, chấm nhiều câu) → DB có `practice_sessions.overall_score` khớp tính tay + rows `session_criterion_scores` đúng (`percentage`, `needs_improvement`); **đóng lại lần 2 → không nhân đôi** row; session B2B → **không** ghi; `GET /sessions/{id}` trả `result` đọc từ DB; history có `overallScore`. L3 e2e: luyện B2C 5 câu → chấm xong → DB lưu kết quả + `GET` đọc đúng.

### Nhận xét chung buổi luyện B2C (BC10) — 🔜 chưa build

**Vì sao.** Số liệu BC9 cho *điểm*; người luyện còn cần **nhận xét chung bằng lời** cho cả buổi (tổng quan làm tốt/chưa tốt ở đâu + hướng cải thiện) — giá trị định hướng của B2C. Sinh bằng **AI (Gemini)** nên **tách khỏi BC9** (BC9 giữ thuần engine, không AI).

**Phạm vi.** CHỈ B2C. Phụ thuộc **BC9** (cần số liệu tổng kết) + endpoint AIService mới.

**AIService (sync, không ghi DB — theo D17).** Endpoint mới `POST /summarize-session` (xem [ai.md](ai.md)): req `{ jobCategory, overallScore, criteriaScores:[{ name, percentage, needsImprovement }] }` → res `{ overallComment }` (text tiếng Việt, vài câu). Bọc nội dung ứng viên trong delimiter (chống prompt-injection — ai.md). InterviewService nhận kết quả rồi **tự lưu** (AI không ghi DB).

**Khi nào sinh & lưu.** Sau khi BC9 ghi số liệu lúc đóng `Scored` (B2C) → InterviewService gọi AIService **best-effort** → lưu `practice_sessions.overall_comment`. **AI lỗi KHÔNG chặn `Scored`**: để `overall_comment = null` + log; có thể **lazy/backfill** (sinh lại khi `GET` thấy null) — không bắt buộc trong BC10.

**DB / DTO.** Cột `practice_sessions.overall_comment text?` (migration BC10) · DTO `SessionResultResponse.overallComment string?`.

**Ảnh hưởng.** Phá tính "không AI" của BC9 (lý do tách BC10) · thêm **endpoint AIService** + **1 call đồng bộ best-effort** lúc đóng session · thêm 1 cột (migration) · chi phí Gemini/buổi. **Không** infra mới (HTTP sync như `generate-questions`/`suggest-criteria`/`analyze-cv`).

**Edge cases.** AI lỗi/timeout → `overall_comment=null`, `Scored` vẫn thành công. Session B2B → không sinh. Chưa `Scored` → null.

**Xác minh (3 lớp).** L1 build (gồm migration). L2 test: AIService `/summarize-session` trả `overallComment` từ input số liệu (mock); InterviewService đóng B2C `Scored` → gọi AI (mock) → lưu `overall_comment`; AI ném lỗi → `Scored` vẫn xong + `overall_comment=null`. L3 e2e: luyện B2C → chấm xong → `GET /sessions/{id}` có `result.overallComment` (AIService thật).

### Sự kiện phát ra (RabbitMQ)
Khi session đóng, engine phát event để service khác phản ứng (event-driven, tránh Campaign gọi HTTP đọc điểm mỗi lần):
| Event | Khi nào | Ai nghe |
|---|---|---|
| `SessionScored` | session `Scored` (kèm `campaign_id`, `candidate_id`, điểm tổng) | **Campaign** (cập nhật ranking read-model) · **Payment** (consume credit) |
| `SessionAbandoned` | session bỏ ngang quá hạn / 0 answer | **Payment** (release reservation) |

- **Credit:** Campaign **reserve** 1 credit của org khi ứng viên bắt đầu; engine phát `SessionScored` → **consume**, `SessionAbandoned` → **release** (chi tiết [payment.md](payment.md)).
- Giữ 1 endpoint HTTP **backfill** làm fallback nếu miss event.

### Xác thực
- Endpoint user: **JWT Bearer**, validate bằng chung `Jwt:Key/Issuer/Audience`.
- Callback `/internal/...`: `AllowAnonymous` + header `X-Internal-Token`.
