> **Bản sao cho agent** của [`docs/services/interview.md`](../../../docs/services/interview.md) — contract (API + DB + business rules) của InterviewService (engine B2C & B2B). **Source of truth ở `docs/`**: sửa thiết kế tại đó rồi copy lại (đừng sửa bản này lệch). Ràng buộc chung + playbook gen code: [`/AGENTS.md`](../../../AGENTS.md).

> 🔴 **BẢN COPY NÀY ĐÃ LỆCH NẶNG — ĐỌC `docs/services/interview.md` TRƯỚC** (đo 2026-08-08, không phải phỏng đoán):
> - Phần lớn nội dung đóng băng quanh **2026-06-28**; còn **27 marker `🔜` "chưa build"** cho những thứ **ĐÃ ship và đang chạy prod** (BC9 · BC10 · E9–E11 · BC7/`cv_analyses`).
> - **Vắng hẳn** khỏi bản này: `deadline` (I2/BK18) · `outbox_messages` (DB2) · `grounding_refs` (D27) · `practice_questions.kind` (INT-17) · `max_deep_per_question` (INT-17b) · `roadmaps` (BC12–BC15) · `metrics_version` (F11) · `language` (F12/song ngữ) · `needs_review` (E10) · `candidate_id` (BC16).
> - **Chỉ các mục gắn "✅ Chấm theo phạm vi" + §Chấm theo PHẠM VI câu hỏi là đã đồng bộ** (2026-08-08). Mọi mục khác: **tin `docs/`, đừng tin file này**.
> - **§Kho tri thức / Grounding (RAG — D27) KHÔNG hề tồn tại trong bản copy này** ⇒ nhãn trích dẫn + điểm uy tín Context7 chỉ có ở [`docs/services/interview.md`](../../../docs/services/interview.md) §Kho tri thức. Đừng kết luận "chưa build" vì không thấy ở đây.
> - Đây đúng là bẫy *"bản copy lệch"* mà [`/AGENTS.md`](../../../AGENTS.md) cảnh báo. Đồng bộ trọn vẹn = task riêng (WIP=1), **không** làm kèm trong vòng sửa tính năng.

---

# InterviewService — Engine phỏng vấn (dùng chung B2B & B2C)

> **Engine lõi**: session → câu hỏi → ghi âm trả lời → chấm điểm theo rubric/tiêu chí. Tái dùng cho cả luyện tập B2C lẫn bài thi B2B.
> Code: `src/services/Isas.InterviewService`. DB: `isas_interview`. Gateway: `/api/v1/interview`.
> Quy ước chung: [../architecture.md](../architecture.md) §5. AIService: [ai.md](ai.md). Phân việc: [../work-division.md](../work-division.md).

## Vai trò
- Quản lý **session phỏng vấn**, câu hỏi, câu trả lời (audio), điểm, rubric, file CV/JD.
- Gọi **AIService** sinh câu hỏi (đồng bộ) + publish job chấm điểm lên **RabbitMQ**; nhận kết quả qua **callback nội bộ**.
- **Phân biệt B2B/B2C bằng `campaign_id` trên session** (null = B2C luyện tập; có giá trị = bài thi B2B của campaign). Engine + state machine **giữ nguyên** cho cả hai.
- **Danh tính ứng viên:** B2C lấy `candidateId` từ token người luyện; **B2B** vào bằng **magic-link** → provision/login account `Candidate` nhẹ (có `candidate_id` + JWT) → ownership "chủ session" dùng đúng cơ chế cũ.
- **Vào bài B2B — sàng CV là bước TRƯỚC, không thuộc engine này:** ứng viên có thể được mời **thẳng**, hoặc qua **sàng lọc CV** ở CampaignService rồi mới mời (`Invited` → magic-link). **Sàng CV KHÔNG chạm engine phỏng vấn này và KHÔNG tiêu credit** ([campaign.md](campaign.md) §Lọc ứng viên qua CV; **D19**). Từ magic-link trở đi (create-or-get session gắn `campaign_id` → reserve credit org → chấm → consume) = **luồng + state machine + billing NGUYÊN như cũ** — engine không phân biệt ứng viên đã qua sàng CV hay chưa.

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
  audioUrl:     string?                 // URL owner-scoped để phát/tải audio; null khi chưa có file
}

AnswerScoreResponse {
  criterionId:  uuid
  score:        decimal(5,2)
  reasoning:    string?
  rubricVersion: int
}

SessionResultResponse  🔜 {           // BC9 (số liệu) + BC10 (nhận xét) — tổng kết cả buổi (B2C), lưu DB khi Scored
  overallScore:    decimal(5,2)        // BC9 — 0–100, B2C = TRUNG BÌNH CỘNG pct các tiêu chí (equal weight, KHÔNG dùng weight); B2B ranking mới weighted (E4)
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
  weight:       decimal(5,4)           // trọng số rubric — B2C KHÔNG dùng cho overall (lấy trung bình cộng), chỉ hiển thị; B2B mới gộp có trọng số
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

**`GET /sessions/{sessionId}/answers/{answerId}/audio`** — Stream audio câu trả lời của chính candidate; `AnswerResponse.audioUrl` trỏ tới route này. Không có audio/answer/session → **404**; session của người khác → **403**. Không trả SeaweedFS object key.

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
- **`/analyze-cv` nay CHỈ phục vụ B2C.** Sàng CV B2B đã tách sang đường riêng (`suggest-job-needs` + `screen_cv`, vai HR technical screener — [ai.md](ai.md) §Sàng CV B2B): hai dòng khác hẳn bản chất (B2C = nhận xét giúp ứng viên sửa CV; B2B = sàng lọc tuyển dụng), và gộp lại buộc hai khái niệm dùng chung tên field `strengths`. Đường B2C **không đổi một chữ**.

**`GET /cv-analysis/{id}`** → `CvAnalysisResponse` (403/404) · **`GET /cv-analysis`** → `CvAnalysisResponse[]` của user.

### Callback nội bộ (worker → InterviewService) — **không qua gateway**, header `X-Internal-Token`

**`POST /internal/answers/{answerId}/result`** — lưu transcript + điểm → answer `Scored`.
- Req: `{ "transcript": string, "rubricVersion": int, "scores": [{ "criterionId": uuid, "score": number, "reasoning": string? }] }`.
- **Idempotent**: xóa điểm cũ cùng `(attemptNo, rubricVersion)` rồi ghi lại. Res **`200/204`**. Lỗi: **401** (sai token) · **404**.

**`POST /internal/answers/{answerId}/failed`** — đánh dấu `Failed` (lỗi chấm vĩnh viễn).
- Req: `{ "reason": string }`. Nếu answer đã `Scored` → **bỏ qua** (không hạ `Failed`). Res **`200/204`**. Lỗi: **401** · **404**.

### Validation & mã lỗi (tổng hợp — chi tiết per-endpoint ở trên)
| Field | Ràng buộc |
|---|---|
| `cvId`/`jdId` (create session) | optional; `FileRecord` phải **của chính user** + có `parsed_text` (không đọc được → 400) |
| `jobCategory` | bắt buộc, enum `BA·BE·FE` |
| upload file | PDF (cv/jd) **≤10MB** · audio (answer) **≤50MB**; sai loại/size → 400 |
| `questionId`/`durationSec` (answer) | bắt buộc; **1 answer/câu** (upload lại = ghi đè idempotent) |
| callback `/internal/*` | `X-Internal-Token` đúng (sai → 401) |

| Mã | Khi nào (đặc thù — chung [../architecture.md](../architecture.md) §6) |
|---|---|
| 400 | CV/JD không đọc được nội dung · AI trả rỗng · thiếu field · file quá lớn/sai loại |
| 401/403 | thiếu/sai JWT · **không phải chủ** session/file |
| 402 🔜 | hết credit ví (B2C reserve khi tạo session) |
| 404 | session/câu/file không tồn tại |
| 409 | upload answer khi session `Scoring`/`Scored` |
| 502 | AIService lỗi (sinh câu hỏi / analyze-cv) |

## Luồng (sequence)

**Tạo session + sinh câu hỏi (sync AI):**
```
Candidate ─POST /sessions {cvId?,jdId?,jobCategory}─► Interview
   ├─ (B2C 🔜) reserve 1 credit ví cá nhân — hết → 402, KHÔNG tạo session
   ├─ đọc parsed_text(cv/jd) → AIService /generate-questions (sync) → câu hỏi
   └─► 201 session(Ready) + questions[]    (AI lỗi → 502)
```

**Chấm dần + đóng session + phát event:**
```
Candidate ─POST /answers (audio)─► Interview: answer Uploaded → publish ScoringJob → Scoring (câu đầu: session→InProgress)
AIService worker ─callback /internal/answers/{id}/result─► lưu answer_scores (idempotent) → answer Scored
Candidate ─POST /submit─► session Scoring; mọi answer ∈{Scored,Skipped,Failed} → Scored → phát SessionScored
   (publish hụt / worker mất tích → StuckAnswerRepublisher quét 2' đẩy lại)
SessionScored ─RabbitMQ─► Campaign (ranking read-model) + Payment (consume credit)
```

**Phân tích CV B2C (sync, miễn phí — D17):**
```
Candidate ─POST /practice/cv-analysis {cvId,jdId?}─► Interview ─AIService /analyze-cv (sync)─► lưu cv_analyses → 201
```

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
scoring_scope_version int?  ✅ **Chấm theo phạm vi** (migration `AddScoringScopeAndQuestionTargets`) — con dấu thước đo. `null`=KHÔNG BIẾT (row có trước cột; ⚠ BK23: KHÔNG suy ra "khác phiên bản") · `1`=đã biết, chấm đủ rubric (B2B + buổi B2C không câu nào có nhãn) · `2`=đã biết, có ≥1 câu chấm trên tập HẸP HƠN (chỉ giá trị này chứng minh được "khác thước đo" cho BC15/F14/CAMP-10)
```

### `practice_questions`
```
id             uuid          PK
session_id     uuid          FK → practice_sessions (Cascade)
order_no       int
content        text
time_limit_sec int           default 120
target_criterion_ids jsonb?  ✅ **Chấm theo phạm vi** (migration `AddScoringScopeAndQuestionTargets`) — tiêu chí NỘI DUNG câu hỏi này nhắm tới (AIService gắn nhãn lúc sinh). **3 trạng thái load-bearing**: `null`=chưa hỏi/không đủ tin → chấm ĐỦ rubric · `[]`=đã hỏi, câu không nhắm nội dung nào → chỉ 4 tiêu chí CÁCH NÓI · non-empty=cách nói + đúng tiêu chí liệt kê. ⚠ **TUYỆT ĐỐI không quy `[]` về `null`** (làm tính năng NO-OP đúng ở nhóm câu cần nó nhất). Nullable ⇒ né bug jsonb-default F15
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
needs_review              bool          🔜 E10 — true khi spread điểm (self-consistency) > ngưỡng → HR/người luyện xem lại; default false
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
level_matched  int?          🔜 E9 — mức khớp (= score khi neo theo rubric_levels); null nếu chưa neo
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
scoring_scope varchar(24)  ✅ **Chấm theo phạm vi** — NOT NULL DEFAULT `'Always'`, enum lưu string (GEN-2). `Always`=chấm MỌI câu (tiêu chí CÁCH NÓI) · `WhenTargeted`=chỉ chấm khi câu hỏi nhắm tới (tiêu chí NỘI DUNG)
                           index (job_category, version, is_active)
                           CHECK ck_rubric_criteria_scoring_scope: scoring_scope IN ('Always','WhenTargeted')
```
> **Nhận diện nhóm "cách nói" CHỈ được đọc từ cột `scoring_scope`** — KHÔNG khớp theo **tên** (rubric có cả `vi`/`en` F12, candidate tự đặt tên rubric riêng BC16 ⇒ so tên gãy ngay khi đổi một chữ), cũng KHÔNG theo id seed (rubric riêng mang GUID mới).
> **Mặc định `Always` = hành vi trước thay đổi này** ⇒ rubric riêng BC16 · tiêu chí campaign B2B · row cũ đều tự động an toàn. Chiều mặc định an toàn là **chấm thừa**, không phải bỏ chấm.
> **Seed B2C:** 42 row = **18 `WhenTargeted`** (3 tiêu chí nội dung × 3 nghề × 2 ngôn ngữ) + **24 `Always`** (4 tiêu chí cách nói × 3 nghề × 2 ngôn ngữ) ⇒ **7 tiêu chí/nghề/ngôn ngữ**.

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

### Index & ràng buộc (tổng hợp)
- **FK on-delete**: Cascade theo `session_id` → `practice_questions` · `practice_answers` (→ `answer_scores` Cascade) · `session_criterion_scores`. `cv_id`/`jd_id` → `file_records` **Restrict** (chặn xoá file đang gắn session). `answer_scores.criterion_id` → `rubric_criteria` **Restrict**. `rubric_levels`/`rubric_anchors` Cascade.
- **UNIQUE**: `practice_questions(session_id, order_no)` · `practice_answers(session_id, question_id)` (1 answer/câu) · `answer_scores(answer_id, criterion_id, attempt_no)` · `session_criterion_scores(session_id, criterion_id)` · `rubric_levels(criterion_id, score)`.
- **Index**: `practice_sessions(candidate_id)` + `(campaign_id)` · `rubric_criteria(job_category, version, is_active)` · `file_records(user_id)`.
- **Idempotency**: callback `result` xoá điểm cũ cùng `(attempt_no, rubric_version)` rồi ghi lại; `failed` bỏ qua nếu answer đã `Scored` (xem §Idempotency callback).

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
- ✅ **Chấm theo PHẠM VI:** bộ tiêu chí gửi vào job = tiêu chí `Always` (cách nói) + đúng tiêu chí `WhenTargeted` mà **câu hỏi này** nhắm tới (`ScoringScopeFilter.Apply`); câu không có nhãn → nguyên bộ. Spec ở **§Chấm theo PHẠM VI câu hỏi**.

### Republish answer kẹt (`StuckAnswerRepublisher`)
Quét mỗi **2 phút**, chỉ session `InProgress`/`Scoring`, answer có audio:
- `Uploaded` + `last_scoring_published_at=null` quá **2 phút** (CreatedAt) → publish hụt → đẩy lại.
- đã `Scoring` quá **15 phút** không callback → worker mất tích → đẩy lại.
- Đẩy lại OK → set `Scoring` + dời `last_scoring_published_at=now`. Answer `Failed`/`Scored` **không** bị nhặt.
- ⚠ **Phải đi CHUNG luật với đường publish** — projection bắt buộc mang `Question.TargetCriterionIds` rồi gọi cùng `ScoringScopeFilter.Apply`. Thiếu vế này thì answer nào phải nhờ republisher cứu sẽ bị chấm **đủ rubric** trong khi answer chạy trơn tru được chấm đúng phạm vi ⇒ **lệch âm thầm, chỉ xảy ra lúc đã có sự cố** (đúng chỗ **F11** từng dính).

### Idempotency callback
- **result**: xoá điểm cũ cùng `(attemptNo, rubricVersion)` rồi ghi lại → retry không nhân đôi.
- **failed**: nếu answer đã `Scored` → bỏ qua, **không** hạ `Failed`.
- Sau khi lưu → thử đóng session.

### Rubric / tiêu chí & điểm
- **Nguồn tiêu chí tùy mode:** B2C dùng **rubric theo `JobCategory`** (`version` + `is_active`; 1 nghề chung 1 version); **B2B dùng tiêu chí campaign CÓ CẤU TRÚC** — Campaign gửi kèm khi tạo session, Interview materialize thành `rubric_criteria(campaign_id)`. **Pipeline chấm + `answer_scores` giữ NGUYÊN**, chỉ đổi *nguồn tiêu chí* (không chấm trên `criteria_text` thô). **✅ I1:** `PracticeService.CreateCampaignSessionAsync(candidateId, { campaignId, jobCategory, questions[], criteria[] })` → session gắn `campaign_id` + materialize criteria → `rubric_criteria(campaign_id)`, **idempotent theo `campaign_id`** (dùng chung mọi session của campaign). HTTP entry (magic-link/internal) chờ **D2**.
  - **✅ E1 (chọn tiêu chí khi build job chấm):** branch theo `campaign_id` của session — B2B (`campaign_id` có) → tiêu chí `rubric_criteria(campaign_id)`; B2C (`campaign_id` null) → rubric theo `job_category` **VÀ `campaign_id IS NULL`** (criteria campaign cũng mang `job_category` nên phải lọc thêm để không rò sang chấm B2C). Áp ở **cả** publish (`AnswerService.TryPublishScoringJobAsync`) lẫn republish (`StuckAnswerRepublisher`). Message shape + worker Python **KHÔNG đổi** (D9). Kết quả: session B2B `Scored` → `answer_scores.criterion_id` trỏ tiêu chí campaign.
- Worker chấm đủ **mọi** tiêu chí; thiếu → lỗi vĩnh viễn. Điểm **kẹp** `[0, maxScore]`. Bỏ tiêu chí Gemini bịa; chống trùng. `answer_scores` gắn `rubric_version` lúc chấm. Hiển thị: mỗi tiêu chí lấy **attempt mới nhất**.
- **Điểm tổng/session** (khi `Scored`): **B2C = TRUNG BÌNH CỘNG** pct tiêu chí (equal weight — BC9); **B2B = `Σ điểm×weight`** chuẩn hoá (có trọng số — dùng cho ranking E4).
- **🔜 Tổng kết điểm B2C (BC9):** spec đầy đủ ở **§Tổng kết điểm buổi luyện B2C (BC9)** ngay dưới.
- **🔜 Chất lượng & độ nhất quán khi chấm (E9–E11):** neo theo mức (đúng) + đo/chặn chênh lệch (nhất quán) + chuẩn nhận xét — spec ở **§Chất lượng & độ nhất quán khi chấm** dưới. *(⚠ marker `🔜` lỗi thời — E9–E11 đã ship, xem `docs/`.)*
- **✅ Chấm theo PHẠM VI câu hỏi:** không phải câu nào cũng chấm đủ rubric — tiêu chí NỘI DUNG chỉ được chấm khi câu hỏi thực sự nhắm tới (`rubric_criteria.scoring_scope` + `practice_questions.target_criterion_ids`). Spec ở **§Chấm theo PHẠM VI câu hỏi** dưới.

#### Đánh giá cách chấm tiêu chí hiện tại (review 2026-06-28)
**✅ Phần chắc — GIỮ NGUYÊN** (worker `gemini.score()` + callback C# `AnswerService.SaveResultAsync`):
- Worker **kẹp điểm `[0, maxScore]`** mỗi tiêu chí, **bỏ `criterionId` Gemini bịa** (không có trong rubric gửi xuống), **chống trùng**, **bắt buộc đủ mọi tiêu chí** (thiếu → `ValueError` → answer `Failed`). Chấm `temperature=0`.
- C# callback **idempotent** (xoá điểm cũ cùng `(attempt_no, rubric_version)` rồi ghi lại). `answer_scores.criterion_id` là **FK → rubric_criteria** ⇒ chặn id rác.
- Nguồn tiêu chí đúng mode (E1): B2B theo `campaign_id`, B2C theo `job_category` + `campaign_id IS NULL`.

**⚠ Điểm cần lưu ý / gap** (biết trước khi làm BC9/BC10/E4):
1. **`weight` hiện CHƯA được dùng ở đâu.** Lưu trên `rubric_criteria`, gửi xuống worker — nhưng **worker chỉ dùng `maxScore`**, KHÔNG dùng `weight`; và **không có code nào tính điểm tổng**. Điểm tổng mới là **thiết kế**: **B2C = trung bình cộng** (BC9, **KHÔNG** dùng `weight`) · **B2B = Σ điểm×weight** (ranking E4 — **chỉ B2B** mới dùng `weight`). → đừng tưởng đã có điểm tổng.
2. **`maxScore` khác nhau giữa các tiêu chí** ⇒ **KHÔNG cộng điểm thô** (tiêu chí thang cao sẽ lấn). Phải chuẩn theo `maxScore` (percentage) như BC9. `answer_scores.score` là điểm **theo thang riêng** từng tiêu chí.
3. **B2C chưa có nguồn `rubric_criteria` theo `JobCategory`**: repo **không** seed/migration, cũng **không** có endpoint tạo rubric B2C. ⇒ DB trống rubric thì `AnswerService` thấy "không có tiêu chí active" → **bỏ publish → answer không được chấm**. Hiện phải **insert tay**. → **task BC11** (seed/CRUD rubric B2C). *(B2B ổn vì I1 materialize từ campaign.)*
4. **C# callback tin worker 100%** — `SaveResultAsync` lưu nguyên điểm worker gửi, **không tự kẹp / không kiểm đủ tiêu chí** (chỉ FK chặn id lạ). Mà **AIService deploy ephemeral** (docker cp, image có thể lệch — [ai.md](ai.md)) ⇒ nên cân nhắc **guard phía C#** (kẹp `[0,maxScore]`, bỏ criterion ngoài rubric) cho chắc. → **task E8**.
5. **Thiếu 1 tiêu chí → answer `Failed` vĩnh viễn** (worker raise `ValueError`): rubric nhiều tiêu chí dễ gãy oan — đã ghi [ai.md](ai.md) §Vấn đề (🟠 nên retry/self-consistency trước khi chốt Failed).
6. **`attempt_no` luôn = 1** (self-consistency nhiều lần chấm chưa làm) — đúng thiết kế hiện tại; schema đã chừa chỗ.

> **Tóm lại:** chấm **từng tiêu chí trên mỗi câu = ổn & chắc**; phần **tổng hợp mức buổi** (weight/điểm tổng/cần cải thiện) **chưa có** (BC9/BC10/E4) và **rubric B2C chưa có nguồn dữ liệu** (#3) là 2 việc cần làm để luồng B2C chạy trọn.

### Chấm theo PHẠM VI câu hỏi — ✅ (migration `AddScoringScopeAndQuestionTargets`, đã apply prod)
> **Vấn đề gốc:** mọi câu trả lời bị chấm trên **TOÀN BỘ** rubric, nên câu hỏi hẹp ("giải thích cơ chế xoay vòng refresh token") vẫn bị chấm "Thiết kế hệ thống & CSDL" và ăn điểm thấp **chỉ vì không được hỏi**. Đo trên deploy: cùng trình độ, bài trả lời câu hỏi hẹp ~69/100 còn bài "đại luận" 91–97.

**Phân loại (`rubric_criteria.scoring_scope`)** — seed B2C **7 tiêu chí/nghề/ngôn ngữ**: 4 **CÁCH NÓI** `Always` (Giao tiếp & trình bày · Ngữ pháp & dùng từ · Thuật ngữ chuyên ngành · Độ trôi chảy & tự tin) + 3 **NỘI DUNG** `WhenTargeted` (BA: Phân tích yêu cầu · Hiểu nghiệp vụ & stakeholder · Tư duy giải quyết vấn đề — BE: Chiều sâu kỹ thuật · Thiết kế hệ thống & CSDL · Giải quyết vấn đề & thuật toán — FE: Chiều sâu kỹ thuật · Giải quyết vấn đề · Ý thức UI/UX & accessibility).

**Luật lọc — `ScoringScopeFilter.Apply` (`null` ≠ `[]`, điểm sống còn):**

| `practice_questions.target_criterion_ids` | Bộ tiêu chí gửi vào lượt chấm |
|---|---|
| `null` — chưa hỏi / không đủ tin | **NGUYÊN bộ** (lùi an toàn, y như trước thay đổi này) |
| `[]` — đã hỏi, câu không nhắm nội dung nào | **Chỉ tiêu chí `Always`** |
| non-empty | Tiêu chí `Always` **+** đúng những tiêu chí được nhắm |

- ⚠ **Gộp `[]` vào `null` làm tính năng NO-OP đúng ở nhóm câu cần nó nhất** — câu xã giao ("giới thiệu bản thân") vẫn bị chấm "Thiết kế hệ thống & CSDL", chính là hình dạng lỗi mà thay đổi này sinh ra để diệt.
- **LÙI AN TOÀN cuối:** lọc xong mà **rỗng** → trả nguyên bộ (+ log warning). Bộ rỗng KHÔNG vô hại: cả hai caller đều **bỏ publish** khi không có tiêu chí ⇒ answer không bao giờ được chấm ⇒ buổi không đóng ⇒ người luyện **mất 1 credit** (PAY-13). Với tới được khi rubric riêng (BC16) bị sửa **giữa buổi**, hoặc rubric không có tiêu chí `Always` nào.
- **INT-9 KHÔNG phải sửa:** guard "chấm thiếu tiêu chí" so với danh sách **được gửi vào**, không phải toàn rubric ⇒ gửi ít hơn vẫn hợp lệ.
- `RubricVersion` đọc từ bộ **ĐẦY ĐỦ** (không phải bộ đã lọc) ⇒ không đổi theo phạm vi từng câu.

**Nguồn nhãn — `AiServiceQuestionGenerator.ParseTargets`:** AIService trả `targetCriteria[]` theo **index câu**; .NET parse GUID + **drop id lạ** (chỉ nhận id nằm trong tập ta vừa gửi đi) ⇒ AIService không thể bịa tiêu chí ngoài rubric để lái phạm vi chấm. ⚠ **"Toàn id lạ" → `null`, KHÔNG phải `[]`** (nó khẳng định *có* nhắm tiêu chí, chỉ gọi tên thứ không thuộc rubric ⇒ không đủ tin để thu hẹp).

**HAI đường đẩy job chấm dùng CHUNG một luật** — `AnswerService.TryPublishScoringJobAsync` + `StuckAnswerRepublisher` đều gọi `ScoringScopeFilter.Apply`. Lệch nhau = lỗi **chỉ lộ ra khi đã có sự cố** (đúng hạng lỗi **F11**).

**Câu ĐÀO SÂU thừa kế nhãn câu cha** — đúng **do cấu trúc**: `follow_up`/`clarify` theo định nghĩa đào sâu vào chính câu trả lời vừa rồi ⇒ vẫn là chủ đề câu cha; `new_question` ở **chế độ chuỗi** bị chặn từ trước (không append) nên không tới đây, ở **chế độ frontier** (kill-switch `MaxDeepPerQuestion=0`) thì tới được và thừa kế SẼ SAI ⇒ chỉ thừa kế **2 loại đào sâu**, `new_question` để `null`. ⚠ Không thừa kế thì mọi câu đào sâu chấm cả rubric — prod chạy chế độ chuỗi nên **phần lớn câu là câu đào sâu**, thiếu vế này tính năng gần như không có hiệu lực.

**Số câu GỐC bám số tiêu chí NỘI DUNG** (`ComputeSeedCount`, SC1) — ba lực kéo đúng thứ tự: **① ngân sách** (`max_questions` là TỔNG số câu buổi ⇒ chia cho chiều sâu, làm tròn LÊN: 20→5 · 10→3 · 5→2) → **② sàn phủ tiêu chí THẮNG trần `Adaptive:SeedCount`** (tiêu chí không được hỏi bị loại khỏi điểm ⇒ điểm thành "may mắn trúng tủ"; bám con số ĐỘNG vì BC16 cho candidate tự CRUD rubric) → **③ chừa ≥1 khe đào sâu** (`max_questions − 1`, thắng sau cùng). ⚠ **`questionCount` VẪN là "tổng số câu buổi"** (F2b), **KHÔNG** đổi thành `seeds × (1 + đào sâu)`: sẽ vi phạm CHECK `ck_practice_sessions_max_questions_range` ngay lúc INSERT = **SAU `ReserveAsync`** ⇒ đúng lỗi **PAY-5** (mất credit + reservation mồ côi).

**Con dấu `practice_sessions.scoring_scope_version`** — thu hẹp phạm vi làm điểm **không còn so sánh được** với điểm cũ, mà BC15/F14/CAMP-10 đang so thẳng. Đóng theo **sự thật quan sát được** (`Any(q => q.TargetCriterionIds is not null)`, kể cả `[]`), không theo "code đã hỗ trợ nhãn". ⚠ `null` = *không biết*, **KHÔNG suy ra "khác phiên bản"** (BK23).

### Chất lượng & độ nhất quán khi chấm (E9–E11) — 🔜 chưa build
> Mục tiêu: **(1) chấm ĐÚNG mức · (2) chênh lệch mỗi lần/câu chấm NHỎ & ĐO ĐƯỢC · (3) nhận xét CÓ CĂN CỨ.** Áp **cả B2B & B2C**. Phần kẹp/lọc hiện có (review trên) **giữ nguyên** — đây là lớp *đảm bảo đúng*, không thay.

**E9 — Chấm NEO theo mức (levels + anchors).** *(tác động lớn nhất tới (1)+(2))*
- **Vấn đề:** worker hiện chỉ nhận `name/description/maxScore` → AI **tự bịa thang** trong đầu → cùng câu trả lời diễn đạt khác → điểm nhảy; reasoning không bám mức. `rubric_levels`(score→descriptor) + `rubric_anchors`(câu mẫu) **có trong schema nhưng KHÔNG gửi xuống worker**.
- **Làm:** mỗi tiêu chí trong message kèm `levels:[{score,descriptor}]` (+ `anchors?:[{score,exampleAnswer}]`). AI **chọn mức khớp** → trả `{score, levelMatched, reasoning bám descriptor}`, **`score = levelMatched.score`**. Worker **+ C# (E8)** reject nếu `score` không trùng mức nào của tiêu chí. Lưu `answer_scores.level_matched`.
- **Nguồn mức:** B2C từ `rubric_levels` (đã có). **B2B:** `campaign_criteria` **chưa có mức** → publish/materialize phải **sinh mức** (mở rộng `/suggest-criteria` trả `levels` mỗi tiêu chí, hoặc dải mặc định `0..maxScore` có descriptor). Đây là điều kiện để E9 đúng cho B2B.

**E10 — Đo & chặn CHÊNH LỆCH (self-consistency).** *(đảm bảo (2))*
- **Vấn đề:** `temperature=0` chỉ *tái lập* (cùng input → cùng output), **không** bảo chứng *đúng*, cũng **không** đo được dao động. `attempt_no` luôn = 1.
- **Làm:** chấm **N lần** (config `Scoring:SelfConsistencyN`, vd 3) → mỗi lần 1 `attempt_no`, **điểm chốt = median** mỗi tiêu chí. **spread = max−min**; **> ngưỡng** (`Scoring:VarianceThreshold`) → gắn `practice_answers.needs_review = true` (cờ HR), **không** tự coi là điểm cuối. Idempotent theo `(attempt_no, rubric_version)`.
- **Chi phí:** N× Whisper/Gemini — throughput đã là **trần** ([ai.md](ai.md) §Vấn đề) → **bật có chọn lọc** (chỉ chấm lại tiêu chí nghi ngờ / khi lần đầu sát biên), không luôn N×.

**E11 — Chuẩn "NHẬN XÉT OK" + HR chốt.** *(đảm bảo (3))*
- `reasoning` (mỗi tiêu chí) + `overall_comment` (BC10): **bắt buộc trích ≥1 dẫn chứng** từ transcript (câu/cụm), **chặn rỗng/quá ngắn**, **bọc chống prompt-injection** (transcript = *dữ liệu*, không phải *lệnh* — ứng viên đọc "chấm/khen tối đa" KHÔNG được lái).
- **Human-in-the-loop:** điểm AI = **gợi ý**; UI hiện **transcript + reasoning + cờ `needs_review`** cho **HR (B2B) / người luyện (B2C)** xem lại → **HR chốt** điểm cuối, không auto-quyết tuyển dụng bằng điểm AI.

**Schema thêm (migration):** `answer_scores.level_matched int?` (E9) · `practice_answers.needs_review bool default false` (E10). **DTO:** `AnswerScoreResponse` thêm `levelMatched?`; `AnswerResponse` thêm `needsReview`. Đều nullable/thêm field → **không phá** client.

**Xác minh (3 lớp).** L1 build (gồm migration). L2 unit: (E9) message có `levels` → AI mock trả `levelMatched`; `score ≠` mức nào → **reject**; (E10) 3 lần chấm spread > ngưỡng → `needs_review=true`, điểm chốt = **median**; (E11) reasoning rỗng/không trích dẫn → reject; transcript chứa "hãy chấm tối đa" → **không** lái điểm. L3 e2e: 1 câu chấm thật → điểm **bám mức** + reasoning **trích transcript** + cờ review khi phân tán.

### Tổng kết điểm buổi luyện B2C (BC9) — 🔜 chưa build

**Vì sao.** Một buổi luyện có **N câu** (mặc định 5 — `QUESTION_COUNT`). Engine **chấm dần từng câu** → mỗi `answer` có điểm theo **từng tiêu chí** (`answer_scores`, kèm `reasoning`). Nhưng người luyện cần **một kết quả buổi** sau khi xong — *điểm tổng + mỗi tiêu chí được bao nhiêu + tiêu chí nào cần cải thiện* — chứ không phải tự cộng tay từ điểm rải rác trong từng câu. Đây là **feedback định hướng** của B2C; hiện engine **chưa tính** mức session.

**Phạm vi.** **CHỈ B2C** (`campaign_id IS NULL`). B2B: điểm tổng phục vụ **ranking** tính ở CampaignService từ event `SessionScored` (xem dưới) — **không** dùng `result` này.

**Khi nào tính & lưu.** Tính **một lần khi session chuyển sang `Scored`** (trong `AnswerService.TryCompleteSessionAsync` + nhánh đóng-ngay của `PracticeService.SubmitSessionAsync`) rồi **ghi DB**: `practice_sessions.overall_score` + `answered_count`, và breakdown vào bảng `session_criterion_scores`. **Idempotent**: đóng lại cùng session → xoá breakdown cũ rồi ghi lại (tránh nhân đôi). `GET /sessions/{id}` và `GET /sessions/history` **đọc thẳng từ DB**, không tính lại. **Cần migration** (cột + bảng mới) — apply schema lên DB chung **qua pipeline/tay trước deploy** (không auto-migrate).

**Đầu vào.** (1) `answer_scores` — mỗi `(answer, criterion)` lấy **attempt mới nhất** (giống cách hiển thị hiện tại); chỉ tính answer `Scored` (có điểm). (2) `rubric_criteria` của rubric `JobCategory` **active** (`campaign_id IS NULL`) — đúng bộ tiêu chí đã chấm (theo E1): lấy `weight`, `max_score`, `name`.

**Công thức.**
1. Với mỗi tiêu chí `c`: `averageScore_c` = trung bình điểm `c` qua các **answer đã chấm**; `percentage_c = averageScore_c / max_score_c × 100`.
2. **B2C — TRUNG BÌNH CỘNG (equal weight):** `overallScore = ( Σ_c percentage_c ) / K` (K = số tiêu chí đã chấm), **kẹp `[0,100]`**. **KHÔNG** dùng `weight` rubric — mỗi tiêu chí cân bằng (đây là luyện tập, không phải xếp hạng). *(B2B khác: điểm cho ranking = `Σ percentage_c × weight_c` **CÓ trọng số** — E4, xem §Sự kiện phát ra; B2C **không** áp.)*
3. `needs_improvement_c` = `percentage_c < ngưỡng` (mặc định **50%**, cấu hình `Scoring:ImprovementThresholdPct`) — lưu cột `session_criterion_scores.needs_improvement`; API `needsImprovement[]` = các row `= true`. Ngưỡng **chốt lúc tính** (đổi ngưỡng sau **không** hồi tố kết quả đã lưu).
4. `answeredCount` / `totalQuestions`: câu `Skipped`/`Failed`/chưa trả lời **không có** `answer_scores` → **loại khỏi trung bình**; trả 2 con số để biết kết quả tính trên bao nhiêu câu (vd `4/5`).

**Edge cases.**
- `status ≠ Scored` → `result = null` (buổi chưa chốt).
- `answeredCount = 0` (mọi câu Failed/Skipped) → `overallScore = 0`, mỗi `criteriaScores[].percentage = 0`, `needsImprovement` = **tất cả** tiêu chí.
- `K = 0` (không tiêu chí nào được chấm) → `overallScore = 0`, log warning. *(B2C đã dùng equal weight nên không có ca chia `Σweight`.)*
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
