# InterviewService — Engine phỏng vấn (dùng chung B2B & B2C)

> **Engine lõi**: session → câu hỏi → ghi âm trả lời → chấm điểm theo rubric/tiêu chí. Tái dùng cho cả luyện tập B2C lẫn bài thi B2B.
> Code: `src/services/Isas.InterviewService`. DB: `isas_interview`. Gateway: `/api/v1/interview`.
> Quy ước chung: [../architecture.md](../architecture.md) §5. AIService: [ai.md](ai.md). Phân việc: [../work-division.md](../work-division.md).

## Vai trò
- Quản lý **session phỏng vấn**, câu hỏi, câu trả lời (audio), điểm, rubric, file CV/JD.
- Gọi **AIService** sinh câu hỏi (đồng bộ) + publish job chấm điểm lên **RabbitMQ**; nhận kết quả qua **callback nội bộ**.
- **Phân biệt B2B/B2C bằng `campaign_id` trên session** (null = B2C luyện tập; có giá trị = bài thi B2B của campaign). Engine + state machine **giữ nguyên** cho cả hai.
- **Danh tính ứng viên:** B2C lấy `candidateId` từ token người luyện; **B2B** vào bằng **magic-link** → provision/login account `Candidate` nhẹ (có `candidate_id` + JWT) → ownership "chủ session" dùng đúng cơ chế cũ.
- **PONR1 (D26):** khi `Billing:ConsumeAtQuestionGeneration=true`, cả B2C, roadmap lesson và B2B consume ngay sau khi `Ready` + questions đã commit. Mặc định `false` cho đến PONR3; lỗi trước materialize release, còn Payment chỉ áp no-show mới sau `OrphanReconcile:ConsumeFromUtc`.

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
  audioUrl:     string?                 // URL owner-scoped để phát/tải audio; null khi chưa có file
}

**`GET /interview/practice/sessions/{sessionId}/answers/{answerId}/audio`** — Phát/tải audio câu trả lời của chính candidate. `AnswerResponse.audioUrl` trỏ tới route này; server xác minh chủ session từ JWT rồi stream audio, không lộ SeaweedFS object key. Không có audio/answer/session → **404**; session của người khác → **403**. **`Content-Type` theo định dạng thật của bản ghi** (suy từ đuôi object key — `audio/webm`, `audio/mp4`, …; đuôi lạ/dữ liệu cũ → `application/octet-stream`), không còn trả cứng `audio/webm` — BK27.

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
  benchmark:       BenchmarkResponse?   // ✅ F14 — mốc đối chiếu (lớp 2 của radar); null khi tắt (`Benchmark:Enabled=false`) / chưa Scored / không có breakdown
}

BenchmarkResponse  ✅ {                // F14 (FR08) — mốc đối chiếu cho radar
  source:   string                     // PeerAverage | PassThreshold
  label:    string                     // ⚠ nhãn PHẢI hiển thị NGUYÊN VĂN — xem cảnh báo dưới
  sampleSize: int                      // số buổi của NGƯỜI KHÁC góp vào (0 khi PassThreshold)
  criteria: CriterionBenchmarkResponse[]
}

CriterionBenchmarkResponse  ✅ {
  criterionId:       uuid
  name:              string
  targetPercentage:  decimal(5,2)      // 0–100, cùng trục với CriterionScoreResponse.percentage
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
- Req `application/json`: `{ "cvId": uuid?, "jdId": uuid?, "jdText": string?, "jobCategory": "BA"|"BE"|"FE", "seniority": "Fresher"|"Junior"|"Middle"|"Senior" }` — `cvId`/`jdId` optional (parse sẵn ở Files); `jobCategory` **bắt buộc** (Đợt-1: DTO `[Required] JobCategory?` → **thiếu/null → 400**, guard TRƯỚC reserve nên không giữ credit oan; trước đây omit im lặng thành `BA`). `seniority` mặc định `Junior`, được snapshot trên session và dùng cho adaptive prompt.
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
- **Định dạng audio (BK27)** — server nhận dạng theo thứ tự **magic bytes → `Content-Type` → đuôi tên file**, rồi lưu S3 với **đuôi đúng định dạng thật** (trước đây luôn là `.webm`). Chấp nhận: `audio/webm` · `audio/ogg` · `audio/mpeg` · `audio/mp4` · `video/mp4` · `audio/flac` · `audio/wav` (kèm alias `audio/x-m4a`, `audio/mp4a-latm`, `application/ogg`, `audio/x-wav`…). Ngoài tập này → **400**. ⚠ **`.3gp`/`.amr`/`.aac` thô KHÔNG được nhận** — client mobile phải ghi ra container MPEG-4 (`.m4a`); đây là ràng buộc từ `ORIGINAL_EXTENSIONS` của AIService, có test hợp đồng khoá hai chiều. Kill-switch `Audio:StrictFormatGate=false` → không từ chối, quay về hành vi cũ (`ext=webm`).
- **Idempotent**: upload lại cùng `questionId` = ghi đè (reset transcript **+ xoá điểm cũ `answer_scores` + `needs_review=false`**, publish lại chấm) — INT-3, chấm lại từ đầu sạch (không trộn điểm/rubric version cũ).
- Res **`200`** `UploadAnswerResult` `{ answerId, questionId, status, transcript?, nextAction?, nextQuestion?{ id, orderNo, content, timeLimitSec, kind }, interviewComplete }` (`status="Scoring"` sau publish; câu đầu: session `Ready→InProgress`). Các field `transcript/nextAction/nextQuestion/interviewComplete` = **phỏng vấn THÍCH ỨNG (INT-17)**, chỉ có khi session bật adaptive; client cũ bỏ qua vẫn chạy (backward-compat).
- **Phỏng vấn THÍCH ỨNG (INT-17):** khi buổi bật adaptive + còn ngân sách + chưa quá `deadline` → gọi AIService `/decide-next` (transcribe đồng bộ + Gemini) → `nextQuestion` (append `practice_questions` với `kind` FollowUp/Clarify/NewQuestion) HOẶC `interviewComplete=true` (→ mời submit). `/decide-next` lỗi → **degrade** luồng tĩnh (answer đã lưu, worker transcribe async; response không có câu kế). Câu kế trả **ngay trong response** → client khỏi poll `GET /sessions/{id}`.
- **INT-17b — chuỗi đào sâu THEO TỪNG CÂU GỐC** (`max_deep_per_question > 0`; `0` = chế độ frontier cũ, xem dưới):
  - Điều kiện sinh câu kế đổi từ *"mọi câu đã có answer"* (frontier) → *"câu vừa trả lời còn dưới trần độ sâu"* ⇒ trả lời câu nào là đào sâu ngay câu đó, **xen kẽ** thay vì dồn ở đuôi buổi. Bỏ frontier KHÔNG hở idempotency: `generated_from_answer_id` khoá theo `answer.Id`, mà Id đó giữ nguyên qua re-upload (INT-3).
  - Câu kế nối chuỗi: `depth = cha + 1`, `root_question_id` thừa kế của cha (seed: `depth=0`, `root_question_id=null` ⇒ gốc hiệu dụng `root_question_id ?? id`).
  - **Đánh số có khoảng trống**: câu gốc thứ `i` nhận `order_no = i × (1 + trần) + 1` (trần 3 → 1, 5, 9, 13, 17), câu đào sâu nhận `order_no = cha + 1`. Sắp theo `order_no` là ra đúng thứ tự hội thoại ⇒ **không cần field `displayOrder`, không phải sửa FE**. Trần 0 → stride 1 = đánh số liền nhau như cũ.
  - `action = "end"` mang nghĩa **hết CHỦ ĐỀ này**, không phải hết buổi ⇒ `interviewComplete` = *không còn câu nào chưa trả lời*. Khi chuỗi hết mà còn câu gốc khác, response trả `nextAction = null` (không phải `"end"`) — FE ánh xạ `end` thành "bạn có thể nộp bài", báo sớm sẽ giục ứng viên nộp giữa chừng và mất 1 credit cho buổi làm dở.
  - `action = "new_question"` **không append** ở chế độ này (chủ đề mới đã có sẵn trong bộ câu gốc); vẫn là giá trị hợp lệ trên dây để không phá hợp đồng AIService.
  - **Circuit breaker**: `/decide-next` lỗi → `practice_sessions.adaptive_failures++`; chạm `Adaptive:MaxFailuresPerSession` (mặc định 3) → thôi gọi cả buổi. Chế độ chuỗi gọi AI sau gần như mọi câu trả lời, không có cầu dao thì AIService hỏng sẽ cộng hàng chục phút chờ timeout vào đúng một buổi thi.
  - **Số câu GỐC bám số tiêu chí NỘI DUNG** (`ComputeSeedCount`, SC1) — ba lực kéo, xử **đúng thứ tự**:
    1. **Ngân sách** — `max_questions` là **TỔNG số câu buổi** (F2b) nên số câu gốc phải **chia cho chiều sâu**, làm tròn LÊN: trần 20 → 5 gốc (5×4=20) · 10 → 3 · 5 → 2. Lấy thẳng `Adaptive:SeedCount` thì FE mặc định 5 câu sẽ sinh 5 câu gốc, `askedCount` chạm trần ngay ⇒ **0 câu đào sâu, không lỗi, không báo** (bug `BUS-01`).
    2. **Phủ tiêu chí (SC1)** — số tiêu chí `WhenTargeted` là **SÀN**, và nó **THẮNG trần `SeedCount`**. Tiêu chí không câu nào hỏi bị loại khỏi điểm ⇒ điểm thành "may mắn trúng tủ"; thiếu đào sâu chỉ mất chiều sâu. Đo trên prod: 3 câu gốc nhưng nhãn ra 2 lần cùng một tiêu chí ⇒ tiêu chí thứ ba không bao giờ được hỏi. Bám con số **ĐỘNG** chứ không hằng số config vì **BC16** cho candidate tự CRUD rubric — hằng số sẽ lệch âm thầm.
    3. **Chừa ≥1 khe đào sâu** — trần cứng `max_questions − 1`, **thắng sau cùng**. Để `seeds == ngân sách` thì `AnswerService` thấy hết ngân sách ngay lượt đầu ⇒ buổi đóng dấu adaptive nhưng chạy y như batch tĩnh. Ngân sách quá hẹp thì thà thiếu phủ (**có log warning**) còn hơn tắt câm cả tính năng đào sâu (không triệu chứng).
    - `max_questions ≤ 0` = không trần buổi ⇒ bỏ vế 1 và 3, chỉ còn config và sàn phủ.
  - ⚠ **`questionCount` VẪN là "tổng số câu của buổi"** (F2b), **KHÔNG phải số câu gốc**. Đừng đổi thành `seeds × (1 + đào sâu)`: `ValidateQuestionCount` nhận 1..20 nên chọn 6 sẽ ra trần 24 → vi phạm CHECK `ck_practice_sessions_max_questions_range` **ngay lúc INSERT**, tức **SAU `ReserveAsync`** ⇒ đúng lỗi **PAY-5** (mất credit + reservation mồ côi) mà `ClampCampaignMaxQuestions` sinh ra để chặn, lần này trên đường B2C. Nó cũng âm thầm đổi thứ ứng viên đã mua bằng 1 credit.
  - **Thiếu phủ thì CHỈ log, không sửa nhãn** — gán bù tiêu chí thiếu vào câu chưa có nhãn là **BỊA** (ứng viên bị chấm đúng thứ họ không được hỏi = chính lỗi mà chấm-theo-phạm-vi sinh ra để diệt); bỏ sạch nhãn để quay về chấm cả rubric là lùi về nguyên hành vi cũ cho MỌI câu. Ném thì càng không: buổi đã reserve credit (PAY-5).
- Lỗi: **400** (thiếu field · file quá lớn) · **401** · **403** · **404** (session/câu không có) · **409** (session đã `Scoring`/`Scored`).

**`GET /sessions/{sessionId}/questions/{questionId}/speech`** — 🔊 **Đọc câu hỏi thành tiếng (TTS)** — trợ năng để ứng viên NGHE đề bài.
- Res **`200`**: **bytes audio**, `Content-Type: audio/mpeg` (mp3) — không phải JSON.
- **Dùng chung B2C và B2B** — session B2B cũng là `practice_sessions` và cũng có `candidate_id`, nên MỘT endpoint phục vụ cả hai dòng (không tách route theo `campaign_id`).
- **Owner-scope (INT-11):** chỉ **chủ buổi**. Không phải buổi của mình → **403** (khớp tiền lệ `GET /sessions/{id}`). `questionId` có thật nhưng **không thuộc** `sessionId` → **404** (không đọc trộm đề buổi khác chỉ vì đoán đúng GUID).
- **KHÔNG trừ credit** — credit = 1 lượt phỏng vấn **được AI chấm** (PAY-1); nghe lại đề bài không phải lượt chấm, và tính tiền theo số lần bấm nghe sẽ phạt đúng người cần trợ năng. Không reserve/consume gì.
- **Cache theo NỘI DUNG (⇒ KHÔNG cần migration):** Interview chỉ kiểm quyền rồi chuyển tiếp bytes; toàn bộ vendor + cache nằm ở **AIService `/tts`** — key S3 `tts/{sha256(voice+text)}.mp3` (xem [ai.md](ai.md) §TTS). Câu hỏi **trùng nhau** (nhất là seed B2B phát cho MỌI ứng viên) dùng chung **1 file** ⇒ chỉ tổng hợp/tính tiền **một lần**; sửa nội dung câu hỏi ⇒ hash đổi ⇒ audio cũ **tự vô hiệu hoá**. **KHÔNG thêm cột/bảng nào.**
- Lỗi: **401** · **403** (không phải buổi của bạn) · **404** (session không có · câu hỏi không thuộc session · nội dung câu hỏi rỗng) · **502** (AIService/TTS gián đoạn).
- ⚠ **502 KHÔNG được chặn luồng phỏng vấn** — FE degrade về **chỉ hiện chữ**. Vendor chết thì cố tình ném `AiServiceException` → **502**, KHÔNG nuốt thành 404 (404 sẽ khiến FE tưởng câu hỏi không tồn tại thay vì hiểu là TTS tạm hỏng).
- **AI-4:** nội dung câu hỏi (do AI sinh) được chuyển **nguyên văn** như **dữ liệu** — không ghép chỉ thị/nội suy gì quanh nó.

### Files — `/api/v1/interview/files` (JWT) — chỉ `.pdf`, `fileType ∈ {cv,jd}`

**`POST /upload?fileType=cv|jd`** — Upload PDF (≤10MB) + parse text.
- Req `multipart/form-data`: `file: pdf`. Query `fileType`.
- Res **`201`** `FileRecord` (`parseStatus` = `done`/`pending`). Lỗi: **400** (không phải PDF · quá lớn · `fileType` sai) · **401**.

**`GET /{id}`** → `FileRecord` · **`GET /{id}/download`** → bytes (`Content-Type` theo mime; **404** nếu thiếu) · **`GET /{id}/parsed-text`** → `{ id, parsedText, parseStatus }`.
**`GET /files`** → `FileRecordSummary[]` của user — **keyset-paged** (`?cursor=&limit=`, mặc định/tối đa 500; next-cursor ở header `X-Next-Cursor`, vắng = hết trang; body vẫn là mảng JSON) + lọc `?fileType=cv|jd` (push-down SQL).
- `FileRecordSummary` = `{ id, fileType, originalName, mimeType, fileSize, parseStatus, createdAt, updatedAt }`. **KHÔNG** có `parsedText` (toàn văn CV/JD — đọc riêng qua `GET /{id}/parsed-text`, owner-scoped) và **không** có `storagePath`/`storageBucket` (toạ độ SeaweedFS nội bộ — GEN-5). Trước đây endpoint trả nguyên entity `FileRecord` nên mỗi lần mở danh sách là kéo về toàn văn mọi CV đã upload; nay projection nằm **trong SQL** ⇒ cột `parsed_text` không được đọc lên, không chỉ là ẩn ở tầng JSON.
**`PUT /{id}`** (multipart, thay file) → `FileRecord` mới · **`DELETE /{id}`** → **`204`** (xóa record + key S3).
Lỗi chung Files: **401** · **403** (không phải file của bạn) · **404**.

### CV Analysis — `/api/v1/interview/practice/cv-analysis` (JWT Candidate) — ✅ **B2C BC4/BC7/BC7b** (D17→D22)

**`POST /cv-analysis`** — Phân tích CV (parse → AIService `/analyze-cv` đồng bộ → lưu `cv_analyses`).
- Req `application/json`: `{ "cvId": uuid, "jdId": uuid?, "jdText": string?, "jobCategory": "BA"|"BE"|"FE" }` — `jobCategory` **bắt buộc** (thiếu/null → **400**, validate **TRƯỚC** reserve credit ⇒ không giữ credit oan; ✅ **BK6**). Có **JD** (`jdId` **hoặc** `jdText`) → kết quả thêm `jdMatch`.
- ✅ **JD nhập TEXT** — `jdText` = JD dán thẳng (quy ước **C11**, **text ưu tiên file**): gửi cả hai → dùng text, file JD **không đọc** (khỏi round-trip + ownership-check cho file không dùng) và `jd_id` **không lưu**. ⚠ Hệ quả đọc response: JD nhập tay → `jdId=null` **nhưng `jdMatch` vẫn có** (gate theo *có nội dung JD*, không theo `jdId`) — client đừng suy ra "không có jdMatch" từ `jdId=null`.
- Res **`201`** `CvAnalysisResponse`. Lỗi: **400** (thiếu `jobCategory` · CV không đọc được) · **401** · **402** (hết credit ví User — BK5/BC7b) · **403** (không phải file của bạn) · **404** (`cvId`/`jdId` không có) · **502** (AI lỗi).
- **Đồng bộ HTTP**, không qua RabbitMQ. **TÍNH PHÍ — trừ credit ví cá nhân** (rules.md **BC-4**, chốt **BK5** 2026-07-12, đảo "free phase 1" của D17). Mục (c) "CV vs câu trả lời" sau khi `Scored` = task `BC8`.
- **Engine `/analyze-cv` dùng chung với B2B:** CampaignService tái dùng **đúng endpoint này** để **sàng lọc CV hàng loạt** (gửi kèm `criteria[]` campaign → nhận thêm `criterionMatches`/`overallMatchScore`), nhưng gọi **async qua worker** (N CV) thay vì sync — xem [campaign.md](campaign.md) §Lọc ứng viên qua CV + [ai.md](ai.md). B2C (đây) **không đổi**: sync, lưu `cv_analyses`.

**`GET /cv-analysis/{id}`** → `CvAnalysisResponse` (403/404) · **`GET /cv-analysis`** → `CvAnalysisResponse[]` của user — **keyset-paged** (`?cursor=&limit=`, mặc định/tối đa 500; header `X-Next-Cursor`; body vẫn là mảng JSON).
- Shape mỗi item **giữ nguyên** (đủ `summary`/`strengths`/`weaknesses`/`suggestions`/`jdMatch`), cố ý: FE hiện **không có màn chi tiết** — trang danh sách chính là chi tiết, render đủ các field này inline. Cắt bớt sẽ làm văng runtime (`string[]` non-optional duyệt bằng `@for`), không phải chỉ thiếu chữ. Muốn list gọn thật thì phải làm trang chi tiết trước (BE + FE cùng nhịp).

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

**`GET /roadmaps`** → `RoadmapSummaryResponse[]` của user — **keyset-paged** (`?cursor=&limit=`, mặc định/tối đa 500; header `X-Next-Cursor`; body vẫn là mảng JSON) · **`GET /roadmaps/{id}`** → `RoadmapResponse` đầy đủ. Lỗi: **401** · **403** · **404**.
- `RoadmapSummaryResponse` = `{ id, jobCategory, level, cvId, status, createdAt, completedAt }` — **KHÔNG** có `milestones`. Trước đây list `Include(Milestones).ThenInclude(Lessons)` nên payload nhân theo cây cho một màn hình chỉ vẽ tiêu đề/ngày/trạng thái. Cần cây đầy đủ (kèm `theoryContent`) → gọi `GET /roadmaps/{id}`.
- ⚠ Model FE (`roadmap.models.ts`) hiện khai **một** interface `RoadmapResponse` dùng chung cho cả list lẫn detail, với `milestones` **required**. Runtime không vỡ (trang danh sách không đọc field đó) nhưng model đang lệch thực tế — tách interface list/detail khi FE có dịp chạm vào.

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
- Req: `{ "transcript": string, "rubricVersion": int, "scores": [{ "criterionId": uuid, "score": number, "reasoning": string? }], "sampleAnswer": string?, "deliveryMetrics": object?, "promptVersion": int? }`.
- **Idempotent**: xóa điểm cũ cùng `(attemptNo, rubricVersion)` rồi ghi lại. Res **`200/204`**. Lỗi: **401** (sai token) · **404**.
- **F13 `sampleAnswer` (optional)** — câu trả lời mẫu mức tối đa cho đúng câu hỏi, do CÙNG lượt chấm sinh. Quy tắc ghi vào `practice_answers.sample_answer`: **attempt 1 ghi đè** (temp=0 = bản chọn ⇒ retry idempotent) · **attempt 2..N (E10) chỉ điền khi trống** (không để nội dung nhảy theo attempt) · **rỗng/thiếu KHÔNG xoá bản đang có** và **KHÔNG làm hỏng lượt chấm** (worker/image cũ không gửi vẫn chấm bình thường — PAY-13).
- **F11 `deliveryMetrics` (optional)** — chỉ số ĐỘ TRÔI CHẢY đo từ mốc thời gian Whisper (`{ speechRateWpm, longestPauseSec, pauseCount, silenceRatio, fillerCount, fillerBreakdown{} , … }`), lưu vào `practice_answers` (6 cột). **null KHÔNG xoá bản đã lưu**: đường THÍCH ỨNG đã ghi chỉ số từ `/decide-next`, nên worker/image CŨ callback với `null` mà ghi đè là **xoá mất số đo đúng**. Thiếu chỉ số **KHÔNG** làm hỏng lượt chấm (PAY-13).
- **BK23 `promptVersion` (optional)** — con dấu phiên bản prompt của **chính lượt chấm này**, do worker chụp **tại chỗ dựng prompt** (không phải đọc lại sau). Ghi lên **mỗi dòng** `answer_scores` (per-attempt, xem §Versioning). `null` = worker cũ / không biết · `0` = bản mặc định thuần · số **âm** → chuẩn hoá về `null` + log WARN (`version > 0` có CHECK ở DB nên âm chỉ có thể là worker lệch hợp đồng; lưu rác vào cột kiểm toán tệ hơn để trống). **Thiếu/hỏng con dấu KHÔNG BAO GIỜ làm answer `Failed`** — biến một cột audit thành đường mất credit (PAY-13) là đổi chác tồi.

**`POST /internal/answers/{answerId}/failed`** — đánh dấu `Failed` (lỗi chấm vĩnh viễn).
- Req: `{ "reason": string }`. Nếu answer đã `Scored` → **bỏ qua** (không hạ `Failed`). Res **`200/204`**. Lỗi: **401** · **404**.

**`POST /internal/sessions/exists`** (DB18 · **R1**) — PaymentService dò chỗ giữ credit mồ côi / chưa settle.
- Req: `{ "sessionIds": uuid[] }`. Res **200**: `{ "existingIds": uuid[], "states": [{ "sessionId": uuid, "status": string }] }`. Lỗi: **401** (sai token). Input null/rỗng → 200 với cả hai mảng rỗng (không chạm DB).
- **`states` = R1, ADDITIVE.** `status` là **tên enum `SessionStatus` dạng string** (GEN-2), KHÔNG phải số thứ tự — Payment đối chiếu Ordinal với `"Scored"`/`"SessionAbandoned"`/`"Failed"`, nên phát số thứ tự sẽ khiến Payment SKIP sạch và R1 thành **no-op IM LẶNG** (không lỗi, không log, chỗ giữ tiếp tục rò).
- **Hai mảng phủ ĐÚNG CÙNG tập session** (`existingIds` suy ra từ `states` — một nguồn). Lệch tập ⇒ Payment thấy "tồn tại mà thiếu status" ⇒ SKIP oan đúng những chỗ giữ cần dọn.
- ⚠ **`existingIds` GIỮ NGUYÊN nghĩa và KHÔNG được bỏ**: đó là trường Payment bản cũ đọc, và cũng là nguồn chân lý duy nhất cho "session có tồn tại không". Xem `payment.md` §R1 để biết vì sao Payment **không** được suy tồn-tại từ `states`.

### Validation & mã lỗi (tổng hợp — chi tiết per-endpoint ở trên)
| Field | Ràng buộc |
|---|---|
| `cvId`/`jdId` (create session) | optional; `FileRecord` phải **của chính user** + có `parsed_text` (không đọc được → 400) |
| `jdText` (create session · cv-analysis) | optional; **ưu tiên hơn `jdId`** (C11); rỗng/khoảng trắng = không gửi; **≤ 20.000 ký tự** (đo SAU trim) — vượt → **400** kèm giới hạn + độ dài đang gửi. Ngưỡng CHUNG với B2B/Campaign (`Isas.Shared.Validation.TextInputLimits.JdTextMaxChars`); guard chạy **NGAY ĐẦU** cả 2 endpoint — **trước** đọc CV/JD và **trước** reserve credit ⇒ JD quá dài không giữ credit oan (mẫu BK6/PAY-5) |
| `jobCategory` | bắt buộc, enum `BA·BE·FE` |
| upload file | PDF (cv/jd) **≤10MB** · audio (answer) **≤50MB**; sai loại/size → 400. Audio: định dạng ngoài allowlist (webm/ogg/mp3/m4a/mp4/flac/wav) → **400** — BK27 |
| `questionId`/`durationSec` (answer) | bắt buộc; **1 answer/câu** (upload lại = ghi đè idempotent) |
| callback `/internal/*` | `X-Internal-Token` đúng (sai → 401) |

| Mã | Khi nào (đặc thù — chung [../architecture.md](../architecture.md) §6) |
|---|---|
| 400 | CV/JD không đọc được nội dung · AI trả rỗng · thiếu field · file quá lớn/sai loại · `jdText` **> 20.000 ký tự** |
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
scoring_scope_version int?  ✅ **Chấm theo phạm vi** (migration `AddScoringScopeAndQuestionTargets`) — con dấu thước đo: điểm buổi này tính trên TOÀN BỘ rubric hay trên tập tiêu chí riêng từng câu. `null`=KHÔNG BIẾT (row có trước cột) · `1`=đã biết, chấm đủ rubric · `2`=đã biết, có ≥1 câu chấm trên tập HẸP HƠN. Xem §Chấm theo PHẠM VI câu hỏi
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
target_criterion_ids     jsonb?        ✅ **Chấm theo phạm vi** (migration `AddScoringScopeAndQuestionTargets`) — tiêu chí NỘI DUNG câu hỏi này nhắm tới (AIService gắn nhãn lúc sinh). **3 trạng thái load-bearing**: `null`=chưa hỏi/không đủ tin → chấm ĐỦ rubric · `[]`=đã hỏi, câu không nhắm nội dung nào → chỉ 4 tiêu chí CÁCH NÓI · non-empty=cách nói + đúng tiêu chí liệt kê. ⚠ **TUYỆT ĐỐI không quy `[]` về `null`**. Nullable ⇒ né bug jsonb-default F15
created_at               timestamptz
                             UNIQUE (session_id, order_no)
                             UNIQUE (generated_from_answer_id) WHERE generated_from_answer_id IS NOT NULL
```
> **Phỏng vấn THÍCH ỨNG (INT-17):** `practice_sessions` thêm `adaptive_enabled bool` (toggle theo buổi; tắt = luồng batch tĩnh cũ) + `max_follow_ups int`/`max_questions int` (trần; 0 = không trần cứng). Migration `AddAdaptiveInterviewColumns` (reversible; `ADD COLUMN kind DEFAULT 'Seed'` backfill an toàn; index add online-safe).
>
> **INT-17b — chuỗi đào sâu theo từng câu gốc:** `practice_questions` thêm `depth int NOT NULL DEFAULT 0` + `root_question_id uuid NULL` (ref lỏng cùng bảng, **KHÔNG FK** — tránh cascade path phụ, cùng lý do `generated_from_answer_id`) + index `(session_id, root_question_id, depth)`; `practice_sessions` thêm `max_deep_per_question int NOT NULL DEFAULT 0` (**0 = chế độ cũ ⇒ kill-switch `Adaptive:MaxDeepPerQuestion=0` chỉnh được lúc chạy, không cần deploy lại — nhưng CHỈ áp cho session TẠO MỚI**: giá trị được đóng dấu lúc tạo session và không có endpoint/hàm/script nào đổi được cho buổi đang chạy. Sửa bằng SQL tay cho session đã tạo còn làm **lệch stride** ⇒ câu đào sâu nhận `order_no` trùng câu gốc kế ⇒ vi phạm UNIQUE `(session_id, order_no)`) + `adaptive_failures int NOT NULL DEFAULT 0`. Migration `AddQuestionChainDepthInt17b` (reversible — mọi cột đều là dữ liệu dẫn xuất).
> ⚠ **Backfill BẮT BUỘC** (recursive CTE dựng lại cây từ `generated_from_answer_id` → `practice_answers.question_id`): production đang có buổi adaptive chạy dở, để `depth` ở mặc định 0 thì mọi câu ĐÃ đào sâu bị coi là câu gốc ⇒ chuỗi được đào thêm 3 tầng nữa. SQLite/`EnsureCreated` không chạy migration ⇒ câu SQL này **không có test nào phủ**, phải verify trên Postgres throwaway (mẫu L3 của DB15).

> **T7 entitlement stamp:** khi `Tiering:Enabled=true`, B2C resolve đúng một lần `GET Payment /internal/entitlements?ownerType=User&ownerId=…` (internal token, timeout/non-2xx/JSON lỗi → warning + Free local), rồi đóng dấu `entitlement_source`, `tier_code`, `tier_rank`, `adaptive_enabled`, `max_questions` (≤20), `max_follow_ups`, `grounding_enabled`, `self_consistency_n`, `cv_analysis_included`, `repo_analysis_included`, `roadmap_enabled`. Session cũ (`entitlement_source=legacy`) tiếp tục dùng config cũ; session mới không đọc tier lại giữa buổi. CV/repo/roadmap là thao tác độc lập nên resolve entitlement tại thao tác và trả 403 nếu plan khóa.

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
scoring_scope varchar(24)  ✅ **Chấm theo phạm vi** (migration `AddScoringScopeAndQuestionTargets`) — NOT NULL DEFAULT `'Always'`, enum `ScoringScope` lưu string (GEN-2). `Always`=chấm MỌI câu (tiêu chí CÁCH NÓI) · `WhenTargeted`=chỉ chấm khi câu hỏi nhắm tới (tiêu chí NỘI DUNG)
                           index (job_category, version, is_active) · index (candidate_id, job_category, is_active) [BC16]
                           CHECK ck_rubric_criteria_single_owner: campaign_id IS NULL OR candidate_id IS NULL  ✅ DB19 (2026-07-17) — cấm both-set; 3 trạng thái loại trừ (campaign-only B2B · candidate-only B2C · both-null seed)
                           CHECK ck_rubric_criteria_scoring_scope: scoring_scope IN ('Always','WhenTargeted')  ✅ Chấm theo phạm vi
```
> **Nhận diện nhóm "cách nói" CHỈ được đọc từ cột `scoring_scope`** — KHÔNG khớp theo **tên** tiêu chí: rubric tồn tại ở cả `vi` lẫn `en` (F12) và candidate tự đặt tên rubric riêng (BC16), nên so tên là hợp đồng gãy ngay khi ai đó đổi một chữ. Cũng KHÔNG khớp theo id seed (rubric riêng mang GUID mới).
> **Mặc định `Always` = hành vi trước thay đổi này** ⇒ mọi row không phân loại tường minh (rubric riêng BC16 · tiêu chí campaign B2B · row cũ) tự động an toàn. Chiều mặc định an toàn là **chấm thừa**, không phải bỏ chấm: bỏ chấm nhầm = tiêu chí biến mất khỏi kết quả mà không ai thấy.
> **BC16 — resolve rubric B2C:** scoring chọn tiêu chí theo `(candidate_id, job_category)`: có rubric riêng active của candidate → dùng nó, **else** seed mặc định (`candidate_id IS NULL`). Dùng chung `B2CRubricScope.ResolveOwnerAsync` ở cả 4 chỗ chấm (publish · callback guard · republisher · breakdown BC9) để không lệch. Sửa rubric = **soft-versioned** (deactivate bản cũ + thêm bản mới `is_active`, KHÔNG hard-delete vì `answer_scores` FK Restrict).

> **F12 (FR03) — 2 tiêu chí NGÔN NGỮ trong seed** (migration `AddLanguageRubricCriteriaF12`): mỗi nghề BA/BE/FE lên **6** tiêu chí seed thay vì 4 *(⚠ cập nhật: nay là **7** — F11 thêm "Độ trôi chảy & tự tin" 0.10; xem bảng phân loại ở §Chấm theo PHẠM VI câu hỏi)* — thêm **"Ngữ pháp & dùng từ"** (0.10) + **"Thuật ngữ chuyên ngành"** (0.10), 4 tiêu chí cũ hạ weight giữ **Σ=1**. Tách khỏi "Giao tiếp & trình bày" vì tiêu chí đó chấm **mạch lạc nội dung**, không phải ngữ pháp. Mô tả tiêu chí "Thuật ngữ" **neo theo nghề** (BA: user story/acceptance criteria… · BE: transaction/idempotent/ACID… · FE: hydration/reflow/debounce…) — không có ví dụ thuật ngữ riêng thì AI không phân biệt được sai-thuật-ngữ theo nghề.
> ⚠ **Transcript là ASR (Whisper)**: chính tả/dấu câu/tên riêng phiên âm sai là lỗi bộ nhận dạng, KHÔNG của ứng viên → mô tả tiêu chí chỉ neo vào thứ sống sót qua ASR (chọn từ · cấu trúc câu · từ đệm · thuật ngữ), và `build_scoring_prompt` có **1 dòng cấm trừ điểm lỗi ASR ở MỌI tiêu chí**. Bỏ dòng đó ⇒ tiêu chí ngữ pháp đo chất lượng Whisper chứ không đo ứng viên.
> ⚠ **BC16 không đổi:** candidate đã có rubric RIÊNG **không** tự nhận 2 tiêu chí mới (rubric riêng là lựa chọn của họ; muốn có thì tự thêm qua `PUT /rubrics/{jobCategory}`, hoặc `DELETE` để về seed).
> ⚠ **INT-9:** thêm tiêu chí vào rubric mà đường publish và đường callback không chọn **cùng** bộ ⇒ AI chấm thiếu tiêu chí ⇒ answer `Failed` hàng loạt. Cả 2 đường đi qua `LoadActiveCriteriaAsync`/`B2CRubricScope` nên luôn khớp; `LanguageRubricCriteriaTests` khoá hợp đồng này (publish 6 → callback 6 → `Scored`).

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
resources           jsonb         ✅ F15 (migration AddLessonResourcesF15) — tài liệu học gợi ý:
                                  [{title, type, publisher?, url?}]. NON-NULL, mặc định [] (rỗng
                                  là HỢP LỆ). Sinh CÙNG lượt với theory_content, ghi CÙNG 1 lần
                                  UPDATE (guard idempotent chỉ nhìn theory_content ⇒ tách 2 lần
                                  ghi sẽ để lại lesson "có theory, resources rỗng" vĩnh viễn).
session_id          uuid?         FK → practice_sessions (Restrict) — session luyện gắn lesson (set khi /start)
status              varchar(16)   enum: Theory·Practicing·Done
```
> **F15 (FR09) — tài liệu học & URL do AI sinh.** LLM sinh URL là **đoán chuỗi trông giống link**, không phải tra cứu; link bịa trông y hệt link thật. Rủi ro nặng KHÔNG phải "link 404" mà là **tên miền bịa** (có thể đã bị người khác đăng ký / typosquat) — ta sẽ đang đẩy người học tới đó dưới danh nghĩa "tài liệu hệ thống gợi ý".
> **Chốt: allowlist TÊN MIỀN** ở AIService (`app/resources.py`, hàm `sanitize_resources`): giữ url chỉ khi **https** + host khớp **đầy đủ** một tên miền tài liệu chính chủ đã biết; host lạ → **bỏ url, GIỮ tên tài liệu** (người học vẫn tra được). Đã cân nhắc và loại: *(a)* không link gì cả — an toàn nhưng giảm mạnh giá trị FR09; *(b)* link tự do + ghi chú — ghi chú không ngăn được cú click, không chặn được domain bịa.
> ⚠ **Giới hạn phải nói thẳng:** allowlist bảo đảm đúng **tên miền**, KHÔNG bảo đảm **đường dẫn** tồn tại (ta không fetch để xác minh — sẽ thêm I/O mạng vào đường sinh lý thuyết đang chạy đồng bộ trong request người dùng). Vì vậy **FE BẮT BUỘC gắn nhãn "Tài liệu do AI gợi ý, chưa được kiểm chứng"** cạnh link — đó là phần bù cho giới hạn này. Muốn bỏ hẳn link: đặt allowlist rỗng, không cần sửa chỗ nào khác.
> ⚠ `url` **null là mục HỢP LỆ**, không phải dữ liệu hỏng — đừng "dọn" mục thiếu url, làm vậy là xoá tài liệu chỉ vì nó không có link tin cậy.
> ⚠ **Migration `AddLessonResourcesF15`:** EF scaffold ra `defaultValue: ""` — **chuỗi rỗng KHÔNG phải JSON hợp lệ**, Postgres từ chối ngay tại `ALTER TABLE`. Đã sửa tay thành `"[]"`. SQLite (test, `EnsureCreated`) bỏ qua migration nên **test không bắt được lỗi này** — chỉ lộ lúc apply Postgres thật.

### Index & ràng buộc (tổng hợp)
- **FK on-delete**: Cascade theo `session_id` → `practice_questions` · `practice_answers` (→ `answer_scores` Cascade) · `session_criterion_scores`. `cv_id`/`jd_id` → `file_records` **Restrict** (chặn xoá file đang gắn session). `answer_scores.criterion_id` → `rubric_criteria` **Restrict**. `rubric_levels` Cascade *(bảng `rubric_anchors` đã DROP — DB15, gộp vào `rubric_levels.example_answers` jsonb)*. ✅ Roadmap: Cascade theo `roadmap_id` → `roadmap_milestones` (→ `roadmap_lessons` Cascade); `roadmaps.cv_id` → `file_records` **Restrict** · `roadmap_lessons.session_id` → `practice_sessions` **Restrict**.
- **UNIQUE**: `practice_questions(session_id, order_no)` · `practice_answers`: 1 answer/câu enforce qua UNIQUE `question_id` (1-1 FK) — ✅ **DB15 bỏ index UNIQUE trùng `(session_id, question_id)`, thay non-unique `(session_id)`** giữ leading-col cho sweeper/EXISTS · `answer_scores(answer_id, criterion_id, attempt_no)` · `session_criterion_scores(session_id, criterion_id)` · `rubric_levels(criterion_id, score)` · ✅ `roadmap_milestones(roadmap_id, order_no)` · `roadmap_lessons(milestone_id, order_no)`.
- **Index**: `practice_sessions(candidate_id)` + `(campaign_id)` · `rubric_criteria(job_category, version, is_active)` · `file_records(user_id)` · ✅ `roadmaps(candidate_id)`.
- **CHECK**: `rubric_criteria` — `ck_rubric_criteria_single_owner` (DB19) · ✅ `ck_rubric_criteria_scoring_scope` (`scoring_scope IN ('Always','WhenTargeted')`).
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
- ✅ **Chấm theo PHẠM VI:** bộ tiêu chí gửi vào job = 4 tiêu chí `Always` (cách nói) + đúng tiêu chí `WhenTargeted` mà **câu hỏi này** nhắm tới (`ScoringScopeFilter`); câu không có nhãn → nguyên bộ. Spec ở **§Chấm theo PHẠM VI câu hỏi**.

### Republish answer kẹt (`StuckAnswerRepublisher`)
Quét mỗi **2 phút**, chỉ session `InProgress`/`Scoring`, answer có audio:
- `Uploaded` + `last_scoring_published_at=null` quá **2 phút** (CreatedAt) → publish hụt → đẩy lại.
- đã `Scoring` quá **15 phút** không callback → worker mất tích → đẩy lại.
- Đẩy lại OK → set `Scoring` + dời `last_scoring_published_at=now`. Answer `Failed`/`Scored` **không** bị nhặt.
- ⚠ **Phải đi CHUNG luật với đường publish** — projection bắt buộc mang `Question.TargetCriterionIds` rồi gọi cùng `ScoringScopeFilter.Apply`. Thiếu vế này thì answer nào phải nhờ republisher cứu sẽ bị chấm **đủ rubric** trong khi answer chạy trơn tru được chấm đúng phạm vi ⇒ lệch âm thầm, chỉ xảy ra lúc đã có sự cố (đúng chỗ **F11** từng dính). Cùng lý do với `DeliveryMetrics`/`TranscriptEngine` trong projection.

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
- **✅ Chấm theo PHẠM VI câu hỏi:** không phải câu nào cũng chấm đủ rubric — tiêu chí NỘI DUNG chỉ được chấm khi câu hỏi thực sự nhắm tới (`rubric_criteria.scoring_scope` + `practice_questions.target_criterion_ids`). Spec ở **§Chấm theo PHẠM VI câu hỏi** dưới.

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

### Chấm theo PHẠM VI câu hỏi — ✅ (migration `AddScoringScopeAndQuestionTargets`, đã apply prod)
> **Vấn đề gốc:** mọi câu trả lời bị chấm trên **TOÀN BỘ** rubric, nên câu hỏi hẹp ("giải thích cơ chế xoay vòng refresh token") vẫn bị chấm "Thiết kế hệ thống & CSDL" và ăn điểm thấp **chỉ vì không được hỏi**. Đo trên deploy: cùng trình độ, bài trả lời câu hỏi hẹp ~69/100 còn bài "đại luận" 91–97.

**Phân loại tiêu chí (`rubric_criteria.scoring_scope`)** — seed B2C có **7 tiêu chí/nghề/ngôn ngữ**:

| Nhóm | `scoring_scope` | Tiêu chí seed | Vì sao |
|---|---|---|---|
| **CÁCH NÓI** (4) | `Always` | Giao tiếp & trình bày · Ngữ pháp & dùng từ (F12) · Thuật ngữ chuyên ngành (F12) · Độ trôi chảy & tự tin (F11) | Quan sát được ở **bất kỳ** câu trả lời nào |
| **NỘI DUNG** (3) | `WhenTargeted` | BA: Phân tích yêu cầu · Hiểu nghiệp vụ & stakeholder · Tư duy giải quyết vấn đề<br>BE: Chiều sâu kỹ thuật · Thiết kế hệ thống & CSDL · Giải quyết vấn đề & thuật toán<br>FE: Chiều sâu kỹ thuật · Giải quyết vấn đề · Ý thức UI/UX & accessibility | Chỉ đo được khi câu hỏi thực sự hỏi tới |

> **Seed:** 42 row = **18 `WhenTargeted`** (3 tiêu chí nội dung × 3 nghề × 2 ngôn ngữ) + **24 `Always`**. Id `vi`/`en` của cùng tiêu chí chỉ khác byte đầu (`B2CRubricSeed.EnglishId`: `bytes[0] ^= 0x11`).

**Luật lọc — `ScoringScopeFilter.Apply` (`null` ≠ `[]`, điểm sống còn):**

| `practice_questions.target_criterion_ids` | Bộ tiêu chí gửi vào lượt chấm |
|---|---|
| `null` — chưa hỏi / không đủ tin | **NGUYÊN bộ** (lùi an toàn, y như trước thay đổi này) |
| `[]` — đã hỏi, câu không nhắm nội dung nào | **Chỉ tiêu chí `Always`** |
| non-empty | Tiêu chí `Always` **+** đúng những tiêu chí được nhắm |

- ⚠ **Gộp `[]` vào `null` làm tính năng NO-OP đúng ở nhóm câu cần nó nhất** — câu xã giao ("giới thiệu bản thân") vẫn bị chấm "Thiết kế hệ thống & CSDL", chính là hình dạng lỗi mà cả thay đổi này sinh ra để diệt.
- **LÙI AN TOÀN cuối:** lọc xong mà **rỗng** → trả nguyên bộ (+ log warning). Bộ rỗng KHÔNG vô hại: cả hai caller đều **bỏ publish** khi không có tiêu chí ⇒ answer không bao giờ được chấm ⇒ buổi không bao giờ đóng ⇒ người luyện **mất 1 credit** (PAY-13). Với tới được khi rubric riêng (BC16) bị sửa **giữa buổi** (bản cũ deactivate, id trong nhãn trỏ vào tiêu chí không còn active) hoặc rubric không có tiêu chí `Always` nào.
- **INT-9 KHÔNG phải sửa:** guard "LLM chấm thiếu tiêu chí" so với danh sách **được gửi vào**, không phải toàn rubric ⇒ gửi ít hơn vẫn hợp lệ.
- `RubricVersion` của job đọc từ bộ **ĐẦY ĐỦ** (không phải bộ đã lọc) ⇒ con số này không đổi theo phạm vi chấm của từng câu.

**Nguồn nhãn — `AiServiceQuestionGenerator.ParseTargets`:** AIService trả `targetCriteria[]` theo **index câu**; .NET parse GUID, **drop id lạ** (guard by-construction, mẫu `GroundingMapper`) — chỉ nhận id nằm trong tập tiêu chí **chính ta vừa gửi đi** ⇒ AIService không thể bịa tiêu chí ngoài rubric để lái phạm vi chấm. ⚠ **"Toàn id lạ" → `null`, KHÔNG phải `[]`**: AIService vừa khẳng định câu này *có* nhắm tiêu chí, chỉ là gọi tên thứ không thuộc rubric ⇒ ta không có tín hiệu đáng tin nào để thu hẹp, khác hẳn lời khẳng định "không nhắm gì cả".

**HAI đường đẩy job chấm dùng CHUNG một luật** — `AnswerService.TryPublishScoringJobAsync` (publish lúc upload) và `StuckAnswerRepublisher` (đẩy lại khi kẹt) đều gọi `ScoringScopeFilter.Apply`, và republisher **phải mang `target_criterion_ids` trong projection**. Hai đường lệch nhau là lỗi **chỉ lộ ra khi đã có sự cố**: answer nào phải nhờ republisher cứu sẽ được chấm theo luật KHÁC answer chạy trơn tru, mà không có gì báo — đúng hạng lỗi **F11** đã dính ở chính cặp đường này.

**Câu ĐÀO SÂU thừa kế nhãn của câu cha** (`AnswerService`) — và **đúng do cấu trúc**, không phải xấp xỉ cho rẻ:
- `follow_up`/`clarify` theo định nghĩa đào sâu vào **chính câu trả lời vừa rồi** ⇒ vẫn là chủ đề câu cha.
- `new_question` (đổi chủ đề) ở **chế độ chuỗi** đã bị chặn từ trước (`endsChain` — INT-17b không append) nên không bao giờ tới đây; ở **chế độ frontier** (kill-switch `MaxDeepPerQuestion=0`) nó tới được, và lúc đó thừa kế SẼ SAI ⇒ chỉ thừa kế cho **2 loại đào sâu**, `new_question` để `null` = chấm đủ rubric (an toàn).
- ⚠ Không thừa kế thì `/decide-next` không trả nhãn ⇒ **mọi** câu đào sâu chấm cả rubric. Prod chạy chế độ chuỗi nên **phần lớn câu trong một buổi là câu đào sâu** — thiếu vế này thì tính năng chỉ có hiệu lực trên câu gốc, tức gần như không có hiệu lực.

**Con dấu `practice_sessions.scoring_scope_version`** — thu hẹp phạm vi làm điểm **KHÔNG CÒN SO SÁNH ĐƯỢC** với điểm cũ, mà **BC15** (đo cải thiện) · **F14** (mốc peer) · **CAMP-10** (xếp hạng) đang đem so thẳng. Tiền lệ: `practice_answers.metrics_version` (F11), `answer_scores.prompt_version` (BK23).
- `null` = **KHÔNG BIẾT** (row có trước cột). ⚠ **KHÔNG được suy ra "khác phiên bản" từ `null`** — suy "khác" từ "không biết" là bịa (BK23).
- `1` = đã biết: chấm trên toàn bộ rubric. Đóng cho **B2B** (câu do HR/Campaign cấp, không đi đường gắn nhãn) và buổi B2C không câu nào mang nhãn.
- `2` = đã biết: có ≥1 câu mang nhãn ⇒ tồn tại answer chấm trên tập **hẹp hơn**. **Chỉ giá trị này mới chứng minh được "khác thước đo".**
- Đóng theo **sự thật quan sát được** (`questions.Any(q => q.TargetCriterionIds is not null)`, kể cả `[]`) chứ không theo "code đã hỗ trợ nhãn" — đóng dấu `2` cho buổi không nhãn là báo động giả cho BC15/F14/CAMP-10.
- Đóng ở **tầng BUỔI** (không phải từng dòng điểm) vì (a) mọi phép so trên đều đọc số liệu mức buổi, (b) `answer_scores` là bảng lớn nhất hệ (~100M dòng ở quy mô mục tiêu).

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

**Schema thêm (migration):** `answer_scores.level_matched int?` (E9) · `practice_answers.needs_review bool default false` (E10) · `practice_answers.sample_answer text?` (F13, migration `AddAnswerSampleAnswerF13`) · **F11** `practice_answers.speech_rate_wpm/filler_count/pause_count/longest_pause_sec/silence_ratio double|int?` + `filler_breakdown text?` (JSON, **không phải jsonb** — chỉ đọc-hiển-thị, không truy vấn theo khoá; migration `AddDeliveryMetricsAndFluencyCriterionF11`). **DTO:** `AnswerScoreResponse` thêm `levelMatched?`; `AnswerResponse` thêm `needsReview` + `sampleAnswer?` + `deliveryMetrics?`. Đều nullable/thêm field → **không phá** client.

**F11 (FR06) — chấm ĐỘ TRÔI CHẢY + phát hiện từ đệm.** ✅ Không phải "thêm 1 tiêu chí rubric": trước F11 hệ thống thu âm → transcribe → **vứt toàn bộ tín hiệu âm thanh**, nên chấm trôi chảy chỉ là đoán từ chữ. Nay AIService giữ mốc thời gian segment → đo **tốc độ nói / khoảng lặng / từ đệm** (chi tiết + vì sao KHÔNG bật `word_timestamps`: [ai.md](ai.md) §F11) và số đo đi kèm transcript suốt cả hai đường.
- **Hai đường phải cùng mang chỉ số** — tĩnh (worker tự transcribe + tự đo) và thích ứng (`/decide-next` đo, worker **bỏ Whisper**). Thiếu một đường thì buổi loại đó không có chỉ số mà **KHÔNG lỗi nào nổ** ⇒ ba call site đều được khoá bằng test: vòng adaptive lưu chỉ số · publish đẩy vào `ScoringJob` · **`StuckAnswerRepublisher`** đẩy lại bản đã lưu (republisher không gọi lại AIService).
- **Tiêu chí seed thứ 7 "Độ trôi chảy & tự tin"** (0.10, cả 3 nghề; 6 tiêu chí cũ hạ weight giữ **Σ=1**). Tách khỏi "Ngữ pháp & dùng từ" (F12) và "Giao tiếp & trình bày" vì hai cái đó chấm thứ đọc được TRONG CHỮ, còn cái này chấm CÁCH NÓI — chỉ tồn tại trong âm thanh. Mô tả cấm lấn sang chấm nội dung (*nói chậm ≠ kiến thức kém*).
- ⚠ **`fillerCount` là mức TỐI THIỂU** — Whisper nuốt bớt từ đệm ⇒ "0 từ đệm" **KHÔNG** phải lời khen. Chỉ số **thời gian** đáng tin hơn (tiếng "ừm" bị nuốt vẫn chiếm thời gian ⇒ vẫn hiện ở khoảng lặng/tốc độ nói). FE phải phân biệt **null = chưa đo được** với **0 = đo ra 0**.
- ⚠ **BC16**: người đã có rubric RIÊNG **không** tự nhận tiêu chí mới (đúng thiết kế) ⇒ FR06 chỉ phủ nhóm dùng rubric seed — cùng nhóm với ghi chú F12/FR03.
- ⚠ **token/lượt tăng thêm** (7 tiêu chí thay vì 6 + khối chỉ số trong prompt), nhân `SelfConsistencyN` nếu bật → liên quan `F22`.

**F13 (FR07) — câu trả lời MẪU chuyên nghiệp.** ✅ Mỗi câu đã chấm kèm 1 câu trả lời mẫu mức tối đa, bám **đúng câu hỏi + rubric của chính buổi đó** + bù chỗ ứng viên còn thiếu (không phải văn mẫu chung).
- **Sinh LÚC CHẤM, trong CÙNG một call Gemini** (thêm `sampleAnswer` vào `response_schema` của `score()`) — KHÔNG phải lazy lúc user mở. Lý do: prompt chấm đã mang sẵn câu hỏi + rubric + transcript, nên phần tăng thêm **chỉ là output token (~250)**; gọi riêng lúc mở sẽ phải nạp lại **toàn bộ** ngần ấy input (~1.000+ token) ⇒ lazy chỉ rẻ hơn khi tỷ lệ mở kết quả **< ~20%**, mà buổi luyện B2C là thứ người dùng vừa trả 1 credit để làm. Thêm nữa: chấm chạy nền (không ai đợi), còn lazy sẽ đắp 5–15s Gemini vào đúng đường xem kết quả.
- **KHÔNG dính dáng `RubricLevel.ExampleAnswers`** — cái đó là anchor **đầu vào** để hiệu chỉnh AI lúc chấm, không bao giờ trả ra cho người dùng (và thực tế luôn rỗng vì không có write path nào ghi `RubricLevel`).
- **AI-4:** transcript vẫn nằm trong delimiter dữ liệu; prompt cấm tường minh việc chép chỉ thị từ phần ứng viên vào mẫu và cấm để việc soạn mẫu đổi điểm đã chấm.
- **Upload lại (INT-3) xoá `sample_answer`** cùng transcript/scores — gợi ý bám bài cũ, giữ lại là khuyên về một bài không còn tồn tại.

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

## F14 (FR08) — So sánh với mốc đối chiếu (radar 2 lớp)

Màn kết quả buổi luyện B2C trước đây chỉ vẽ điểm **của chính người dùng** — không có gì để đối chiếu. F14 thêm **lớp thứ hai** (đường đứt nét) lên radar: điểm của bạn vs mốc.

> ⚠️ **HỆ THỐNG KHÔNG CÓ DỮ LIỆU "CHUẨN NGÀNH".** Không mua bộ benchmark, không tích hợp nguồn ngoài. Vì vậy mốc **chỉ** đến từ hai nguồn có thật, và **nhãn phải nói đúng nguồn**. Gắn chữ "chuẩn ngành" lên trung bình nội bộ / ngưỡng nội bộ là nói dối người dùng về độ tin cậy của đường kẻ họ đang nhìn — có test khoá điều này (`NhanKhongBaoGioNoiLaChuanNganh`).

| `source` | Là gì | Nhãn trả về |
|---|---|---|
| `PeerAverage` | Trung bình % của **người dùng KHÁC** trên chính hệ thống: cùng `job_category`, buổi **B2C** đã `Scored` | `Trung bình người luyện cùng vị trí (n=N)` |
| `PassThreshold` | **Ngưỡng đạt NỘI BỘ** = `Scoring:ImprovementThresholdPct` — đúng ngưỡng đang quyết định tiêu chí nào bị gắn `needsImprovement` trên chính màn hình đó | `Ngưỡng đạt nội bộ (50%)` |

**Bốn quyết định dễ bị "sửa cho gọn", ghi lại lý do** (`CriterionBenchmarkService`, đều có mutation-check):
1. **Loại chính mình khỏi mẫu.** So mình với tập có chứa mình là vòng tròn; ở ca hệ thống mới có 1 người dùng thì tập đó **chính là họ** ⇒ mốc trùng khít điểm của họ — vô nghĩa nhưng nhìn rất thuyết phục. Loại bản thân khiến ca đó tự rơi về `n=0` → ngưỡng nội bộ.
2. **Gom theo TÊN tiêu chí, không theo id.** BC16 cho candidate rubric riêng ⇒ cùng một tiêu chí nhưng `rubric_criteria.id` khác nhau giữa các người. Gom theo id thì nhóm dùng rubric riêng **vĩnh viễn `n=0`** — tính năng chết im lặng đúng với nhóm dùng nhiều nhất.
3. **Một nguồn cho CẢ radar, không trộn.** Thiếu mẫu ở **một** trục là cả biểu đồ rơi về ngưỡng nội bộ. Mỗi trục một nguồn thì đường đứt nét không còn nghĩa thống nhất và không chú thích trung thực được bằng một nhãn.
4. **Chỉ lấy buổi B2C.** Điểm B2B chấm theo tiêu chí campaign (thang/ngữ cảnh khác hẳn) — trộn vào là so hai thứ không cùng đơn vị.

**Read-time, KHÔNG migration.** Dựng trong `GET /practice/sessions/{id}` (cùng chỗ BC9/BC8). Không snapshot vào bảng: mốc đổi theo dữ liệu cộng đồng nên bản lưu sẽ lỗi thời ngay mà không ai chịu trách nhiệm làm mới.

**Config `Benchmark`:** `Enabled` (mặc định `true`; `false` → `benchmark = null` → radar về 1 lớp như trước F14) · `MinSampleSize` (mặc định **5** buổi của người khác **cho mỗi tiêu chí**).

**FE.** Dùng lại `RadarChart` của F3 (ECharts `renderer:'svg'`, lazy-import — đã verify `echarts` nằm ở lazy chunk, không lọt initial bundle). Thêm input `thresholdLabel` (mặc định giữ chuỗi cũ ⇒ trang báo cáo lộ trình không đổi). Radar chỉ vẽ khi **≥ 3 tiêu chí** (dưới 3 trục là hình thoi/đường thẳng, vô nghĩa) — ca ít tiêu chí vẫn thấy mốc qua **vạch trên thanh ngang**. Mốc ghép theo `criterionId`, **không theo thứ tự mảng** (ghép nhầm trục thì biểu đồ vẫn vẽ đẹp, không ai phát hiện).

**Hạn chế còn lại (cần team biết).** `PeerAverage` là trung bình **người dùng của chính sản phẩm này**, không phải mặt bằng ứng viên ngoài thị trường — mẫu lệch theo tập người dùng hiện có. Muốn có "chuẩn ngành" thật thì phải có nguồn dữ liệu ngoài; đó là việc khác, **không** phải đổi nhãn cho số hiện tại.

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

## F21 (FR17) — Quản lý System Prompt + nội dung lĩnh vực

**Interview sở hữu registry prompt** (AUTH-7: endpoint admin nằm trong service sở hữu dữ liệu). Lý do chọn Interview thay vì Payment (nơi F22 đặt `ai_usage_logs`): con dấu phiên bản prompt phải đóng lên `answer_scores` — bảng của Interview — trong cùng transaction. Cơ chế phía AIService + 4 tầng fail-open: [ai.md](ai.md) §Prompt tuỳ biến.

### API
| Method | Path | Ai gọi | Ghi chú |
|---|---|---|---|
| GET | `/api/admin/prompts` | Admin | Mọi khoá code khai, **kể cả khoá chưa ai sửa** (`body: null` = đang dùng bản mặc định trong `prompts.py`) |
| GET | `/api/admin/prompts/{key}/history` | Admin | Append-only ⇒ đây là dấu vết đầy đủ ai đổi gì, khi nào, vì sao |
| PUT | `/api/admin/prompts/{key}` | Admin | Tạo **version mới**; 400 khi khoá lạ / body rỗng / >8.000 ký tự / chứa delimiter khung |
| DELETE | `/api/admin/prompts/{key}` | Admin | Về bản mặc định = deactivate; **giữ lịch sử** |
| GET | `/internal/prompts` | AIService | `X-Internal-Token`, KHÔNG qua gateway (GEN-1). Trả **chỉ phần đã tuỳ biến** + `promptVersion` |

### DB — `prompt_templates`
`id · key(64) · version · body · is_active · updated_by · change_note(512) · created_at`
· UNIQUE `(key, version)` · UNIQUE partial `key WHERE is_active` · CHECK `version > 0`.

**APPEND-ONLY.** Sửa = deactivate bản cũ + insert `version+1` (mẫu BC16), **1 transaction**. Không UPDATE tại chỗ: điểm đã chấm đóng dấu `answer_scores.prompt_version`, nên sửa tại chỗ khiến con dấu đó trỏ tới một văn bản **khác** với văn bản thực sự đã chấm ⇒ dấu vết kiểm toán nói dối. Với thứ quyết định điểm của người trả tiền, "không truy lại được" là **hỏng**, không phải bất tiện.

### Versioning — vì sao cần dù đã có `rubric_version`
Rubric quyết định chấm **cái gì**; prompt quyết định chấm **như thế nào**. Đổi prompt chấm là **đổi thước đo** — điểm trước và sau không còn so sánh trực tiếp được, mà hệ thống đang dùng điểm để **xếp hạng ứng viên với nhau** (CAMP-10/E4) và tính **cải thiện theo thời gian** (BC15).

`answer_scores.prompt_version` nullable: `null` = chấm trước F21 · `0` = bản mặc định thuần · `>0` = tổng version các mảnh đang active. Gộp `null` với `0` là mất đúng thông tin cần để biết có so sánh được hay không.

✅ **Con dấu ĐÃ được ghi thật (BK23).** F21 để lại cột + `GetPromptVersionStampAsync()` nhưng **không đấu dây writer nào** ⇒ cột NULL trên mọi dòng, tức tính năng tự vô hiệu hoá lớp an toàn của chính nó. Nay AIService gửi kèm `promptVersion` trong callback chấm → đóng lên **từng dòng** `answer_scores`.

**Nguồn con dấu = AIService, KHÔNG phải Interview tự đọc DB lúc lưu.** AIService cache mảnh prompt theo TTL và **cố ý fail-open về cache CŨ** khi registry lỗi (F21 tầng 3), còn chấm thì bất đồng bộ qua RabbitMQ và có thể bị `StuckAnswerRepublisher` đẩy lại sau hàng giờ ⇒ "phiên bản đang trong DB lúc callback về" thường xuyên **khác** "phiên bản thực sự đã chấm". Con dấu sai còn **tệ hơn NULL**: cả lý do tồn tại của cột là trả lời *"hai điểm này có cùng thước đo không"*, mà con dấu nói dối thì nó trả lời **sai một cách tự tin** và không ai có cách nào biết. Giá phải trả: đổi hợp đồng callback — rẻ, đúng mẫu `sampleAnswer` (F13) / `deliveryMetrics` (F11).

**Con dấu là thuộc tính của ATTEMPT, không phải của answer.** Với `SelfConsistencyN > 1` (E10), 1 answer = N lượt gọi AI riêng, mỗi lượt refresh registry riêng ⇒ lưu **per-row**, không gộp. Prompt đổi giữa chừng là **thấy được**, không bị một giá trị "đại diện" nuốt mất.

⚠ **Attempt trộn hai phiên bản prompt → `needs_review`.** Điểm chốt là **median giữa các attempt**; trộn hai thước đo thì median vẫn ra một con số trông bình thường mà **không gì nói rằng nó vô nghĩa**. Xử bằng **cờ soi lại**: KHÔNG loại attempt (median mất mẫu, có khi còn 1) và KHÔNG `Failed` (mất credit PAY-13 vì một thao tác của admin — người trả tiền không liên quan). Dòng **khuyết** con dấu (worker cũ) **không** bị suy ra là "khác thước đo": `null` = *không biết*, suy "khác" từ "không biết" là bịa và sẽ gắn cờ oan hàng loạt đúng kiểu nhiễu khiến người ta tắt cờ rồi mất luôn tín hiệu thật.

⚠ **Còn lại của `BK23`:** chưa có cảnh báo ở tầng **bảng xếp hạng** khi ranking trộn hai `prompt_version` khác nhau (CAMP-10/E4) và ở **BC15** khi so cải thiện theo thời gian — nay đã **có dữ liệu** để làm, trước thì không.

### Ranh giới — F21 đóng theo nghĩa nào
✅ **"Sửa được NỘI DUNG của 3 ngành"** (BA/BE/FE): tên hiển thị · mô tả · hướng dẫn riêng theo nghề, hiệu lực ở lần sinh/chấm kế mà **không cần deploy**.

❌ **KHÔNG phải "thêm ngành tuỳ ý".** `JobCategory` vẫn là enum 3 giá trị. Đây là quyết định có chủ đích chứ không phải giới hạn kỹ thuật: `B2CRubricSeed` khoá theo enum (mỗi nghề 7 tiêu chí sau F11/F12), rubric riêng BC16 khoá theo nó, validation khoá theo nó. Một ngành **không có rubric** sẽ khiến `AnswerService` thấy 0 tiêu chí active ⇒ INT-9 *"thiếu tiêu chí → `Failed`"* ⇒ **người luyện trả 1 credit rồi nhận một buổi hỏng**. Mở tập ngành mà không mở kèm đường khai rubric là mở thẳng ra đường đó; tập đóng bảo đảm **mọi ngành chắc chắn có rubric**.

*(Ghi chú kỹ thuật cho lần mở sau: 4 cột lưu `JobCategory` đều đã là **string** trên đĩa — `HasConversion<string>()` — và wire contract cũng đã là string, nên mở tập ngành **không phải data migration**. Cái chặn chỉ là kiểu C# + validation, cộng với bắt buộc khai rubric kèm theo.)*

## Kho tri thức / Grounding (RAG) ✅ **D27 — 2026-08-01**
InterviewService làm chủ kho tri thức; ground **lớp SINH** (câu hỏi · lý thuyết · roadmap). Xem [ai.md](ai.md) §Grounding cho `/embed` + injection, [decisions.md](../decisions.md) D27 cho quyết định.

**DB (migration `AddKnowledgeAndGroundingRefs`, đã apply prod):**
- Bảng `knowledge_sources` (Postgres): `id · title · job_category(null=chung) · source_type(Context7|Url|Manual) · source_ref · raw_content(để reindex) · reputation · status · chunk_count · created_by · created_at`. Index `(created_at desc, id desc)` + `(job_category)`.
- **Chunk KHÔNG ở Postgres** — ở **Qdrant** collection `knowledge` (vector 768, Cosine, payload `{sourceId, jobCategory, ordinal, content, sourceUrl, sourceTitle}`).
- Cột thêm: `practice_questions.grounding_refs` + `roadmap_lessons.grounding_refs` (jsonb, **nullable** — `null`=không đi grounding, `[]`=ungrounded, non-empty=grounded; nullable né bug jsonb-default F15).

**Service:** `IVectorStore`/`QdrantVectorStore` (upsert · search filter jobCategory top-k=4 + **score-threshold** chống over-attribution · delete-by-sourceId) · `IChunker` (Context7 per-snippet · Url `ChunkHtmlByHeading` h1–h3 rồi window 350–500 token overlap 60–80 · Manual `##`) · `Context7Client` · `AiServiceEmbedder` (gọi `/embed`) · `KnowledgeService` (ingest = chunk→embed→upsert Qdrant→ghi row; retrieve = embed query→search→grounding; **degrade rỗng 1 điểm duy nhất**).

**Admin API** `api/admin/knowledge` `[Authorize(Roles="Admin")]` (FE `{apiBase}/interview/admin/knowledge`): `GET /` list (keyset) · `POST /` add Manual/Url · `DELETE /{id}` (**xóa Qdrant TRƯỚC rồi Postgres** — chống orphan vector) · `POST /{id}/reindex` · `GET /context7/search` · `POST /context7/ingest {libraryId, topics[], jobCategory}`.

**Wire sinh:** `PracticeService` retrieve theo jobCategory (query=jobCategory+focus+JD/CV) → truyền grounding vào `/generate-questions` → map `citedChunkIds`→`QuestionResponse.citations: [{chunkId, sourceUrl, sourceTitle}]` (`GroundingMapper` drop id lạ) → lưu `grounding_refs`. **Roadmap Cách 2 precompute:** `RoadmapService.CreateAsync` batch-embed tên bài (1 lần `/embed`) → lưu snapshot `roadmap_lessons.grounding_refs`; mở bài feed snapshot vào `/generate-lesson-theory` → **không retrieve realtime** (khỏi lo timeout 60s). Precompute/retrieval lỗi → `[]` (ungrounded, không vỡ).

**Context7 (`context7.com/api/v2/`, Bearer `Context7:ApiKey`):** shape v2 verify keyless — `/libs/search`→`results[]{id,title,trustScore,totalSnippets}`; `/context?libraryId=&query=&type=json`→`{codeSnippets[]{codeTitle,codeDescription,codeId=URL,codeList[].code}, infoSnippets[]{pageId=URL,breadcrumb,content}}`. ⚠ **Bẫy đã sửa (D27):** BaseAddress phải **trailing-slash** + path **không leading-slash** (nếu không `/api/v2` bị mất → 404); param `libraryId`/`query` (không `library`/`topic`); sourceUrl từ `codeId`/`pageId`. Khóa bằng `Context7ClientShapeTests` (feed JSON thật).

**Nhãn trích dẫn — LUÔN bắt đầu bằng tên nguồn admin curate** (`KnowledgeService.CitationLabel`): `source.Title`, hoặc `source.Title · <mục con>` khi chunk có `SectionTitle` khác tên nguồn. Áp cho **CẢ BA** `source_type`.
> ⚠ **Bẫy đã sửa:** trước đây nhãn là `SectionTitle ?? source.Title` — heading của chunk **ĐÈ** tên nguồn. Trên trang thật, heading thường là đồ trang trí điều hướng: đo trên corpus đã nạp, `"Help improve MDN"` xuất hiện **5 lần**, cạnh `"In This Article:"` và `"Format: 3-Part"`. Người dùng thấy trích dẫn tên "Help improve MDN" thì **không kiểm chứng được gì**, mà cả lý do tồn tại của citation là để kiểm chứng (D27). Tên nguồn đứng **TRƯỚC** nên nhãn không bao giờ vô nghĩa; mục con giữ phía sau để không mất vị trí trong tài liệu dài (MDN Web Performance: 57 chunk).
> ⚠ **Lần vá đầu BỎ SÓT nhánh Context7** (nó dùng thẳng tiêu đề snippet): đo trên prod sau khi reindex toàn bộ — **607/687 nhãn đúng, 80 sai**, và 80 **đúng bằng tổng chunk của cả 5 nguồn Context7** ⇒ nhánh đó sai **100%** (`"SET TRANSACTION"`, `"CREATE INDEX"`, `"Built-in React Hooks"`). Bất biến *"nhãn luôn bắt đầu bằng `source.Title`"* nay được test khoá theo vòng lặp trên `Enum.GetValues<KnowledgeSourceType>()` để nhánh thêm về sau không lọt lần nữa.

**Điểm uy tín (`reputation`) — SERVER tự tra, không nhận từ client:** `Context7IngestAsync` **và** `ReindexAsync` đều gọi `TryResolveContext7ReputationAsync` (chỉ với `source_type=Context7` + có `source_ref`; Manual/Url bỏ qua, không tốn lời gọi nào). Khớp bằng **ID ĐẦY ĐỦ** chứ không theo tên — search `"react"` trả **5 thư viện khác nhau, uy tín 8.3→10**.
> ⚠ **Tra hụt thì GIỮ giá trị cũ, KHÔNG ghi đè `null`** (`?? source.Reputation`): `null` ở đây nghĩa là *"không biết"* (Context7 lỗi/rate-limit, hoặc id không nằm trong tập kết quả search) chứ **không phải "uy tín bị rút"** — ghi đè sẽ **XOÁ dữ liệu tốt** mỗi lần Context7 rate-limit, mà lần reindex sau không còn gì để khôi phục.
> ⚠ **Vì sao `ReindexAsync` phải tra lại:** trước đây `reputation` được gán **đúng một lần** trong `Context7IngestAsync` ⇒ mọi nguồn nạp **TRƯỚC** khi có đường ghi đó **vĩnh viễn `null`**, reindex bao nhiêu lần cũng không cứu. Đo trên prod: reindex cả 25 nguồn xong thì **5/5** nguồn Context7 vẫn `reputation = null`.
> Fail-open có chủ đích (cùng hướng với `cv_screening` không raise khi thiếu `fullName`): nạp corpus là việc admin làm thủ công và **tốn tiền embedding** — biến một nhãn phụ thành đường làm hỏng cả lần nạp là đánh đổi tồi.

**Config/deploy:** `Qdrant:Url` · `Context7:ApiKey` · `Grounding:{Enabled(default false),TopK=4,ScoreThreshold=0.5}`. Qdrant = service trong `deploy/compose.yaml`. Bật grounding: nạp corpus qua admin → `Grounding__Enabled=true`. Unit test **mock `IVectorStore` + embed client** (không cần Qdrant/AI thật).
