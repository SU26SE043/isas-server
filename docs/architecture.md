# ISAS — Kiến trúc hệ thống

Hệ thống luyện phỏng vấn: sinh câu hỏi theo CV/JD/vị trí, ghi âm trả lời, chấm điểm tự động theo rubric bằng AI.

## 1. Tổng quan

Kiến trúc **microservices**, một **API Gateway** (YARP) đứng trước, các service .NET + 1 service AI (Python).

```
                         ┌──────────────────────────┐
        Frontend ──────► │  Gateway (YARP)          │  /api/v1/*
   (web / vercel)        │  - reverse proxy         │
                         │  - gộp OpenAPI            │
                         └─────┬───────┬──────┬──────┘
                               │       │      │
              /api/v1/auth ────┘       │      └──── /api/v1/ai
                    │                  │                 │
            ┌───────▼──────┐   /api/v1/interview   ┌─────▼─────────┐
            │ AuthService  │           │           │  AIService     │
            │ (.NET)       │   ┌───────▼───────┐   │  (Python/FastAPI│
            │  JWT, OAuth  │   │ InterviewSvc  │   │   + worker)    │
            └──────┬───────┘   │ (.NET)        │   └──┬─────────┬──┘
                   │           └───┬───────┬───┘      │         │
                   │               │       │          │ Gemini  │
           ┌───────▼───┐   ┌───────▼──┐ ┌──▼─────┐    │ Whisper │
           │ Postgres  │   │ Seaweed  │ │RabbitMQ│◄───┘         │
           │ (auth DB) │   │ FS (S3)  │ │ queue  │──► worker ───┘
           └───────────┘   └──────────┘ └────────┘   (chấm điểm)
```

## 2. Thành phần

| Thành phần | Công nghệ | Vai trò |
|---|---|---|
| **Gateway** | .NET, YARP | Reverse proxy `/api/v1/*` → service; gộp OpenAPI các service thành 1 doc |
| **AuthService** | .NET, JWT, Google OAuth | Đăng ký/đăng nhập, phát JWT, refresh token, profile |
| **InterviewService** | .NET, EF Core | Quản lý session phỏng vấn, câu hỏi, câu trả lời, điểm, rubric, file CV/JD |
| **AIService** | Python, FastAPI, faster-whisper, google-genai | Sinh câu hỏi (HTTP) + worker chấm điểm (consume RabbitMQ) |
| **Postgres** | PostgreSQL 18 | DB cho Auth (`isas`) và Interview (`isas_interview`) |
| **SeaweedFS** | S3-compatible (cổng 8333) | Lưu file CV/JD (PDF) + audio trả lời |
| **RabbitMQ** | AMQP | Hàng đợi job chấm điểm (`scoring_pipeline_queue`) |
| **Redis** | Redis 7 | Cache / refresh token (AuthService) |

## 3. Giao tiếp giữa service

| Từ | Đến | Kiểu | Mục đích |
|---|---|---|---|
| Frontend | Gateway | HTTPS | Mọi request public, prefix `/api/v1` |
| InterviewService | AIService | HTTP (`AiService:BaseUrl`) | Sinh câu hỏi (đồng bộ) |
| InterviewService | RabbitMQ | AMQP publish | Đẩy job chấm điểm |
| AIService worker | RabbitMQ | AMQP consume | Nhận job chấm điểm |
| AIService worker | SeaweedFS | S3 | Tải audio để transcribe |
| AIService worker | InterviewService | HTTP callback (`/internal/...`) | Trả transcript + điểm (xác thực `X-Internal-Token`) |

> **Nguyên tắc:** AIService (Python) **không** ghi DB. Mọi kết quả gửi callback về InterviewService — .NET là chủ DB duy nhất.

## 4. Luồng chính

### 4.1. Sinh câu hỏi (đồng bộ)
1. FE → `POST /api/v1/interview/practice/sessions` (JobCategory bắt buộc; CvId/JdId optional).
2. InterviewService parse CV/JD (nếu có) → tạo session `GeneratingQuestions`.
3. Gọi AIService `POST /api/v1/generate-questions` (Gemini sinh câu hỏi tiếng Việt).
4. Lưu câu hỏi → session `Ready`. Trả về cho FE.

### 4.2. Chấm điểm dần (bất đồng bộ)
1. FE upload từng câu trả lời → audio lên SeaweedFS → answer `Uploaded`.
2. InterviewService publish job (kèm rubric) lên RabbitMQ → answer `Scoring`.
3. Worker consume → tải audio → Whisper transcribe → Gemini chấm theo rubric.
4. Worker callback `/internal/answers/{id}/result` → InterviewService lưu điểm → answer `Scored`.
5. Lỗi vĩnh viễn → worker callback `/internal/answers/{id}/failed` → answer `Failed`.
6. FE `POST .../submit` chốt session; khi mọi answer xong → session `Scored`.

> Cơ chế phục hồi: `StuckAnswerRepublisher` (background) đẩy lại job kẹt. Chi tiết state machine + retry xem [rules.md](rules.md).

## 5. Hạ tầng & Deploy

- CI/CD: [.github/workflows/ci.yml](../.github/workflows/ci.yml) — build & push image lên GHCR, SSH deploy qua Tailscale.
- Routing & prefix gateway: `appsettings.json` của Gateway (scheme `/api/v1/{service}` + StripPrefix).
- Triển khai 2 host (server + Mac cho AIService): xem [DEPLOYMENT.md](../DEPLOYMENT.md).
