# ISAS — Tài liệu hệ thống

**Sản phẩm mục tiêu: B2B tuyển dụng – phỏng vấn bằng AI.** Nhà tuyển dụng tạo chiến dịch đánh giá từ JD, phát link cho ứng viên, AI chấm & xếp hạng, xuất kết quả.

> **Trạng thái:** phần đang chạy là **engine phỏng vấn B2C** (luyện tập cá nhân: CV/JD → AI sinh câu hỏi → ghi âm → chấm rubric). B2B tái dùng engine này và thêm lớp điều phối (campaign, distribution, ranking, result) + thanh toán (credit/PayOS). Phạm vi đầy đủ & phân công xem [work-division.md](work-division.md).

## Mục lục

**Global (luôn dùng):**

| Tài liệu | Nội dung |
|---|---|
| [AGENTS.md](AGENTS.md) | **Cửa vào** — dự án là gì, chạy/test, ràng buộc cứng, quy trình vào-ca/tan-ca |
| [work-division.md](work-division.md) | **Source of truth** — phạm vi B2B (5 module), hiện trạng, phân việc 4 người, thứ tự build |
| [decisions.md](decisions.md) | **Decision log** — *vì sao* các quyết định (D1–D15) + phương án bị loại |
| [progress.md](progress.md) | **Handoff** — trạng thái hiện tại + bước kế tiếp (cập nhật mỗi phiên) |
| [tasks.md](tasks.md) | **Task surface (WIP=1)** — task nguyên tử + lệnh xác minh + phụ thuộc + trạng thái (tạm; về sau đẩy sang board) |
| [architecture.md](architecture.md) | Kiến trúc — thành phần, giao tiếp, các luồng + **§5 quy ước chung, §6 routing/mã lỗi** |
| [../DEPLOYMENT.md](../DEPLOYMENT.md) | Triển khai 2 host (server + Mac cho AIService) qua Tailscale |

**Theo service** (API + DB + business rules gộp trong 1 file/service — `docs/services/`):

| Service | Doc | Trạng thái |
|---|---|---|
| AuthService | [services/auth.md](services/auth.md) | ✅ |
| InterviewService (engine) | [services/interview.md](services/interview.md) | ✅ |
| AIService | [services/ai.md](services/ai.md) | ✅ |
| CampaignService | [services/campaign.md](services/campaign.md) | 🟡 branch |
| PaymentService | [services/payment.md](services/payment.md) | 🟡 branch |

> `api.md` / `database.md` / `rules.md` cũ giờ chỉ là **trang chuyển hướng** sang doc service (đã tách, không gom chung nữa).

## Bắt đầu từ đâu

- **Mới vào dự án** → [AGENTS.md](AGENTS.md) (cửa vào) → [architecture.md](architecture.md) (bức tranh tổng + quy ước).
- **Làm 1 service cụ thể** → mở thẳng `docs/services/<service>.md` (có đủ API + DB + rules).
- **Phạm vi / ai làm gì** → [work-division.md](work-division.md).
- **Deploy** → [../DEPLOYMENT.md](../DEPLOYMENT.md).

## Thành phần (mục tiêu — 6 service)

Mô hình **Engine + Orchestrator**: InterviewService là engine phỏng vấn dùng chung cho cả B2B & B2C; CampaignService điều phối B2B. Chi tiết & lý do: [work-division.md](work-division.md) §"Quyết định kiến trúc".

| Service | Công nghệ | Vai trò | Trạng thái |
|---|---|---|---|
| Gateway | .NET / YARP | Reverse proxy `/api/v1/*`, gộp OpenAPI | ✅ |
| AuthService | .NET / JWT, OAuth | Đăng nhập, token, profile; 3 role + **Organization (org-role OrgAdmin/HrMember)** | ✅ (cần thêm Org) |
| InterviewService | .NET / EF Core | **Engine phỏng vấn (B2B & B2C)**: session (`campaign_id?`), câu hỏi, câu trả lời, điểm, rubric, file | ✅ (mở rộng B2B) |
| AIService | Python / FastAPI + worker | Sinh câu hỏi + chấm điểm (Whisper, Gemini) — rubric hoặc tiêu chí campaign | ✅ (mở rộng) |
| CampaignService | .NET / EF Core | Điều phối B2B: campaign, tiêu chí, distribution, ranking, result | 🟡 branch |
| PaymentService | .NET / EF Core | Thanh toán PayOS, **credit theo chủ ví** (org/B2C — D15), **prepaid + postpaid (trả sau)**, reserve/consume | 🟡 branch |

Hạ tầng: PostgreSQL · Redis · SeaweedFS (S3) · RabbitMQ.
