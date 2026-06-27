# AGENTS.md — ISAS server

Cửa vào cho agent/người mới. Đọc file này trước, rồi xem các doc cùng thư mục.
*(Tạm để trong `docs/`; theo convention agent thường tìm `AGENTS.md` ở gốc repo — cân nhắc chuyển ra gốc khi chốt bố cục.)*

## Dự án là gì
**ISAS** — nền tảng **B2B tuyển dụng – phỏng vấn bằng AI** (capstone SEP490, team SU26SE043). Nhà tuyển dụng tạo *chiến dịch đánh giá* từ JD → phát link cho ứng viên → AI chấm theo tiêu chí → xếp hạng. Lõi chấm điểm vốn xây cho **B2C luyện tập**, **dùng lại** cho B2B (phân biệt bằng `campaign_id`).

Kiến trúc **Engine + Orchestrator, 6 service**: `Gateway · AuthService · AIService · CampaignService · InterviewService · PaymentService`. Hạ tầng: Postgres · Redis · SeaweedFS(S3) · RabbitMQ.

## Chạy & kiểm thử
- **Chạy:** `docker compose up` (xem `../compose.yaml`). AIService (Python) chạy riêng — xem [../DEPLOYMENT.md](../DEPLOYMENT.md).
- **Kiểm thử:** `dotnet test` (hiện chỉ có project `Isas.InterviewService.Tests`).
- **Env cần:** connection string mỗi DB · `Jwt:Key/Issuer/Audience` (giống nhau mọi service) · `Internal:Token` · `AiService:BaseUrl` · SeaweedFS keys · PayOS keys.

**Readiness (4 điều kiện cho phiên/người mới):** ✅ chạy được (compose) · ✅ thấy tiến độ ([progress.md](progress.md)) · ✅ chọn bước kế ([work-division.md](work-division.md) §5) · ⚠ **test được**: Campaign/Payment **chưa có** test project, chưa có lệnh chuẩn hoá (`make setup/test/check`). → làm **Phase 0** trong [work-division.md](work-division.md) §5 trước khi code feature.

## Ràng buộc cứng (PHẢI tuân — chi tiết [architecture.md](architecture.md) §5)
- API public qua Gateway `/api/v1/<service>/...`; **callback `/internal/...` + webhook PayOS KHÔNG qua gateway**.
- **DB-per-service**, EF Core, cột **snake_case**, enum lưu **string**, **không FK xuyên service** (ref = Guid lỏng).
- **Auth offline:** validate JWT bằng chung key, **không call AuthService lúc chạy**.
- **AIService không ghi DB** — trả kết quả qua callback (`X-Internal-Token`).
- File SeaweedFS: **lưu key/path, không lưu full URL**.
- **Tiền:** chỉ PaymentService ghi bảng payment; cộng credit/kích hoạt **chỉ khi webhook PayOS đã verify**.

## Quy tắc làm việc (WIP = 1)
- **Mỗi người/agent chỉ 1 task `active`** tại một thời điểm. Xong + **xác minh end-to-end pass** mới bắt task kế.
- **KHÔNG "tiện thể tái cấu trúc"** thứ khác khi đang làm task A → thấy việc đáng làm thì **ghi vào backlog/board**, làm sau.
- Mỗi task có **1 lệnh xác minh chạy được**; "Xong" theo **3 lớp** (mục dưới), **không phải** "code trông ổn".
- **Commit theo đơn vị nguyên tử** (1 task = 1 commit; message: *làm gì + vì sao*).
- **Nguồn task = [tasks.md](tasks.md)** (nguồn sự thật task-level): chọn task `not_started` không bị block; chỉ đánh **`passing` khi lệnh xác minh PASS** + ghi **bằng chứng** (commit/log) vào ô Status. Đừng tự nâng trạng thái.
- *WIP=1 là per-người/per-agent* — team vẫn chạy 4 stream S1–S4 **song song**, mỗi stream WIP=1.

## Định nghĩa "Xong" — 3 lớp (đừng tin "cảm giác xong")
Agent/người **tự tin thái quá có hệ thống** → "Xong" do **xác minh THỰC THI** quyết định, không phải "code trông ổn". Qua **lần lượt cả 3 lớp**, fail lớp dưới thì KHÔNG xét lớp trên:
1. **Build/static**: `dotnet build` (+ lint) sạch.
2. **Hành vi runtime**: test pass **+ service khởi động tới `/health` 200** + đường đi chính chạy (lệnh trong [tasks.md](tasks.md)).
3. **End-to-end hệ thống**: chạy **trọn luồng thật** (mua credit → tạo campaign → ứng viên làm bài → chấm → ranking), gồm **DB/migration nhất quán + dịch vụ ngoài cấu hình đủ** (PayOS/SeaweedFS/RabbitMQ/email). **Unit test xanh ≠ Xong.**

- **Người làm ≠ người kiểm:** task `passing` chỉ khi xác minh pass **VÀ** **người khác review PR** (tự chấm luôn lệch dương). *Không xây agent-evaluator cho capstone — dùng PR review.*
- **Thứ tự ưu tiên:** đúng chức năng → hiệu năng → phong cách. **Không refactor/tối ưu khi chức năng cốt lõi chưa qua 3 lớp.**
- **Báo lỗi kèm cách sửa:** test/CI fail nên nói *sai ở đâu + sửa thế nào* (vd "POST /payment/order 500 — thiếu `PayOS:ChecksumKey` trong env"), không chỉ "failed".

## Bản đồ tài liệu (`docs/`)
- [work-division.md](work-division.md) — **source of truth**: phạm vi, phân việc 4 người, thứ tự build.
- [decisions.md](decisions.md) — **vì sao** các quyết định (D1–D15) + phương án bị loại.
- [progress.md](progress.md) — trạng thái hiện tại + bước kế.
- [tasks.md](tasks.md) — **task nguyên tử + lệnh xác minh + trạng thái** (WIP=1; chọn task `not_started` không bị block).
- [architecture.md](architecture.md) — tổng quan + quy ước + routing.
- `services/<service>.md` — API + DB + business rules từng service.

## Vào ca (bắt đầu phiên)
1. Đọc [progress.md](progress.md) (đang ở đâu, bước kế) + [decisions.md](decisions.md) (đừng lật lại quyết định đã chốt).
2. `git status` / `git log --oneline -5` để nắm trạng thái thật.
3. Tiếp tục từ mục **"Bước tiếp theo"** trong `progress.md`.

## Tan ca — trạng thái sạch (điều kiện hoàn thành phiên)
**Clean state = đủ 5, thiếu 1 = phiên CHƯA xong** ("dọn sau = không bao giờ dọn"):
- [ ] **Build pass** (`dotnet build`) — đừng để phiên sau sửa build hỏng.
- [ ] **Test pass** (`dotnet test`, kể cả test cũ — không phá chức năng đang có).
- [ ] **Tiến độ cập nhật**: [progress.md](progress.md) + trạng thái [tasks.md](tasks.md); quyết định mới → [decisions.md](decisions.md); đổi service → `services/<service>.md`.
- [ ] **Không rác tạm**: bỏ debug log / file tạm / code comment-out / TODO rác.
- [ ] **Đường khởi động chạy** (`docker compose up`) — phiên sau code tiếp ngay, không phải dập lửa.
- [ ] **Commit nguyên tử** trạng thái sạch (message: *làm gì + vì sao*).
