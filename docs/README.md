# ISAS — Tài liệu hệ thống

**Sản phẩm: phỏng vấn bằng AI — 2 dòng dùng chung 1 engine.** **(B2C)** Luyện phỏng vấn cá nhân: CV/JD → AI sinh câu hỏi → ghi âm → chấm rubric → xem lịch sử. **(B2B)** Tuyển dụng: nhà tuyển dụng tạo chiến dịch từ JD → phát link cho ứng viên → AI chấm & xếp hạng → xuất kết quả. Phân biệt bằng `campaign_id`; **cả hai đều là deliverable**.

> **Trạng thái:** **B2C** — engine luyện tập + lịch sử **đã chạy**, còn thiếu ví credit cá nhân (D15) + tổng kết điểm/nhận xét (BC9–BC11). **B2B** — đã có Auth org (A1–A3), campaign authoring + tiêu chí cấu trúc (C*, merged), engine nhận `campaign_id` + chấm theo tiêu chí (I1/E1); **còn**: distribution (magic-link), ranking/result/export, **lọc CV hàng loạt (C13–C15)**, thanh toán. Thanh toán (credit/PayOS) dùng chung. Phạm vi đầy đủ & phân công xem [work-division.md](work-division.md).

## Mục lục

**Global (luôn dùng):**

| Tài liệu | Nội dung |
|---|---|
| [AGENTS.md](../AGENTS.md) | **Cửa vào** — dự án là gì, chạy/test, ràng buộc cứng, quy trình vào-ca/tan-ca |
| [work-division.md](work-division.md) | **Source of truth** — phạm vi 2 dòng SP (B2C 4 + B2B 5 module), hiện trạng, phân việc 4 người / 5 stream, thứ tự build |
| [decisions.md](decisions.md) | **Decision log** — *vì sao* các quyết định (D1–D21) + phương án bị loại |
| [progress.md](progress.md) | **Handoff** — trạng thái hiện tại + bước kế tiếp (cập nhật mỗi phiên) |
| [tasks.md](tasks.md) | **Task surface (WIP=1)** — task nguyên tử + lệnh xác minh + phụ thuộc + trạng thái (tạm; về sau đẩy sang board) |
| [architecture.md](architecture.md) | Kiến trúc — thành phần, giao tiếp, các luồng + **§5 quy ước chung, §6 routing/mã lỗi** |
| [rules.md](rules.md) | **Business rules toàn hệ thống** — danh mục quy tắc mã hóa (GEN/AUTH/INT/CAMP/PAY/AI/BC/SEC/DATA) |
| [../DEPLOYMENT.md](../DEPLOYMENT.md) | Triển khai 2 host (server + Mac cho AIService) qua Tailscale |

**Theo service** (API + DB + business rules gộp trong 1 file/service — `docs/services/`):

| Service | Doc | Trạng thái |
|---|---|---|
| AuthService | [services/auth.md](services/auth.md) | ✅ |
| InterviewService (engine) | [services/interview.md](services/interview.md) | ✅ |
| AIService | [services/ai.md](services/ai.md) | ✅ |
| CampaignService | [services/campaign.md](services/campaign.md) | 🟢 merged main (M2) |
| PaymentService | [services/payment.md](services/payment.md) | 🟡 branch |

> `api.md` / `database.md` cũ giờ chỉ là **trang chuyển hướng** sang doc service (đã tách, không gom chung nữa). `rules.md` nay là **danh mục business rules toàn hệ thống** (tham chiếu chéo doc service + decisions.md).

## Bắt đầu từ đâu

- **Mới vào dự án** → [AGENTS.md](../AGENTS.md) (cửa vào) → [architecture.md](architecture.md) (bức tranh tổng + quy ước).
- **Làm 1 service cụ thể** → mở thẳng `docs/services/<service>.md` (có đủ API + DB + rules).
- **Dùng AI gen code từ doc** → [AGENTS.md](../AGENTS.md) §"Gen code từ doc" (playbook + prompt mẫu).
- **Phạm vi / ai làm gì** → [work-division.md](work-division.md).
- **Deploy** → [../DEPLOYMENT.md](../DEPLOYMENT.md).

## Thành phần (mục tiêu — 6 service)

Mô hình **Engine + Orchestrator**: InterviewService là engine phỏng vấn dùng chung (B2C nền · B2B xây thêm lớp điều phối). Chi tiết & lý do: [work-division.md](work-division.md) §"Quyết định kiến trúc".

| Service | Công nghệ | Vai trò | Trạng thái |
|---|---|---|---|
| Gateway | .NET / YARP | Reverse proxy `/api/v1/*`, gộp OpenAPI | ✅ |
| AuthService | .NET / JWT, OAuth | Đăng nhập, token, profile; 3 role + **Organization (org-role OrgAdmin/HrMember)** | ✅ (+ Org A1–A3) |
| InterviewService | .NET / EF Core | **Engine phỏng vấn (B2C & B2B)**: session (`campaign_id?`), câu hỏi, câu trả lời, điểm, rubric, file | ✅ (mở rộng B2B) |
| AIService | Python / FastAPI + worker | Sinh câu hỏi + chấm điểm (Whisper, Gemini) — rubric hoặc tiêu chí campaign | ✅ (mở rộng) |
| CampaignService | .NET / EF Core | Điều phối B2B: campaign, tiêu chí, distribution, ranking, result | 🟢 merged main (M2); distribution/ranking 🟡 |
| PaymentService | .NET / EF Core | Thanh toán PayOS, **credit theo chủ ví** (org/B2C — D15), **prepaid + postpaid (trả sau)**, reserve/consume | 🟡 branch |

Hạ tầng: PostgreSQL · Redis · SeaweedFS (S3) · RabbitMQ.
