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

## API

### Practice — `/api/v1/interview/practice` (JWT; `candidateId` từ claim)
| Method | Path | Mô tả |
|---|---|---|
| POST | `/sessions` | Tạo session + sinh câu hỏi (gọi AI). Body `{ cvId?, jdId?, jobCategory }` → 201 |
| GET | `/sessions/history` | Lịch sử session của user |
| GET | `/sessions/{sessionId}` | Chi tiết session (câu hỏi + bài nộp + điểm) |
| POST | `/sessions/{sessionId}/submit` | Chốt session (đi chấm nốt) → 204 |
| POST | `/sessions/{sessionId}/answers` | Upload audio trả lời. `multipart`: `questionId`, `file`, `durationSec`. ≤ 50MB |

Lỗi: 400 (AI rỗng / CV-JD không đọc được) · 401 · 403 (không phải buổi của bạn) · 404.

### Files — `/api/v1/interview/files` (JWT)
| Method | Path | Mô tả |
|---|---|---|
| POST | `/upload?fileType=cv\|jd` | Upload PDF (≤10MB), parse text → fileId |
| GET | `/{id}` · `/{id}/download` · `/{id}/parsed-text` | Metadata / tải / text đã trích |
| GET | `/files` | File của user |
| PUT/DELETE | `/{id}` | Thay / xóa |

Chỉ nhận `.pdf`, `fileType ∈ {cv, jd}`.

### Callback nội bộ (worker → InterviewService) — **không qua gateway**
Xác thực header **`X-Internal-Token`** (khớp `Internal:Token`).
| Method | Path | Mô tả |
|---|---|---|
| POST | `/internal/answers/{answerId}/result` | Lưu transcript + điểm → answer `Scored` |
| POST | `/internal/answers/{answerId}/failed` | Đánh dấu `Failed` (lỗi chấm vĩnh viễn) |

`result` body: `{ transcript, rubricVersion, scores:[{criterionId, score, reasoning}] }`. `failed` body: `{ reason }`.

---

## DB — `isas_interview`
```
practice_sessions 1──* practice_questions 1──1 practice_answers 1──* answer_scores
        │                                                              │
        └──*? file_records (cv_id, jd_id)            rubric_criteria ──┘
rubric_criteria 1──* rubric_levels 1──* rubric_anchors
```

### `practice_sessions`
| Cột | Kiểu | Ghi chú |
|---|---|---|
| id | uuid | PK |
| candidate_id | uuid | bắt buộc, **indexed**; ref lỏng → Auth |
| **campaign_id** | uuid? | **B2B**: null = B2C; có = bài thi campaign (ref lỏng → Campaign) |
| cv_id / jd_id | uuid? | FK → file_records (Restrict), optional |
| job_category | varchar(8) | enum: `BA`/`BE`/`FE` |
| status | varchar(32) | enum (state machine bên dưới) |
| created_at / completed_at | timestamptz | completed_at set khi submit |

### `practice_questions`
`id` · `session_id` (FK Cascade) · `order_no` · `content` · `time_limit_sec` (mặc định 120) · `created_at`. **unique (session_id, order_no)**.

### `practice_answers`
`id` (= fileId audio) · `session_id` (Cascade) · `question_id` (1–1, Restrict) · `audio_object_key` · `transcript?` · `status` (AnswerStatus) · `duration_sec` · `created_at` · `last_scoring_published_at?`. **unique (session_id, question_id)** — tối đa 1 answer/câu.

### `answer_scores`
`id` · `answer_id` (Cascade) · `criterion_id` (Restrict) · `attempt_no` (mặc định 1) · `score` numeric(5,2) · `reasoning?` · `rubric_version` · `created_at`. **unique (answer_id, criterion_id, attempt_no)**.

### `rubric_criteria` / `rubric_levels` / `rubric_anchors`
- **criteria**: `name` · `description?` · `weight` numeric(5,4) · `max_score` · `is_active` · `job_category` · **`campaign_id uuid?`** (B2B: tiêu chí theo campaign thay cho job_category; null = rubric B2C) · `version`. **index (job_category, version, is_active)**.
- **levels**: `criterion_id` (Cascade) · `score` (0..max) · `descriptor`. **unique (criterion_id, score)**.
- **anchors**: `level_id` (Cascade) · `example_answer`.

### `file_records`
`id` · `user_id` · `file_type` (`cv`/`jd`/`answer-audio`) · `original_name` · `storage_path` (key SeaweedFS) · `storage_bucket` (`isas-files`) · `mime_type` · `file_size` · `parsed_text?` · `parse_status` (`pending`/`done`/`failed`) · timestamps.

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
- **Nguồn tiêu chí tùy mode:** B2C dùng **rubric theo `JobCategory`** (`version` + `is_active`; 1 nghề chung 1 version); **B2B dùng tiêu chí campaign CÓ CẤU TRÚC** — Campaign gửi kèm khi tạo session, Interview materialize thành `rubric_criteria(campaign_id)`. **Pipeline chấm + `answer_scores` giữ NGUYÊN**, chỉ đổi *nguồn tiêu chí* (không chấm trên `criteria_text` thô).
- Worker chấm đủ **mọi** tiêu chí; thiếu → lỗi vĩnh viễn. Điểm **kẹp** `[0, maxScore]`. Bỏ tiêu chí Gemini bịa; chống trùng. `answer_scores` gắn `rubric_version` lúc chấm. Hiển thị: mỗi tiêu chí lấy **attempt mới nhất**.
- **Điểm tổng/session** (khi `Scored`) = `Σ điểm_tiêu_chí × weight` chuẩn hóa — dùng cho ranking.

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
