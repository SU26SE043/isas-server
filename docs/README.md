# ISAS — Tài liệu hệ thống

Hệ thống luyện phỏng vấn: sinh câu hỏi theo CV/JD/vị trí, ghi âm trả lời, chấm điểm tự động theo rubric bằng AI.

## Mục lục

| Tài liệu | Nội dung |
|---|---|
| [architecture.md](architecture.md) | Kiến trúc hệ thống — thành phần, tech stack, giao tiếp giữa service, các luồng chính |
| [database.md](database.md) | Lược đồ CSDL — bảng, cột, khóa, quan hệ, enum (Interview + Auth DB) |
| [api.md](api.md) | API — routing gateway `/api/v1`, endpoint từng service, callback nội bộ, mã lỗi |
| [rules.md](rules.md) | Business rules — state machine session/answer, chấm dần, retry, rubric, xác thực |
| [../DEPLOYMENT.md](../DEPLOYMENT.md) | Triển khai 2 host (server + Mac cho AIService) qua Tailscale |

## Bắt đầu từ đâu

- **Mới vào dự án** → đọc [architecture.md](architecture.md) trước để nắm bức tranh tổng thể.
- **Làm việc với DB / migration** → [database.md](database.md).
- **Tích hợp frontend / gọi API** → [api.md](api.md).
- **Hiểu nghiệp vụ / trạng thái** → [rules.md](rules.md).
- **Deploy lên server** → [../DEPLOYMENT.md](../DEPLOYMENT.md).

## Thành phần (tóm tắt)

| Service | Công nghệ | Vai trò |
|---|---|---|
| Gateway | .NET / YARP | Reverse proxy `/api/v1/*`, gộp OpenAPI |
| AuthService | .NET / JWT, OAuth | Đăng nhập, phát token, profile |
| InterviewService | .NET / EF Core | Session, câu hỏi, câu trả lời, điểm, rubric, file |
| AIService | Python / FastAPI + worker | Sinh câu hỏi + chấm điểm (Whisper, Gemini) |

Hạ tầng: PostgreSQL · Redis · SeaweedFS (S3) · RabbitMQ.
