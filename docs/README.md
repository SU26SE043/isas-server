# ISAS — Tài liệu hệ thống

**Sản phẩm: phỏng vấn bằng AI — 2 dòng dùng chung 1 engine.** **(B2C)** Luyện phỏng vấn cá nhân: CV/JD → AI sinh câu hỏi → ghi âm → chấm rubric → xem lịch sử. **(B2B)** Tuyển dụng: nhà tuyển dụng tạo chiến dịch từ JD → phát link cho ứng viên → AI chấm & xếp hạng → xuất kết quả. Phân biệt bằng `campaign_id`; **cả hai đều là deliverable**.

> **Trạng thái — cập nhật 2026-08-01** · trạng thái SỐNG chi tiết: [progress.md](progress.md)
>
> - **Engine 2 dòng (B2C + B2B): XONG · merged `main` · DEPLOY LIVE.** Server = gateway tunnel + 5 .NET + Mac AIService. **E2E B2C+B2B verify live** (UI Angular Vercel + API qua gateway) + **luồng tiền PayOS verify live** (order → webhook HMAC → credit, cả 2 dòng).
>   - *B2C:* luyện + lịch sử + ví credit + tổng kết điểm/nhận xét + phân tích CV + roadmap ôn tập cá nhân hoá + rubric cá nhân.
>   - *B2B:* Auth org nhiều thành viên · campaign/tiêu chí · lọc CV hàng loạt · distribution join→start · ranking/result/export · chất lượng chấm (neo-mức/self-consistency/nhận-xét). Auth gate `[Authorize(Roles)]` mọi service.
> - **Hardening + bù Functional Requirement (S6→S10): phần lớn done + merged.** DB integrity/scale (CHECK · outbox · index · xmin · FK), AIService reliability & security, money-loss/security (DB20–31), FR gaps (F1–F23), Postpaid org (BK24), PONR credit.
> - **🌱 Feature mới nhất — Grounding (RAG nguồn uy tín, [D27](decisions.md)):** AI dựa **corpus tài liệu uy tín do admin curate** + citation bấm ra nguồn; đã build (3 worker) + migration apply prod, **CHƯA merge** (nhánh `s/grounding-integration` BE · `feat/grounding-fe` FE; default **TẮT**).
> - **Còn lại:** Phase 0 (compose máy sạch · `make`), **SEC anti-cheat detection** thật (cross-repo), một số hardening/ops (xem [tasks.md](tasks.md) §CHƯA XONG).
>
> Phạm vi & phân công: [work-division.md](work-division.md).

## Mục lục

**Global (luôn dùng):**

| Tài liệu | Nội dung |
|---|---|
| [AGENTS.md](../AGENTS.md) | **Cửa vào** — dự án là gì, chạy/test, ràng buộc cứng, quy trình vào-ca/tan-ca |
| [work-division.md](work-division.md) | **Source of truth** — phạm vi 2 dòng SP (B2C 4 + B2B 5 module), hiện trạng, phân việc 4 người / 5 stream, thứ tự build |
| [decisions.md](decisions.md) | **Decision log** — *vì sao* các quyết định (D1–D27) + phương án bị loại |
| [progress.md](progress.md) | **Handoff** — trạng thái hiện tại + bước kế tiếp (cập nhật mỗi phiên) |
| [tasks.md](tasks.md) | **Task surface (WIP=1)** — task nguyên tử + lệnh xác minh + phụ thuộc + trạng thái (tạm; về sau đẩy sang board) |
| [architecture.md](architecture.md) | Kiến trúc — thành phần, giao tiếp, các luồng + **§5 quy ước chung, §6 routing/mã lỗi** |
| [rules.md](rules.md) | **Business rules toàn hệ thống** — danh mục quy tắc mã hóa (GEN/AUTH/INT/CAMP/PAY/AI/BC/SEC/DATA) |
| [../DEPLOYMENT.md](../DEPLOYMENT.md) | Triển khai 2 host (server + Mac cho AIService) qua Tailscale |

**Theo service** (API + DB + business rules gộp trong 1 file/service — `docs/services/`):

| Service | Doc | Trạng thái |
|---|---|---|
| AuthService | [services/auth.md](services/auth.md) | ✅ merged main |
| InterviewService (engine) | [services/interview.md](services/interview.md) | ✅ merged main · + §Kho tri thức (grounding, D27) |
| AIService | [services/ai.md](services/ai.md) | ✅ merged main · + §Grounding (`/embed`, D27) |
| CampaignService | [services/campaign.md](services/campaign.md) | ✅ merged main (M2–M5) |
| PaymentService | [services/payment.md](services/payment.md) | ✅ merged main + deploy live |

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
| InterviewService | .NET / EF Core | **Engine phỏng vấn (B2C & B2B)**: session (`campaign_id?`), câu hỏi, câu trả lời, điểm, rubric, file · **+ kho tri thức grounding** (Qdrant, D27) | ✅ merged main |
| AIService | Python / FastAPI + worker | Sinh câu hỏi + chấm điểm (Whisper, Gemini) — rubric/tiêu chí campaign · **+ `/embed` + grounding** (D27) | ✅ merged main |
| CampaignService | .NET / EF Core | Điều phối B2B: campaign, tiêu chí, distribution, ranking, result | ✅ merged main (M2–M5) |
| PaymentService | .NET / EF Core | Thanh toán PayOS, **credit theo chủ ví** (org/B2C — D15), **prepaid + postpaid (trả sau)**, reserve/consume | ✅ merged main + deploy live |

Hạ tầng: PostgreSQL · Redis · SeaweedFS (S3) · RabbitMQ · **Qdrant** (vector store — grounding, D27).
