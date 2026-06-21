# ISAS — API

- **Base URL (qua Gateway):** `{gateway}/api/v1`
- **Xác thực:** Bearer JWT (`Authorization: Bearer <token>`), trừ các endpoint công khai (auth) và callback nội bộ.
- Gateway gộp OpenAPI tất cả service → 1 doc (Scalar).

## Routing qua Gateway

| Gateway path | Forward tới service |
|---|---|
| `/api/v1/auth/**` | AuthService `/auth/**` |
| `/api/v1/ai/**` | AIService `/api/v1/**` |
| `/api/v1/interview/practice/**` | InterviewService `/api/practice/**` |
| `/api/v1/interview/files/**` | InterviewService `/api/files/**` |

> Callback nội bộ `/internal/answers/**` **không** qua gateway — worker gọi thẳng InterviewService.

---

## 1. Auth — `/api/v1/auth` (AuthService)

| Method | Path | Auth | Mô tả |
|---|---|---|---|
| POST | `/register` | — | Đăng ký, trả token |
| POST | `/login` | — | Đăng nhập, trả access + refresh token |
| POST | `/refresh` | — | Làm mới access token bằng refresh token |
| POST | `/logout` | — | Thu hồi refresh token |
| GET | `/me` | ✅ | Lấy profile |
| PUT | `/me` | ✅ | Cập nhật profile |

---

## 2. Practice — `/api/v1/interview/practice` (InterviewService)

Tất cả yêu cầu JWT. `candidateId` lấy từ claim trong token.

| Method | Path | Mô tả |
|---|---|---|
| POST | `/sessions` | Tạo session + sinh câu hỏi (gọi AI). Body: `{ cvId?, jdId?, jobCategory }`. → 201 |
| GET | `/sessions/history` | Lịch sử session của user |
| GET | `/sessions/{sessionId}` | Chi tiết 1 session (câu hỏi + bài nộp + điểm) |
| POST | `/sessions/{sessionId}/submit` | Chốt session (đi chấm nốt). → 204 |
| POST | `/sessions/{sessionId}/answers` | Upload audio trả lời. `multipart/form-data`: `questionId`, `file`, `durationSec`. Giới hạn 50MB |

**Tạo session — request:**
```json
{ "cvId": null, "jdId": null, "jobCategory": "BE" }
```
**Lỗi thường gặp:** 400 (AI trả rỗng / CV-JD không đọc được), 401 (token sai), 403 (không phải buổi của bạn), 404 (không thấy session).

---

## 3. Files — `/api/v1/interview/files` (InterviewService)

| Method | Path | Mô tả |
|---|---|---|
| POST | `/upload?fileType=cv\|jd` | Upload PDF (≤10MB), parse text. Trả về fileId |
| GET | `/{id}` | Metadata file |
| GET | `/{id}/download` | Tải file |
| GET | `/{id}/parsed-text` | Text đã trích từ PDF |
| GET | `/files` | Danh sách file của user |
| PUT | `/{id}` | Thay file |
| DELETE | `/{id}` | Xoá file |

Chỉ nhận `.pdf`, `fileType` ∈ `{cv, jd}`.

---

## 4. AI — `/api/v1/ai` (AIService FastAPI)

| Method | Path (qua gateway) | Path thật | Mô tả |
|---|---|---|---|
| GET | `/api/v1/ai/health` | `/api/v1/health` | Health check |
| POST | `/api/v1/ai/generate-questions` | `/api/v1/generate-questions` | Sinh câu hỏi |
| POST | `/api/v1/ai/transcribe` | `/api/v1/transcribe` | Transcribe audio (multipart `file`, `language`) |

**generate-questions — request / response:**
```json
// request
{ "jobCategory": "BE", "cvText": null, "jdText": null }
// response
{ "questions": ["câu 1", "câu 2", "..."] }
```

> Thực tế InterviewService gọi `generate-questions` **trực tiếp** qua `AiService:BaseUrl`, không qua gateway.

---

## 5. Internal callbacks (worker → InterviewService)

Không qua gateway. Xác thực bằng header `X-Internal-Token` (khớp `Internal:Token`).

| Method | Path | Mô tả |
|---|---|---|
| POST | `/internal/answers/{answerId}/result` | Lưu transcript + điểm → answer `Scored` |
| POST | `/internal/answers/{answerId}/failed` | Đánh dấu answer `Failed` (lỗi chấm vĩnh viễn) |

**result — request:**
```json
{
  "transcript": "….",
  "rubricVersion": 1,
  "scores": [
    { "criterionId": "<guid>", "score": 4.5, "reasoning": "…" }
  ]
}
```
**failed — request:** `{ "reason": "audio hỏng" }`

---

## Mã lỗi chung
`200/201/204` thành công · `400` sai input · `401` thiếu/sai token · `403` không có quyền · `404` không tìm thấy · `409` xung đột trạng thái · `500` lỗi hệ thống · `502` lỗi gọi AIService.
