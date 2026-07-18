# InterviewService — Engine phỏng vấn (dùng chung B2B & B2C)

> **Engine lõi**: session → câu hỏi → ghi âm trả lời → chấm điểm theo rubric/tiêu chí. Tái dùng cho cả luyện tập B2C lẫn bài thi B2B.
> Code: `src/services/Isas.InterviewService`. DB: `isas_interview`. Gateway: `/api/v1/interview`.
> Quy ước chung: [../architecture.md](../architecture.md) §5. AIService: [ai.md](ai.md). Phân việc: [../work-division.md](../work-division.md).

## Vai trò
- Quản lý **session phỏng vấn**, câu hỏi, câu trả lời (audio), điểm, rubric, file CV/JD.
- Gọi **AIService** sinh câu hỏi (đồng bộ) + publish job chấm điểm lên **RabbitMQ**; nhận kết quả qua **callback nội bộ**.
- **Phân biệt B2B/B2C bằng `campaign_id` trên session** (null = B2C luyện tập; có giá trị = bài thi B2B của campaign). Engine + state machine **giữ nguyên** cho cả hai.
- **Danh tính ứng viên:** B2C lấy `candidateId` từ token người luyện; **B2B** vào bằng **magic-link** → provision/login account `Candidate` nhẹ (có `candidate_id` + JWT) → ownership "chủ session" dùng đúng cơ chế cũ.
- **Vào bài B2B — sàng CV là bước TRƯỚC, không thuộc engine này:** ứng viên có thể được mời **thẳng**, hoặc qua **sàng lọc CV** ở CampaignService rồi mới mời (`Invited` → magic-link). **Sàng CV KHÔNG chạm engine phỏng vấn này và KHÔNG tiêu credit** ([campaign.md](campaign.md) §Lọc ứng viên qua CV; **D19**). Từ magic-link trở đi (create-or-get session gắn `campaign_id` → **Interview reserve** credit org (BK14) → chấm → consume) = **luồng + state machine + billing NGUYÊN như cũ** — engine không phân biệt ứng viên đã qua sàng CV hay chưa.

---

## API — `/api/v1/interview`

> **Quy ước:** Base public `/api/v1/interview/*` (gateway StripPrefix → service `/api/practice/*`, `/api/files/*`). **Auth: JWT Bearer** (validate offline); `candidateId` = claim `sub`; chỉ **chủ** session/file thao tác được. Callback nội bộ dùng header **`X-Internal-Token`**, **KHÔNG** qua gateway. **Kiểu dữ liệu:** `uuid` · `string` · `int` · `long` (bytes) · `decimal(p,s)` · `bool` · `datetime` (ISO-8601, `timestamptz`) · `enum` lưu **string** · `T[]` = mảng · `{…}` = object lồng · `?` = optional/nullable. Mã lỗi chung: [../architecture.md](../architecture.md) §6. *(🔜 = endpoint/field thuộc phần chưa build.)*

### Schemas (DTO)

```
PracticeSessionResponse {
  id:           uuid
  status:       enum(string)            // GeneratingQuestions·Ready·InProgress·Completed·Scoring·Scored·Failed·SessionAbandoned (Completed = reserved, chưa set; SessionAbandoned = bỏ ngang/quá hạn/0-scored, terminal)
  jobCategory:  enum(string)            // BA·BE·FE
  cvId:         uuid?                   // file CV đính kèm
  jdId:         uuid?                   // file JD đính kèm
  createdAt:    datetime
  completedAt:  datetime?               // set khi submit
  questions:    QuestionResponse[]
  result:       SessionResultResponse?  // ✅ BC9 — chỉ khi status=Scored & campaign_id=null (B2C); null nếu chưa chấm xong
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

SessionResultResponse  ✅ {           // BC9 (số liệu) + BC10 (nhận xét) — tổng kết cả buổi (B2C), lưu DB khi Scored
  overallScore:    decimal(5,2)        // BC9 — 0–100, B2C = TRUNG BÌNH CỘNG pct các tiêu chí (equal weight, KHÔNG dùng weight); B2B ranking mới weighted (E4)
  answeredCount:   int                 // BC9 — số câu đã chấm (có scores) — kết quả tính trên bấy nhiêu câu
  totalQuestions:  int                 // BC9 — tổng số câu của buổi (vd 5)
  criteriaScores:  CriterionScoreResponse[]   // BC9 — mỗi tiêu chí được bao nhiêu điểm
  needsImprovement: uuid[]             // BC9 — criterionId của tiêu chí dưới ngưỡng (yếu → ưu tiên cải thiện)
  overallComment:  string?             // ✅ BC10 — NHẬN XÉT CHUNG cả buổi (AI `/summarize-session` best-effort khi B2C Scored); null nếu AI lỗi/timeout/rỗng HOẶC criteria rỗng/overallScore null (skip summarize)
}

CriterionScoreResponse  ✅ {           // BC9 — điểm "mỗi trường tiêu chí cỡ nhiêu điểm"
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
  overallScore: decimal(5,2)?           // ✅ BC9 — điểm tổng 0–100 nếu đã Scored (B2C); null nếu chưa → list lịch sử hiện điểm
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

CvAnalysisResponse  ✅ {
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

RoadmapResponse  ✅ {                  // BC5 (BC12) — roadmap ôn tập cá nhân hoá (D20)
  id:           uuid
  jobCategory:  enum(string)           // BA·BE·FE
  level:        enum(string)           // Fresher·Junior·Middle·Senior
  cvId:         uuid?
  status:       enum(string)           // Active·Completed·Abandoned
  milestones:   MilestoneResponse[]    // theo orderNo
  createdAt:    datetime
  completedAt:  datetime?
}

MilestoneResponse  ✅ {
  id:            uuid
  orderNo:       int
  title:         string
  focusCriteria: string[]              // tên tiêu chí trọng tâm (snapshot từ điểm yếu)
  status:        enum(string)          // Pending·InProgress·Completed
  improvement:   { criterionName: string, deltaPct: decimal(5,2) }[]?  // set khi Completed — so baseline / mile trước
  lessons:       LessonResponse[]
}

LessonResponse  ✅ {
  id:            uuid
  orderNo:       int
  title:         string
  theoryContent: string?               // markdown — AI sinh LẦN ĐẦU mở lesson (lazy); null nếu chưa mở
  sessionId:     uuid?                 // session luyện gắn lesson (set khi /start)
  status:        enum(string)          // Theory·Practicing·Done
}

RoadmapReportResponse  ✅ {            // BC15 — interim (Active) tính read-time · final snapshot (Completed) không tính lại
  radar:           CriterionScoreResponse[]   // avg % per tiêu chí qua các session thuộc roadmap
  levelEvaluation: { criterionName: string, percentage: decimal(5,2), levelThreshold: int, passed: bool }[]
  strengths:       string[]            // kết luận chi tiết — AI sinh (best-effort; rỗng nếu AI lỗi/interim)
  weaknesses:      string[]
  improvements:    string[]            // cần cải thiện + gợi ý luyện tiếp
  overallComment:  string?
}
```

### Practice — `/api/v1/interview/practice/sessions` (JWT Candidate)

**`POST /sessions`** — Tạo session + sinh câu hỏi (gọi AI đồng bộ).
- Req `application/json`: `{ "cvId": uuid?, "jdId": uuid?, "jdText": string?, "jobCategory": "BA"|"BE"|"FE" }` — `cvId`/`jdId` optional (parse sẵn ở Files); `jobCategory` **bắt buộc** (Đợt-1: DTO `[Required] JobCategory?` → **thiếu/null → 400**, guard TRƯỚC reserve nên không giữ credit oan; trước đây omit im lặng thành `BA`).
- ✅ **JD nhập TEXT** — `jdText` = JD dán thẳng, **không cần upload PDF**. Áp nguyên quy ước **C11** của B2B/Campaign: **text ưu tiên file** — gửi cả `jdText` lẫn `jdId` → dùng text, file **không parse** và `jd_id` **KHÔNG lưu** (row đừng "nhận vơ" file không góp gì vào câu hỏi). `jdText` rỗng/toàn khoảng trắng = coi như **không gửi** (rơi về `jdId`). **KHÔNG có cột `jd_text`** — JD text chỉ là input sinh câu hỏi, không ai đọc lại sau khi tạo (khác Campaign: `campaigns.jd_text` bị publish đọc lại nên buộc phải lưu).
- ✅ **BC2** *B2C:* reserve **1 credit ví cá nhân** (owner=User, khoá idempotency = sessionId) **TRƯỚC** khi tạo row session; ví hết → **402** (KHÔNG tạo session — PAY-5). Reserve thành công rồi AI/DB lỗi → release best-effort (P1-2) + SessionAbandoned (BK12) hoàn credit.
- Res **`201`** `PracticeSessionResponse` (`status="Ready"`, `questions` đã sinh):
```json
{ "id":"…","status":"Ready","jobCategory":"BE","cvId":"…","jdId":null,
  "createdAt":"2026-06-27T03:00:00Z","completedAt":null,
  "questions":[{"id":"…","orderNo":1,"content":"…","timeLimitSec":120,"answer":null}] }
```
- Lỗi: **400** (thiếu `jobCategory` · CV/JD không đọc được nội dung · AI trả rỗng) · **401** · **402** (hết credit ví — BC2) · **502** (AIService **gián đoạn sinh câu hỏi** → `AiServiceException`; Đợt-3: outage map **502** chứ KHÔNG phải 400, để phân biệt lỗi upstream thật với lỗi dữ liệu · Payment down cũng 502).

**`GET /sessions/history`** — Lịch sử của chính user. Res **`200`** `PracticeSessionSummary[]` (mới nhất trước; ✅ BC9: kèm `overallScore` nếu buổi đã `Scored`). Lỗi: **401**.

**`GET /sessions/{sessionId}`** — Chi tiết (câu hỏi + bài nộp + điểm). Res **`200`** `PracticeSessionResponse` (mỗi câu kèm `answer` + `scores` nếu có; ✅ BC9: kèm `result` tổng kết khi `status=Scored` & B2C). Lỗi: **401** · **403** (không phải buổi của bạn) · **404**.

**`POST /sessions/{sessionId}/submit`** — Chốt sổ (đi chấm nốt). Res **`204`**. Lỗi: **400** (chưa trả lời câu nào · trạng thái không cho submit) · **401** · **403** · **404**.

**`POST /sessions/{sessionId}/answers`** — Upload audio trả lời.
- Req `multipart/form-data`: `questionId: uuid` · `file: audio ≤50MB` · `durationSec: int`.
- **Idempotent**: upload lại cùng `questionId` = ghi đè (reset transcript **+ xoá điểm cũ `answer_scores` + `needs_review=false`**, publish lại chấm) — INT-3, chấm lại từ đầu sạch (không trộn điểm/rubric version cũ).
- Res **`200`** `UploadAnswerResult` `{ answerId, questionId, status, transcript?, nextAction?, nextQuestion?{ id, orderNo, content, timeLimitSec, kind }, interviewComplete }` (`status="Scoring"` sau publish; câu đầu: session `Ready→InProgress`). Các field `transcript/nextAction/nextQuestion/interviewComplete` = **phỏng vấn THÍCH ỨNG (INT-17)**, chỉ có khi session bật adaptive; client cũ bỏ qua vẫn chạy (backward-compat).
- **Phỏng vấn THÍCH ỨNG (INT-17):** khi buổi bật adaptive + đây là **frontier** (mọi câu đã có answer) + còn ngân sách + chưa quá `deadline` → gọi AIService `/decide-next` (transcribe đồng bộ + Gemini) → `nextQuestion` (append `practice_questions` với `kind` FollowUp/Clarify/NewQuestion) HOẶC `interviewComplete=true` (end/hết ngân sách → mời submit). `/decide-next` lỗi → **degrade** luồng tĩnh (answer đã lưu, worker transcribe async; response không có câu kế). Câu kế trả **ngay trong response** → client khỏi poll `GET /sessions/{id}`.
- Lỗi: **400** (thiếu field · file quá lớn) · **401** · **403** · **404** (session/câu không có) · **409** (session đã `Scoring`/`Scored`).

### Files — `/api/v1/interview/files` (JWT) — chỉ `.pdf`, `fileType ∈ {cv,jd}`

**`POST /upload?fileType=cv|jd`** — Upload PDF (≤10MB) + parse text.
- Req `multipart/form-data`: `file: pdf`. Query `fileType`.
- Res **`201`** `FileRecord` (`parseStatus` = `done`/`pending`). Lỗi: **400** (không phải PDF · quá lớn · `fileType` sai) · **401**.

**`GET /{id}`** → `FileRecord` · **`GET /{id}/download`** → bytes (`Content-Type` theo mime; **404** nếu thiếu) · **`GET /{id}/parsed-text`** → `{ id, parsedText, parseStatus }`.
**`GET /files`** → `FileRecord[]` của user.
**`PUT /{id}`** (multipart, thay file) → `FileRecord` mới · **`DELETE /{id}`** → **`204`** (xóa record + key S3).
Lỗi chung Files: **401** · **403** (không phải file của bạn) · **404**.

### CV Analysis — `/api/v1/interview/practice/cv-analysis` (JWT Candidate) — ✅ **B2C BC4/BC7/BC7b** (D17→D22)

**`POST /cv-analysis`** — Phân tích CV (parse → AIService `/analyze-cv` đồng bộ → lưu `cv_analyses`).
- Req `application/json`: `{ "cvId": uuid, "jdId": uuid?, "jdText": string?, "jobCategory": "BA"|"BE"|"FE" }` — `jobCategory` **bắt buộc** (thiếu/null → **400**, validate **TRƯỚC** reserve credit ⇒ không giữ credit oan; ✅ **BK6**). Có **JD** (`jdId` **hoặc** `jdText`) → kết quả thêm `jdMatch`.
- ✅ **JD nhập TEXT** — `jdText` = JD dán thẳng (quy ước **C11**, **text ưu tiên file**): gửi cả hai → dùng text, file JD **không đọc** (khỏi round-trip + ownership-check cho file không dùng) và `jd_id` **không lưu**. ⚠ Hệ quả đọc response: JD nhập tay → `jdId=null` **nhưng `jdMatch` vẫn có** (gate theo *có nội dung JD*, không theo `jdId`) — client đừng suy ra "không có jdMatch" từ `jdId=null`.
- Res **`201`** `CvAnalysisResponse`. Lỗi: **400** (thiếu `jobCategory` · CV không đọc được) · **401** · **402** (hết credit ví User — BK5/BC7b) · **403** (không phải file của bạn) · **404** (`cvId`/`jdId` không có) · **502** (AI lỗi).
- **Đồng bộ HTTP**, không qua RabbitMQ. **TÍNH PHÍ — trừ credit ví cá nhân** (rules.md **BC-4**, chốt **BK5** 2026-07-12, đảo "free phase 1" của D17). Mục (c) "CV vs câu trả lời" sau khi `Scored` = task `BC8`.
- **Engine `/analyze-cv` dùng chung với B2B:** CampaignService tái dùng **đúng endpoint này** để **sàng lọc CV hàng loạt** (gửi kèm `criteria[]` campaign → nhận thêm `criterionMatches`/`overallMatchScore`), nhưng gọi **async qua worker** (N CV) thay vì sync — xem [campaign.md](campaign.md) §Lọc ứng viên qua CV + [ai.md](ai.md). B2C (đây) **không đổi**: sync, lưu `cv_analyses`.

**`GET /cv-analysis/{id}`** → `CvAnalysisResponse` (403/404) · **`GET /cv-analysis`** → `CvAnalysisResponse[]` của user.

### Rubric cá nhân — `/api/v1/interview/practice/rubrics` (JWT Candidate) — ✅ **BC16**
> Candidate tự chỉnh **rubric riêng theo JobCategory** (không admin — đảo hướng BK3). Owner-scope tuyệt đối theo `candidateId` trong JWT. Chưa khai → dùng seed mặc định (BC11). Điểm tổng vẫn TB cộng (INT-10), `weight` chỉ hiển thị.

**`GET /rubrics/{jobCategory}`** (`BA|BE|FE`) → `RubricResponse { jobCategory, isCustom, criteria[] }` — rubric **hiệu lực**: rubric riêng nếu có (`isCustom=true`), else **seed mặc định làm template** (`isCustom=false`, FE clone rồi sửa).
**`PUT /rubrics/{jobCategory}`** body `{ criteria: [{ name, description?, weight, maxScore }] }` → **replace-all** rubric riêng (soft-versioned, FK-safe). Validate: `0<weight≤1`, `maxScore≥1`, name không trùng (case-insensitive), `Σweight∈[0.99,1.01]`→chuẩn hoá Σ→1; rỗng/ngoài dải → **400**. Res **`200`** `RubricResponse`.
**`DELETE /rubrics/{jobCategory}`** → reset về seed mặc định (soft-deactivate rubric riêng, idempotent). Res **`204`**.

### Roadmap ôn tập — `/api/v1/interview/practice/roadmaps` (JWT Candidate) — ✅ **B2C BC5 (BC12–BC15)** (D20)

> Nền tảng **ôn tập cá nhân hoá**: từ **report các buổi đã chấm** (`session_criterion_scores` — điểm yếu) + **CV** (upload mới hoặc hệ thống tự lấy CV có sẵn) + **level** → AI sinh **milestone roadmap**; mỗi milestone gồm các **lesson** = *lý thuyết trước* (AI sinh bám điểm yếu, lưu lại) → *luyện session* (engine chấm như thường). Xong mỗi mile → xem **độ cải thiện**; xong roadmap → `Completed` → **report cuối** (radar + đánh giá tiêu chí theo level + kết luận chi tiết). State machine + công thức: xem §Roadmap ôn tập cá nhân hoá (Business rules).

**`POST /roadmaps`** — Tạo roadmap.
- Req: `{ "jobCategory": "BA"|"BE"|"FE", "level": "Fresher"|"Junior"|"Middle"|"Senior", "cvId": uuid? }`.
- Server gom **điểm yếu** từ các session `Scored` gần nhất (`session_criterion_scores.needs_improvement`) + `parsed_text` CV (nếu có) → gọi AIService `/generate-roadmap` (**sync**) → lưu `roadmaps` + `roadmap_milestones` + `roadmap_lessons`; snapshot `baseline` (% hiện tại per tiêu chí) + `source_session_ids`.
- **Chưa có buổi nào đã chấm** → vẫn **`201`**: `baseline=null` + `source_session_ids=null`, AI sinh **roadmap chuẩn theo `level`** (không có điểm yếu để bám) — ✅ BC12 (khớp `tasks.md` BC12; **bỏ** quy tắc "403" cũ).
- **Tạo roadmap KHÔNG trừ credit** — chỉ session luyện bên trong mới reserve→consume (D7/D15).
- Res **`201`** `RoadmapResponse`. Lỗi: **400** (`jobCategory`/`level` sai · CV không đọc được) · **401** · **403** (`cvId` không phải của bạn) · **404** (`cvId`) · **502** (AI lỗi).

**`GET /roadmaps`** → `RoadmapResponse[]` của user (list — không kèm `theoryContent`) · **`GET /roadmaps/{id}`** → `RoadmapResponse` đầy đủ. Lỗi: **401** · **403** · **404**.

**`GET /roadmaps/{id}/lessons/{lessonId}`** — Mở lesson (lý thuyết).
- `theory_content` **null** → gọi AIService `/generate-lesson-theory` (**sync**) → **lưu rồi trả**; lần sau đọc DB (**lazy, idempotent** — mở nhiều lần chỉ sinh 1 lần). AI lỗi → **502**, mở lại được.
- Res **`200`** `LessonResponse`. **Miễn phí** (text-only — như D17). Lỗi: **401/403/404** · **502**.

**`POST /roadmaps/{id}/lessons/{lessonId}/start`** — Bắt đầu luyện lesson.
- Tạo **practice session B2C bình thường** (**reserve 1 credit** ví cá nhân như BC2 — hết → **402, KHÔNG tạo session**), câu hỏi sinh bám `focusCriteria` của milestone; set `roadmap_lessons.session_id`; lesson `Theory → Practicing`.
- Res **`201`** `PracticeSessionResponse`. Lỗi: **401/403/404** · **402** (hết credit) · **409** (lesson đang `Practicing` — resume session cũ thay vì tạo mới) · **502**.

**`GET /roadmaps/{id}/report`** — Report roadmap → **`200`** `RoadmapReportResponse`.
- **Interim** (`Active`): radar + levelEvaluation tính từ các session đã `Scored`; kết luận (strengths/…/overallComment) có thể rỗng/null.
- **Final** (`Completed`): đọc **snapshot** `roadmaps.final_report` + `overall_comment` — không tính lại.

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
| `jdText` (create session · cv-analysis) | optional; **ưu tiên hơn `jdId`** (C11); rỗng/khoảng trắng = không gửi; **chưa giới hạn độ dài** (theo C11 — xem ⚠ dưới) |
| `jobCategory` | bắt buộc, enum `BA·BE·FE` |
| upload file | PDF (cv/jd) **≤10MB** · audio (answer) **≤50MB**; sai loại/size → 400 |
| `questionId`/`durationSec` (answer) | bắt buộc; **1 answer/câu** (upload lại = ghi đè idempotent) |
| callback `/internal/*` | `X-Internal-Token` đúng (sai → 401) |

| Mã | Khi nào (đặc thù — chung [../architecture.md](../architecture.md) §6) |
|---|---|
| 400 | CV/JD không đọc được nội dung · AI trả rỗng · thiếu field · file quá lớn/sai loại |
| 401/403 | thiếu/sai JWT · **không phải chủ** session/file |
| 402 | hết credit ví (B2C reserve khi tạo session / cv-analysis — BC2/BC7b) |
| 404 | session/câu/file không tồn tại |
| 409 | upload answer khi session `Scoring`/`Scored` |
| 502 | AIService **gián đoạn/outage** (sinh câu hỏi / analyze-cv → `AiServiceException`, Đợt-3) · PaymentService down khi reserve |

## Luồng (sequence)

**Tạo session + sinh câu hỏi (sync AI):**
```
Candidate ─POST /sessions {cvId?,jdId?,jobCategory}─► Interview
   ├─ ✅ (B2C BC2) reserve 1 credit ví cá nhân — hết → 402, KHÔNG tạo session
   ├─ đọc parsed_text(cv/jd) → AIService /generate-questions (sync) → câu hỏi
   └─► 201 session(Ready) + questions[]    (AI outage → 502 · gen lỗi → session Failed + release credit BK12)
```

**Chấm dần + đóng session + phát event:**
```
Candidate ─POST /answers (audio)─► Interview: answer Uploaded → publish ScoringJob → Scoring (câu đầu: session→InProgress)
   (upload lại cùng câu = ghi đè: xoá answer_scores cũ + needs_review=false, chấm lại sạch — INT-3, Đợt-3)
AIService worker ─callback /internal/answers/{id}/result─► lưu answer_scores (idempotent) → answer Scored
Candidate ─POST /submit─► session Scoring; mọi answer ∈{Scored,Skipped,Failed} →
   ├─ ≥1 answer Scored → Scored → phát SessionScored  → Payment CONSUME credit
   └─ 0 answer Scored (mọi answer Failed/Skipped) → SessionAbandoned → phát SessionAbandoned → Payment RELEASE (PAY-13, Đợt-3: không trừ credit oan)
   (publish hụt / worker mất tích → StuckAnswerRepublisher quét 2' đẩy lại)
SessionScored ─RabbitMQ─► Campaign (ranking read-model) + Payment (consume credit)
   ✅ DB2: outbox-row ghi CÙNG transaction đóng session → OutboxDispatcher publish at-least-once
     (bus rớt → row published_at=null → gửi lại; event không mất) → Payment không kẹt reservation treo
```

**Phân tích CV B2C (sync, TÍNH PHÍ — BC-4, chốt BK5; đảo D17):**
```
Candidate ─POST /practice/cv-analysis {cvId,jdId?}─► Interview ─AIService /analyze-cv (sync)─► lưu cv_analyses → 201
```

---

## DB — `isas_interview`
```
practice_sessions 1──* practice_questions 1──1 practice_answers 1──* answer_scores
        │                                                              │
        ├──*? file_records (cv_id, jd_id)            rubric_criteria ──┘
        └──* session_criterion_scores (✅ BC9, B2C)
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
deadline      timestamptz?  ✅ I2+BK18 (migration AddSessionDeadline) — hạn chót nhận bài (B2B=campaign expires_at, Campaign gửi qua create-session payload BK18; B2C=null). Sweeper quá hạn → auto-submit/SessionAbandoned
overall_score numeric(5,2)? ✅ BC9 (migration `AddSessionResultBC9`) — điểm tổng 0–100, set khi `Scored` (B2C); null khi chưa/B2B
answered_count int?         ✅ BC9 — số câu đã chấm lúc tính kết quả (snapshot)
overall_comment text?       ✅ BC10 (migration `AddSessionOverallComment`) — nhận xét chung, AI `/summarize-session` sinh trong `SessionScoringNotifier` khi B2C `Scored` (best-effort, sau BC9); null nếu AI lỗi/timeout/rỗng / criteria rỗng / B2B
                            ⚠ cột `settlement_published_at` (Đợt-3b) đã **DROP** ở ✅ **DB2** (migration `AddOutboxMessages`) — thay bằng bảng `outbox_messages` (Transactional Outbox, xem dưới)
```

### `outbox_messages` — ✅ DB2 (Transactional Outbox, migration `AddOutboxMessages`)
```
id           uuid          PK — cũng là message-id (BasicProperties.MessageId, consumer dedup)
type         varchar(64)   routing key: session.scored · session.abandoned
payload      jsonb         JSON nguyên của SessionScoredEvent/SessionAbandonedEvent (giữ totalScore weighted, reason gốc)
session_id   uuid          tra cứu/idempotency
occurred_at  timestamptz   thứ tự phát
published_at timestamptz?  null=chưa gửi (dispatcher set khi publish OK); broker down → giữ null → gửi lại
attempts     int           số lần thử publish
                           index (published_at) WHERE published_at IS NULL — dispatcher scan
```
> **Cơ chế:** khi đóng session (`Scored`/`SessionAbandoned`/`Failed`-generation_failed), 5 site ghi outbox-row **CÙNG transaction với state-flip** (sweeper bọc `BeginTransactionAsync`; các nơi khác cùng `SaveChanges` với state) → `OutboxDispatcher` (BackgroundService, `Outbox:Enabled/ScanIntervalSeconds`) publish at-least-once lên `interview.events` → set `published_at`. **Thay hẳn** marker `settlement_published_at` + `SettlementReconciler` (B2C-only, bỏ sót B2B + generation_failed — nay outbox phủ cả). Payment/Campaign consumer idempotent (PAY-11/CAMP-10) → at-least-once an toàn.

### `practice_questions`
```
id                       uuid          PK
session_id               uuid          FK → practice_sessions (Cascade)
order_no                 int
content                  text
time_limit_sec           int           default 120  giới hạn/câu — ĐANG hiệu lực (hết giờ→chốt câu, sang câu kế); KHÔNG có giới hạn tổng buổi
kind                     varchar(16)   INT-17 enum QuestionKind: Seed·FollowUp·Clarify·NewQuestion (rows cũ backfill 'Seed')
generated_from_answer_id uuid?         INT-17 answer đã "đẻ" câu này (null=seed); UNIQUE filtered (idempotency: 1 answer→≤1 câu kế). Ref lỏng practice_answers (KHÔNG FK — tránh cascade path)
created_at               timestamptz
                             UNIQUE (session_id, order_no)
                             UNIQUE (generated_from_answer_id) WHERE generated_from_answer_id IS NOT NULL
```
> **Phỏng vấn THÍCH ỨNG (INT-17):** `practice_sessions` thêm `adaptive_enabled bool` (toggle theo buổi; tắt = luồng batch tĩnh cũ) + `max_follow_ups int`/`max_questions int` (trần; 0 = không trần cứng). Migration `AddAdaptiveInterviewColumns` (reversible; `ADD COLUMN kind DEFAULT 'Seed'` backfill an toàn; index add online-safe).

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
needs_review              bool          ✅ E10 (migration AddPracticeAnswerNeedsReview) — true khi spread điểm (self-consistency, max−min/tiêu chí qua N attempt) > ngưỡng → HR/người luyện xem lại; default false
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
level_matched  int?          ✅ E9 (migration AddAnswerScoreLevelMatched) — mức khớp (= score khi neo theo rubric_levels/dải mặc định); null nếu chưa neo
rubric_version int
created_at     timestamptz
                             UNIQUE (answer_id, criterion_id, attempt_no)
```

### `session_criterion_scores` — ✅ BC9 (B2C; ghi khi session `Scored`)
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
candidate_id uuid?         ✅ BC16 — B2C rubric CÁ NHÂN: null=seed mặc định dùng chung · set=rubric riêng của candidate (ref lỏng AuthService, không FK). Chỉ có nghĩa khi campaign_id IS NULL.
version      int
                           index (job_category, version, is_active) · index (candidate_id, job_category, is_active) [BC16]
                           CHECK ck_rubric_criteria_single_owner: campaign_id IS NULL OR candidate_id IS NULL  ✅ DB19 (2026-07-17) — cấm both-set; 3 trạng thái loại trừ (campaign-only B2B · candidate-only B2C · both-null seed)
```
> **BC16 — resolve rubric B2C:** scoring chọn tiêu chí theo `(candidate_id, job_category)`: có rubric riêng active của candidate → dùng nó, **else** seed mặc định (`candidate_id IS NULL`). Dùng chung `B2CRubricScope.ResolveOwnerAsync` ở cả 4 chỗ chấm (publish · callback guard · republisher · breakdown BC9) để không lệch. Sửa rubric = **soft-versioned** (deactivate bản cũ + thêm bản mới `is_active`, KHÔNG hard-delete vì `answer_scores` FK Restrict).

### `rubric_levels`
```
id              uuid   PK
criterion_id    uuid   FK → rubric_criteria (Cascade)
score           int    0..max_score
descriptor      text
example_answers jsonb  ✅ DB15 — list câu mẫu neo cho mức (gộp từ bảng `rubric_anchors` cũ); '[]' nếu không có
                       UNIQUE (criterion_id, score)
```

### ~~`rubric_anchors`~~ ✅ **DB15 (2026-07-17) — bảng đã DROP, gộp inline vào `rubric_levels.example_answers` (jsonb `List<string>`)**
> Trước: bảng riêng `rubric_anchors(id, level_id, example_answer)` 1 level ↔ N anchor. Nay mỗi câu mẫu = 1 phần tử trong `rubric_levels.example_answers` jsonb (converter+comparer như `RoadmapMilestone.FocusCriteria`). `ScoringCriteriaBuilder` flatten `l.ExampleAnswers` → `ScoringAnchorDto{Score, ExampleAnswer}` **giữ nguyên output** → **ScoringJob wire contract bất biến** (worker Python không đổi). Migration `SchemaCleanupDb15` (backfill `jsonb_agg(ORDER BY id)` → DROP TABLE; reversible; L3 Postgres verify 0-loss).

### `file_records`
```
id             uuid          PK
user_id        uuid          ref lỏng → Auth
file_type      varchar(16)   enum: cv·jd·answer-audio
original_name  varchar
storage_path   varchar       key SeaweedFS (KHÔNG lưu full URL)
storage_bucket varchar       "isas-files" — ⚠ hằng config (mọi row cùng giá trị): chỉ đáng giữ để phòng multi-bucket/migration bucket sau này; KHÔNG viết logic đọc theo cột này, đọc theo config
mime_type      varchar
file_size      bigint        bytes
parsed_text    text?
parse_status   varchar(16)   enum: pending·done·failed
created_at     timestamptz
updated_at     timestamptz
```
> ⚠ **Audio trả lời chỉ có MỘT nguồn: `practice_answers.audio_object_key`** — không tạo row `file_records` cho audio (2 nơi cùng giữ key = nguy cơ lệch/mồ côi khi ghi đè answer). Enum `answer-audio` + quirk `practice_answers.id (= fileId audio)` là **vết thiết kế cũ của engine**: giữ để không phá code chạy, nhưng **target** là bỏ `answer-audio` khỏi enum này; `file_records` chỉ lo **cv·jd** (file user upload + parse text).

### `cv_analyses` — ✅ **B2C BC4/BC7** (migration `AddCvAnalyses`) (D17→D22)
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

### `roadmaps` — ✅ **B2C BC5 (BC12)** (migration `AddRoadmaps`) (D20)
```
id                 uuid          PK
candidate_id       uuid          NOT NULL, index; ref lỏng → Auth
job_category       varchar(8)    enum: BA·BE·FE
level              varchar(16)   enum: Fresher·Junior·Middle·Senior
cv_id              uuid?         FK → file_records (Restrict)
source_session_ids jsonb?        uuid[] — session `Scored` làm input điểm yếu (snapshot lúc tạo)
baseline           jsonb?        { criterionName: pct } — % per tiêu chí lúc tạo (mốc so cải thiện); null nếu chưa có buổi nào
status             varchar(16)   enum: Active·Completed·Abandoned
final_report       jsonb?        snapshot RoadmapReport khi Completed (radar + levelEvaluation + kết luận)
overall_comment    text?         nhận xét chung roadmap — AI `/summarize-roadmap` best-effort (pattern BC10)
created_at         timestamptz
completed_at       timestamptz?
```

### `roadmap_milestones` — ✅ (BC12)
```
id             uuid          PK
roadmap_id     uuid          FK → roadmaps (Cascade)
order_no       int           UNIQUE (roadmap_id, order_no)
title          varchar
focus_criteria jsonb         string[] — tên tiêu chí trọng tâm (snapshot; rubric đổi version không hồi tố)
status         varchar(16)   enum: Pending·InProgress·Completed
improvement    jsonb?        { criterionName: deltaPct } — set khi Completed (so baseline / mile trước)
completed_at   timestamptz?
```

### `roadmap_lessons` — ✅ (BC12)
```
id                  uuid          PK
milestone_id        uuid          FK → roadmap_milestones (Cascade)
order_no            int           UNIQUE (milestone_id, order_no)
title               varchar
theory_content      text?         markdown lý thuyết — AI sinh LẦN ĐẦU mở lesson (lazy), sau đọc DB
theory_generated_at timestamptz?
session_id          uuid?         FK → practice_sessions (Restrict) — session luyện gắn lesson (set khi /start)
status              varchar(16)   enum: Theory·Practicing·Done
```

### Index & ràng buộc (tổng hợp)
- **FK on-delete**: Cascade theo `session_id` → `practice_questions` · `practice_answers` (→ `answer_scores` Cascade) · `session_criterion_scores`. `cv_id`/`jd_id` → `file_records` **Restrict** (chặn xoá file đang gắn session). `answer_scores.criterion_id` → `rubric_criteria` **Restrict**. `rubric_levels` Cascade *(bảng `rubric_anchors` đã DROP — DB15, gộp vào `rubric_levels.example_answers` jsonb)*. ✅ Roadmap: Cascade theo `roadmap_id` → `roadmap_milestones` (→ `roadmap_lessons` Cascade); `roadmaps.cv_id` → `file_records` **Restrict** · `roadmap_lessons.session_id` → `practice_sessions` **Restrict**.
- **UNIQUE**: `practice_questions(session_id, order_no)` · `practice_answers`: 1 answer/câu enforce qua UNIQUE `question_id` (1-1 FK) — ✅ **DB15 bỏ index UNIQUE trùng `(session_id, question_id)`, thay non-unique `(session_id)`** giữ leading-col cho sweeper/EXISTS · `answer_scores(answer_id, criterion_id, attempt_no)` · `session_criterion_scores(session_id, criterion_id)` · `rubric_levels(criterion_id, score)` · ✅ `roadmap_milestones(roadmap_id, order_no)` · `roadmap_lessons(milestone_id, order_no)`.
- **Index**: `practice_sessions(candidate_id)` + `(campaign_id)` · `rubric_criteria(job_category, version, is_active)` · `file_records(user_id)` · ✅ `roadmaps(candidate_id)`.
- **Idempotency**: callback `result` xoá điểm cũ cùng `(attempt_no, rubric_version)` rồi ghi lại; `failed` bỏ qua nếu answer đã `Scored` (xem §Idempotency callback).

---

## Business rules

### State machine — Session
```
GeneratingQuestions ──► Ready ──► InProgress ──► Scoring ──► Scored          ★
        │                                            └──► SessionAbandoned   ★ (0 answer Scored — PAY-13)
        └──► Failed (sinh câu hỏi lỗi)                                        ★
InProgress/Ready ──► SessionAbandoned  ★ (quá deadline B2B 0-answer · B2C không hoạt động P1-1)
```
- **Enum đầy đủ:** `GeneratingQuestions·Ready·InProgress·Completed·Scoring·Scored·Failed·SessionAbandoned` (`Enums/SessionStatus.cs`; API trả **chuỗi** — Interview có `JsonStringEnumConverter`).
- Chỉ `Ready`/`InProgress` mới **submit** được; phải có ≥1 answer.
- Submit → `Scoring` + `CompletedAt`. Nếu mọi answer đã xong → đóng thẳng `Scored`.
- Đóng `Scored` khi đang `Scoring` **và** mọi answer ∈ {Scored, Skipped, Failed} **và** ≥1 answer `Scored`.
- **✅ PAY-13 (Đợt-3): 0 answer nào `Scored`** (mọi answer `Failed`/`Skipped`) → session sang **`SessionAbandoned`** + phát `SessionAbandoned` (Payment **release**, KHÔNG consume) — candidate không bị trừ credit oan khi cả buổi lỗi chấm. Áp cả 2 điểm đóng (`AnswerService` chấm dần + `PracticeService.SubmitSession`).
- `Completed` có trong enum nhưng **CHƯA set** ở production (reserved; enum value giữ cho FE + tương lai).
- **✅ DB2 Transactional Outbox (thay `SettlementReconciler` Đợt-3b):** đóng session ghi row `outbox_messages` CÙNG transaction với state-flip → `OutboxDispatcher` publish at-least-once `session.scored`/`session.abandoned`; bus rớt → `published_at` null → gửi lại vòng sau → Payment không kẹt reservation `Reserved` vĩnh viễn (Payment idempotent PAY-11). Phủ CẢ B2C + **B2B** + **generation_failed** (SettlementReconciler cũ bỏ sót 2 cái sau); payload giữ nguyên (không reconstruct điểm).
- **Giới hạn thời gian = TỪNG CÂU (áp cả B2B & B2C), KHÔNG có tổng buổi (🔸 `time_limit_minutes` tạm bỏ):** hết giờ 1 câu → **chốt riêng câu đó** (có ghi âm → nộp bình thường; chưa ghi → `Skipped`) → **sang câu kế**, KHÔNG đóng cả buổi. **Chống reservation treo (B2B) — `SessionAbandonSweeper` quét 2':** session `InProgress` quá **`deadline`** (= `campaigns.expires_at`, Campaign gửi qua create-session payload BK18) → **auto-submit** (≥1 answer → `Scoring`→`Scored` → consume credit) hoặc **0 answer → `SessionAbandoned`** (release credit). **✅ P1-1 (Đợt-2) B2C không hoạt động:** B2C không có hard-deadline (`deadline=null`) → nhánh quét riêng: session `Ready`/`InProgress` mà last-activity (`max(CreatedAt, answer mới nhất)`) cũ hơn `Scoring:B2CInactivityMinutes` → `SessionAbandoned` + release credit ví User (candidate đang luyện/vừa upload KHÔNG bị quét; B2B tuyệt đối không đụng). **Resume**: mở lại token chỉ cho làm **các câu CHƯA nộp** (1 answer/câu, câu đã nộp giữ nguyên).

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
- **Nguồn tiêu chí tùy mode:** B2C dùng **rubric theo `JobCategory`** (`version` + `is_active`; 1 nghề chung 1 version); **B2B dùng tiêu chí campaign CÓ CẤU TRÚC** — Campaign gửi kèm khi tạo session, Interview materialize thành `rubric_criteria(campaign_id)`. **Pipeline chấm + `answer_scores` giữ NGUYÊN**, chỉ đổi *nguồn tiêu chí* (không chấm trên `criteria_text` thô). **✅ I1:** `PracticeService.CreateCampaignSessionAsync(candidateId, { campaignId, jobCategory, questions[], criteria[] })` → session gắn `campaign_id` + materialize criteria → `rubric_criteria(campaign_id)`, **idempotent theo `campaign_id`** (dùng chung mọi session của campaign). **✅ D2:** HTTP entry = `POST /internal/sessions/campaign` (X-Internal-Token) → `GetOrCreateCampaignSessionAsync` (create-or-get idempotent theo (candidateId,campaignId) chưa-terminal); CampaignService gọi khi ứng viên bấm **Start** (sau khi Join campaign — membership model).
  - **✅ E1 (chọn tiêu chí khi build job chấm):** branch theo `campaign_id` của session — B2B (`campaign_id` có) → tiêu chí `rubric_criteria(campaign_id)`; B2C (`campaign_id` null) → rubric theo `job_category` **VÀ `campaign_id IS NULL`** (criteria campaign cũng mang `job_category` nên phải lọc thêm để không rò sang chấm B2C). Áp ở **cả** publish (`AnswerService.TryPublishScoringJobAsync`) lẫn republish (`StuckAnswerRepublisher`). Message shape + worker Python **KHÔNG đổi** (D9). Kết quả: session B2B `Scored` → `answer_scores.criterion_id` trỏ tiêu chí campaign.
- Worker chấm đủ **mọi** tiêu chí; thiếu → lỗi vĩnh viễn. Điểm **kẹp** `[0, maxScore]`. Bỏ tiêu chí Gemini bịa; chống trùng. `answer_scores` gắn `rubric_version` lúc chấm. Hiển thị: mỗi tiêu chí lấy **attempt mới nhất**.
- **Điểm tổng/session** (khi `Scored`): **B2C = TRUNG BÌNH CỘNG** pct tiêu chí (equal weight — BC9); **B2B = `Σ điểm×weight`** chuẩn hoá (có trọng số — dùng cho ranking E4).
- **✅ Tổng kết điểm B2C (BC9):** spec đầy đủ ở **§Tổng kết điểm buổi luyện B2C (BC9)** ngay dưới.
- **✅ Chất lượng & độ nhất quán khi chấm (E9–E11):** neo theo mức (đúng) + đo/chặn chênh lệch (nhất quán) + chuẩn nhận xét — spec ở **§Chất lượng & độ nhất quán khi chấm** dưới.

#### Đánh giá cách chấm tiêu chí hiện tại (review 2026-06-28)
**✅ Phần chắc — GIỮ NGUYÊN** (worker `gemini.score()` + callback C# `AnswerService.SaveResultAsync`):
- Worker **kẹp điểm `[0, maxScore]`** mỗi tiêu chí, **bỏ `criterionId` Gemini bịa** (không có trong rubric gửi xuống), **chống trùng**, **bắt buộc đủ mọi tiêu chí** (thiếu → `ValueError` → answer `Failed`). Chấm `temperature=0`.
- C# callback **idempotent** (xoá điểm cũ cùng `(attempt_no, rubric_version)` rồi ghi lại). `answer_scores.criterion_id` là **FK → rubric_criteria** ⇒ chặn id rác.
- Nguồn tiêu chí đúng mode (E1): B2B theo `campaign_id`, B2C theo `job_category` + `campaign_id IS NULL`.

**⚠ Điểm cần lưu ý / gap** (biết trước khi làm BC9/BC10/E4):
1. **Điểm tổng ĐÃ CÓ (cập nhật 2026-07-17).** **B2C = trung bình cộng** tiêu chí (BC9, `SessionResultService` → `practice_sessions.overall_score`, **KHÔNG** dùng `weight`) · **B2B = Σ điểm×weight** cho ranking (E4, CampaignService qua event `SessionScored`). Worker vẫn chỉ chấm **từng câu** theo `maxScore`; tổng hợp ở service .NET. `weight` (trên `rubric_criteria`) **dùng cho ranking B2B + hiển thị**, không dùng cho điểm tổng B2C (INT-10).
2. **`maxScore` khác nhau giữa các tiêu chí** ⇒ **KHÔNG cộng điểm thô** (tiêu chí thang cao sẽ lấn). Phải chuẩn theo `maxScore` (percentage) như BC9. `answer_scores.score` là điểm **theo thang riêng** từng tiêu chí.
3. **B2C chưa có nguồn `rubric_criteria` theo `JobCategory`**: repo **không** seed/migration, cũng **không** có endpoint tạo rubric B2C. ⇒ DB trống rubric thì `AnswerService` thấy "không có tiêu chí active" → **bỏ publish → answer không được chấm**. Hiện phải **insert tay**. → **task BC11** (seed/CRUD rubric B2C). *(B2B ổn vì I1 materialize từ campaign.)*
4. **C# callback tin worker 100%** — `SaveResultAsync` lưu nguyên điểm worker gửi, **không tự kẹp / không kiểm đủ tiêu chí** (chỉ FK chặn id lạ). Mà **AIService deploy ephemeral** (docker cp, image có thể lệch — [ai.md](ai.md)) ⇒ nên cân nhắc **guard phía C#** (kẹp `[0,maxScore]`, bỏ criterion ngoài rubric) cho chắc. → **task E8**.
5. **Thiếu 1 tiêu chí → answer `Failed` vĩnh viễn** (worker raise `ValueError`): rubric nhiều tiêu chí dễ gãy oan — đã ghi [ai.md](ai.md) §Vấn đề (🟠 nên retry/self-consistency trước khi chốt Failed).
6. **`attempt_no` — self-consistency ĐÃ BUILD (E10, cập nhật 2026-07-17):** chấm **N lần** (opt-in qua `Scoring:Attempts`, default **N=1** = hành vi cũ) → điểm chốt = **median/tiêu chí**, spread>ngưỡng → `practice_answers.needs_review=true`. Vì default N=1 nên thực tế `attempt_no` thường =1; `UNIQUE(answer_id,criterion_id,attempt_no)` hỗ trợ N>1.

> **Tóm lại:** chấm **từng tiêu chí trên mỗi câu = ổn & chắc**; phần **tổng hợp mức buổi** (weight/điểm tổng/cần cải thiện) **chưa có** (BC9/BC10/E4) và **rubric B2C chưa có nguồn dữ liệu** (#3) là 2 việc cần làm để luồng B2C chạy trọn.

### Chất lượng & độ nhất quán khi chấm (E9–E11) — ✅ passing (E9 `4b4d625` · E10 `938bef0` · E11 `f3ef192`)
> Mục tiêu: **(1) chấm ĐÚNG mức · (2) chênh lệch mỗi lần/câu chấm NHỎ & ĐO ĐƯỢC · (3) nhận xét CÓ CĂN CỨ.** Áp **cả B2B & B2C**. Phần kẹp/lọc hiện có (review trên) **giữ nguyên** — đây là lớp *đảm bảo đúng*, không thay.

**E9 — Chấm NEO theo mức (levels + anchors).** ✅ **passing (vòng 17 · `4b4d625`)** *(tác động lớn nhất tới (1)+(2))*
- **✅ Đã làm:** `ScoringCriteriaBuilder` nạp `rubric_levels`(+`rubric_anchors`) mỗi tiêu chí vào message chấm; **có khai levels → dùng; KHÔNG → dải mặc định `0..maxScore`** (đúng cả B2B & B2C, chưa cần `/suggest-criteria` sinh levels — để **E9b**). AIService in levels/anchors vào prompt, AI trả `levelMatched` (score=level); C#+worker **snap mức gần nhất** khi score lệch (KHÔNG drop → tránh Failed INT-9), lưu `answer_scores.level_matched`.
- **Vấn đề (đã giải):** worker trước chỉ nhận `name/description/maxScore` → AI **tự bịa thang** → điểm nhảy; reasoning không bám mức. `rubric_levels`/`rubric_anchors` có schema nhưng KHÔNG gửi xuống worker → nay đã gửi.
- **Làm:** mỗi tiêu chí trong message kèm `levels:[{score,descriptor}]` (+ `anchors?:[{score,exampleAnswer}]`). AI **chọn mức khớp** → trả `{score, levelMatched, reasoning bám descriptor}`, **`score = levelMatched.score`**. Worker **+ C# (E8)** reject nếu `score` không trùng mức nào của tiêu chí. Lưu `answer_scores.level_matched`.
- **Nguồn mức:** B2C từ `rubric_levels` (đã có). **B2B:** `campaign_criteria` **chưa có mức** → publish/materialize phải **sinh mức** (mở rộng `/suggest-criteria` trả `levels` mỗi tiêu chí, hoặc dải mặc định `0..maxScore` có descriptor). Đây là điều kiện để E9 đúng cho B2B.

**E10 — Đo & chặn CHÊNH LỆCH (self-consistency).** ✅ **passing (vòng 18 · `938bef0`)** *(đảm bảo (2))*
- **✅ Đã làm:** `Scoring:SelfConsistencyN` (**default 1 — opt-in**, bật >1 khi cần) → publish N job/answer (attempt 1 temp=0, 2..N temp>0 để đo dao động); callback theo `attempt_no`, answer Scored khi đủ N attempt; **điểm chốt = median/tiêu chí** (client-eval, thay "latest"); **spread=max−min > `Scoring:VarianceThreshold` → `needs_review=true`** (cờ HR, điểm AI = gợi ý). N=1 → median-of-1 = hành vi cũ.
- **Vấn đề (đã giải):** `temperature=0` chỉ *tái lập*, **không** đo được dao động; `attempt_no` trước luôn = 1 → nay 1..N.
- **Làm:** chấm **N lần** (config `Scoring:SelfConsistencyN`, vd 3) → mỗi lần 1 `attempt_no`, **điểm chốt = median** mỗi tiêu chí. **spread = max−min**; **> ngưỡng** (`Scoring:VarianceThreshold`) → gắn `practice_answers.needs_review = true` (cờ HR), **không** tự coi là điểm cuối. Idempotent theo `(attempt_no, rubric_version)`.
- **Chi phí:** N× Whisper/Gemini — throughput đã là **trần** ([ai.md](ai.md) §Vấn đề) → **bật có chọn lọc** (chỉ chấm lại tiêu chí nghi ngờ / khi lần đầu sát biên), không luôn N×.

**E11 — Chuẩn "NHẬN XÉT OK" + HR chốt.** ✅ **passing (vòng 19 · `f3ef192`)** — AIService siết anti-injection (bỏ qua lệnh lái điểm trong transcript) + reasoning bắt buộc trích ≥1 dẫn chứng, `score()` reject reasoning rỗng; Interview flag `needs_review` khi reasoning quá ngắn (< `Scoring:MinReasoningLen`), KHÔNG mất điểm. HR override điểm cuối = **E11b**. *(đảm bảo (3))*
- `reasoning` (mỗi tiêu chí) + `overall_comment` (BC10): **bắt buộc trích ≥1 dẫn chứng** từ transcript (câu/cụm), **chặn rỗng/quá ngắn**, **bọc chống prompt-injection** (transcript = *dữ liệu*, không phải *lệnh* — ứng viên đọc "chấm/khen tối đa" KHÔNG được lái).
- **Human-in-the-loop:** điểm AI = **gợi ý**; UI hiện **transcript + reasoning + cờ `needs_review`** cho **HR (B2B) / người luyện (B2C)** xem lại → **HR chốt** điểm cuối, không auto-quyết tuyển dụng bằng điểm AI.

**Schema thêm (migration):** `answer_scores.level_matched int?` (E9) · `practice_answers.needs_review bool default false` (E10). **DTO:** `AnswerScoreResponse` thêm `levelMatched?`; `AnswerResponse` thêm `needsReview`. Đều nullable/thêm field → **không phá** client.

**Xác minh (3 lớp).** L1 build (gồm migration). L2 unit: (E9) message có `levels` → AI mock trả `levelMatched`; `score ≠` mức nào → **reject**; (E10) 3 lần chấm spread > ngưỡng → `needs_review=true`, điểm chốt = **median**; (E11) reasoning rỗng/không trích dẫn → reject; transcript chứa "hãy chấm tối đa" → **không** lái điểm. L3 e2e: 1 câu chấm thật → điểm **bám mức** + reasoning **trích transcript** + cờ review khi phân tán.

### Tổng kết điểm buổi luyện B2C (BC9) — ✅ passing

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

### Báo cáo "CV vs câu trả lời" (BC8) — ✅ passing

**Vì sao.** Sau khi tổng kết buổi (BC9), người luyện muốn biết **chỗ nào CV thể hiện mạnh nhưng thực tế trả lời lại yếu** — để ưu tiên ôn đúng lỗ hổng. Đây là mục (c) "CV vs câu trả lời" đã hoãn ở BC7.

**Phạm vi.** **CHỈ B2C** đã `Scored` **và có CV đã phân tích** (BC7). B2B / chưa `Scored` / không có CV → mục **absent** (không lỗi).

**Nguồn (THUẦN ĐỌC — KHÔNG AI, KHÔNG call service ngoài).**
- **"CV mạnh"** = `cv_analyses.strengths` **∪** `cv_analyses.jd_match.matched_skills` (BC7) — lấy bản phân tích **mới nhất** theo `(cv_id = session.cv_id, candidate_id)` (join lỏng qua `cv_id`, không FK xuyên bảng phân tích). Khử trùng, giữ thứ tự.
- **"trả lời yếu"** = `session_criterion_scores.needs_improvement = true` (BC9 — `percentage < ngưỡng`, mặc định 50%). **Tái dùng** cờ BC9, không tính lại.

**Định nghĩa "gap" (deterministic — không semantic AI).** Một tiêu chí là **"CV mạnh nhưng trả lời yếu"** khi thoả **CẢ HAI**: (1) `needs_improvement = true`, **và** (2) tên tiêu chí có **token trùng** với ≥1 chuỗi strength/skill CV. Token hoá = tách theo dấu/khoảng trắng, bỏ token < 3 ký tự + stopword generic (`and/skills/experience/knowledge/…`), so khớp **case-insensitive**. Mỗi gap trả kèm `cvEvidence[]` = các strength CV đã khớp (giải thích *vì sao* coi là "CV mạnh"). ⚠ *Khớp theo token, không hiểu ngữ nghĩa* — CV mô tả chung chung (không trùng chữ với tên tiêu chí) sẽ không tạo gap; nâng cấp semantic để sau (cần AI, ngoài phạm vi "không AI" của BC8).

**Trả về (read-time — KHÔNG migration).** `SessionResultResponse` thêm field nullable `cvVsAnswer`:
```json
"cvVsAnswer": {
  "cvStrengths": ["Microservice architecture", "SQL databases"],
  "gaps": [
    { "criterionId":"…","criterionName":"Microservice Design",
      "percentage":40, "maxScore":5, "cvEvidence":["Microservice architecture"] }
  ]
}
```
Dựng **lúc `GET /sessions/{id}`** trong `MapResult` (nằm trong `result` BC9) qua `CvVsAnswerReportBuilder.Build(cvStrengths, criterionScores)`. Có CV nhưng không tiêu chí nào khớp → `gaps` rỗng (report vẫn có `cvStrengths`). Không CV đã phân tích → `cvVsAnswer = null`. **Field nullable, thêm mới → không phá client.**

**Xác minh (3 lớp).** L1 `dotnet build`. L2 `dotnet test` (`CvVsAnswerReportTests`, +10): builder thuần (chỉ liệt kê tiêu chí VỪA yếu VỪA CV mạnh; loại tiêu chí mạnh/không khớp; không CV → null) + wiring qua `GET`: B2C Scored có CV → `cvVsAnswer` đúng gap+evidence (gộp matched skills JD); không CV/chưa phân tích → `null`; B2B/chưa Scored → `result` null. L3 e2e: buổi B2C có CV → chấm xong → `GET` thấy mục "CV mạnh trả lời yếu" đúng.

### Nhận xét chung buổi luyện B2C (BC10) — ✅ passing

**Vì sao.** Số liệu BC9 cho *điểm*; người luyện còn cần **nhận xét chung bằng lời** cho cả buổi (tổng quan làm tốt/chưa tốt ở đâu + hướng cải thiện) — giá trị định hướng của B2C. Sinh bằng **AI (Gemini)** nên **tách khỏi BC9** (BC9 giữ thuần engine, không AI).

**Phạm vi.** CHỈ B2C. Phụ thuộc **BC9** (cần số liệu tổng kết) + endpoint AIService mới.

**AIService (sync, không ghi DB — theo D17).** Endpoint mới `POST /summarize-session` (xem [ai.md](ai.md)): req `{ jobCategory, overallScore, criteriaScores:[{ name, percentage, needsImprovement }] }` → res `{ overallComment }` (text tiếng Việt, vài câu). Bọc nội dung ứng viên trong delimiter (chống prompt-injection — ai.md). InterviewService nhận kết quả rồi **tự lưu** (AI không ghi DB).

**Khi nào sinh & lưu.** Sau khi BC9 ghi số liệu lúc đóng `Scored` (B2C) → InterviewService gọi AIService **best-effort** → lưu `practice_sessions.overall_comment`. **AI lỗi KHÔNG chặn `Scored`**: để `overall_comment = null` + log; có thể **lazy/backfill** (sinh lại khi `GET` thấy null) — không bắt buộc trong BC10.

**DB / DTO.** Cột `practice_sessions.overall_comment text?` (migration BC10) · DTO `SessionResultResponse.overallComment string?`.

**Ảnh hưởng.** Phá tính "không AI" của BC9 (lý do tách BC10) · thêm **endpoint AIService** + **1 call đồng bộ best-effort** lúc đóng session · thêm 1 cột (migration) · chi phí Gemini/buổi. **Không** infra mới (HTTP sync như `generate-questions`/`suggest-criteria`/`analyze-cv`).

**Edge cases.** AI lỗi/timeout → `overall_comment=null`, `Scored` vẫn thành công. Session B2B → không sinh. Chưa `Scored` → null.

**Xác minh (3 lớp).** L1 build (gồm migration). L2 test: AIService `/summarize-session` trả `overallComment` từ input số liệu (mock); InterviewService đóng B2C `Scored` → gọi AI (mock) → lưu `overall_comment`; AI ném lỗi → `Scored` vẫn xong + `overall_comment=null`. L3 e2e: luyện B2C → chấm xong → `GET /sessions/{id}` có `result.overallComment` (AIService thật).

### Roadmap ôn tập cá nhân hoá (BC5 · BC12–BC15) — ✅ passing (D20)

**Vì sao.** BC9/BC10 cho biết *yếu ở đâu* rồi dừng — người luyện không có *lộ trình luyện tiếp*. Roadmap đóng vòng lặp retention B2C: **chấm xong → biết điểm yếu → lộ trình mile/lesson (lý thuyết + luyện) → đo cải thiện → report**. Tái dùng nguyên engine chấm + BC9 (nguồn điểm yếu) + BC11 (rubric B2C) — **không** nhân đôi máy chấm, **không** infra mới.

**Luồng.** Chọn `jobCategory` (BA/FE/BE) + `level` → server gom report các buổi đã chấm + CV (có sẵn tự lấy / upload mới) → AI sinh **milestones** (mỗi mile bám 1–2 tiêu chí yếu) + **lessons** → vào từng mile: mở lesson → **lý thuyết trước** (AI sinh theo điểm yếu, lưu DB) → `/start` luyện session (chấm như thường, BC9 ghi kết quả) → mọi lesson `Done` → mile `Completed` + tính **improvement** → mọi mile xong → roadmap `Completed` → **final report**.

**State machine.**
```
roadmap  : Active ─(mọi milestone Completed → build final_report + AI comment)─► Completed ★
           Active ─(user bỏ)──────────────────────────────────────────────────► Abandoned ★
milestone: Pending ─(lesson đầu tiên được mở)─► InProgress ─(mọi lesson Done → tính improvement)─► Completed ★
lesson   : Theory ─(/start: tạo session + reserve credit)─► Practicing ─(session Scored — móc vào luồng đóng BC9)─► Done ★
           Practicing ─(session Abandoned → release credit như D7)─► Theory   (mở làm lại được; session_id clear)
```
- Lesson `Done` set **trong luồng đóng session `Scored`** (cùng service/DB với BC9 — **không cần event**); session `Abandoned` → lesson quay về `Theory`, `session_id` clear để start lại (session bỏ ngang **mất link lesson** — chấp nhận, KHÔNG cần bảng trung gian `lesson_attempts`: session Abandoned không có điểm nên không tham gia radar/improvement; lịch sử buổi vẫn còn ở `practice_sessions`).
- **Idempotent:** mở lesson N lần chỉ sinh lý thuyết 1 lần (`theory_content` có → đọc DB); `/start` khi đang `Practicing` → **409** (resume session cũ, không tạo/reserve thêm).

**Billing — D7/D15 nguyên vẹn, KHÔNG cơ chế tiền mới.** Tạo roadmap + sinh lý thuyết = **miễn phí** (text-only Gemini, như D17 — *team xác nhận nếu muốn tính phí*). **Session luyện trong lesson = practice session B2C bình thường**: reserve 1 credit khi `/start` (hết → 402), consume khi `Scored`, release khi bỏ ngang.

**Improvement & report (BC15).**
- **Improvement mile N** = avg `percentage_c` (từ `session_criterion_scores` các session thuộc mile N) − avg mile N−1; **mile 1 so với `roadmaps.baseline`** (baseline `null` → mile 1 không có delta, chỉ hiện điểm đạt).
- **Radar** = avg `percentage_c` per tiêu chí qua **mọi** session thuộc roadmap — đọc `session_criterion_scores` (BC9), **không** tính lại từ `answer_scores`.
- **Đánh giá theo level**: `passed_c = percentage_c ≥ ngưỡng level`. Ngưỡng mặc định **Fresher 50 · Junior 60 · Middle 70 · Senior 80** (config `Roadmap:LevelThresholdPct` — *chốt khi build*); snapshot vào report lúc build (đổi config không hồi tố).
- **Kết luận chi tiết** (strengths / weaknesses / improvements + `overallComment`): AIService `/summarize-roadmap` **best-effort** — AI lỗi → list rỗng + comment null, roadmap vẫn `Completed` (pattern BC10). Final report **snapshot** vào `roadmaps.final_report`; interim **không** lưu (tính on-read).

**Edge cases.** Chưa có buổi nào đã chấm → roadmap chuẩn theo `level + jobCategory` (baseline null). Rubric đổi version giữa roadmap → `focus_criteria`/`baseline` là **snapshot theo TÊN tiêu chí** (so theo tên, không FK id — tránh vỡ khi rubric re-seed). Xoá CV đang gắn roadmap → chặn (FK Restrict). Lesson chưa mở lý thuyết mà gọi `/start` → cho phép (lý thuyết không bắt buộc đọc trước, sinh lazy khi mở).

**Xác minh (3 lớp).** L1 `dotnet build` (gồm migration 3 bảng). L2 unit: tạo roadmap **có/không** report cũ (AI mock) → đúng cấu trúc mile/lesson + baseline; mở lesson 2 lần → AI chỉ gọi **1 lần**; `/start` reserve credit (mock Payment), hết → **402 không tạo session**, đang `Practicing` → **409**; session `Scored` → lesson `Done`; mọi lesson Done → mile `Completed` + `improvement` khớp tính tay; mọi mile xong → `final_report` snapshot + comment (AI mock lỗi → vẫn `Completed`, comment null). L3 e2e: chạy trọn 1 roadmap 2 mile → report radar + levelEvaluation + kết luận đúng.

### Sự kiện phát ra (RabbitMQ)
Khi session đóng, engine phát event để service khác phản ứng (event-driven, tránh Campaign gọi HTTP đọc điểm mỗi lần):
| Event | Khi nào | Ai nghe |
|---|---|---|
| `SessionScored` | session `Scored` (kèm `campaign_id`, `candidate_id`, điểm tổng) | **Campaign** (cập nhật ranking read-model) · **Payment** (consume credit) |
| `SessionAbandoned` | session bỏ ngang quá hạn / 0 answer | **Payment** (release reservation) |

**Hợp đồng transport (pin 2026-07-11 theo E2 — E3/E4/E7 PHẢI khớp):** exchange **`interview.events`** (topic, durable); routing key **`session.scored`** (E2) · **`session.abandoned`** (E3). Mỗi consumer bind **queue durable riêng** (Campaign ranking / Payment credit) vào exchange → cùng 1 event tới nhiều consumer. Publish **best-effort** (lỗi publish KHÔNG phá state `Scored`/`Abandoned` đã commit) + giữ endpoint HTTP **backfill** làm fallback.

**Shape event:**
- `SessionScored` = `{ sessionId, campaignId?(null=B2C), candidateId, totalScore, scoredAt }`. `totalScore` (0–100) = **Σ pct×weight / Σweight** (CÓ trọng số) — là **snapshot phục vụ ranking B2B** (E4). **B2C** (`campaignId=null`): điểm hiển thị cho user = **trung bình cộng** tính riêng ở **BC9** (`result.overallScore`), **KHÔNG** đọc `totalScore` của event này (tránh lệch INT-10).
- `SessionAbandoned` = `{ sessionId, campaignId?, candidateId, reason, abandonedAt }` → Payment release reservation.

- **Credit:** **InterviewService reserve** 1 credit của org khi ứng viên **Start** (owner=Org từ `campaign.OrgId` Campaign gửi kèm — **BK14**, reserve-first như B2C; ví org hết → 402 no-session); engine phát `SessionScored` → **consume**, `SessionAbandoned` → **release** (owner lấy từ reservation, E7 — chi tiết [payment.md](payment.md)).
- Giữ 1 endpoint HTTP **backfill** làm fallback nếu miss event.

### Xác thực
- Endpoint user: **JWT Bearer**, validate bằng chung `Jwt:Key/Issuer/Audience`.
- Callback `/internal/...`: `AllowAnonymous` + header `X-Internal-Token`.
