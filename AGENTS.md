# AGENTS.md — ISAS server

**Cửa vào cho agent/người mới** — đặt ở **gốc repo** (agent tự tìm `AGENTS.md` ở đây). Đọc file này trước, rồi xem doc chi tiết trong [`docs/`](docs/).

## Dự án là gì
**ISAS** — nền tảng phỏng vấn bằng AI (capstone SEP490, team SU26SE043), giao **2 dòng sản phẩm dùng chung 1 engine**: **(B2C)** luyện phỏng vấn cá nhân (tự tạo session từ CV/JD → AI sinh câu hỏi → ghi âm → chấm rubric → xem lịch sử) · **(B2B)** tuyển dụng (Employer tạo *chiến dịch đánh giá* từ JD → phát link → AI chấm theo tiêu chí → xếp hạng). Engine phỏng vấn dùng chung; phân biệt bằng `campaign_id` (null = B2C, có = B2B). **Cả hai đều là deliverable** — B2C không phải engine phụ của B2B.

Kiến trúc **Engine + Orchestrator, 6 service**: `Gateway · AuthService · AIService · CampaignService · InterviewService · PaymentService`. Hạ tầng: Postgres · Redis · SeaweedFS(S3) · RabbitMQ.

## Chạy & kiểm thử
- **Chạy:** `docker compose up` (xem `compose.yaml`). AIService (Python) chạy riêng — xem [DEPLOYMENT.md](DEPLOYMENT.md).
- **Kiểm thử:** `dotnet test` (4 test project **đã có**: Auth · Interview · Campaign · Payment) + AIService `pytest` (Python, `cd src/services/Isas.AIService` + `pip install -r requirements-dev.txt` — cần `pytest-asyncio`). Trạng thái số test hiện hành: [progress.md](docs/progress.md).
- **Env cần:** connection string mỗi DB · `Jwt:Key/Issuer/Audience` (giống nhau mọi service) · `Internal:Token` · `AiService:BaseUrl` · SeaweedFS keys · PayOS keys.

**Readiness (4 điều kiện cho phiên/người mới):** ✅ chạy được (compose) · ✅ thấy tiến độ ([progress.md](docs/progress.md)) · ✅ chọn bước kế ([progress.md](docs/progress.md) §Bước tiếp theo) · ✅ **test được**: cả 4 service .NET + AIService pytest **đã có** (`P0.3`/`P0.4` xong). **Còn Phase 0:** `P0.1` (compose máy sạch → `/health` 200), `P0.2` (`make setup/test/check`), `P0.5` (readiness checkpoint).

## Ràng buộc cứng (PHẢI tuân — chi tiết [architecture.md](docs/architecture.md) §5)
- API public qua Gateway `/api/v1/<service>/...`; **callback `/internal/...` + webhook PayOS KHÔNG qua gateway**.
- **DB-per-service**, EF Core, cột **snake_case**, enum lưu **string**, **không FK xuyên service** (ref = Guid lỏng).
- **Auth offline:** validate JWT bằng chung key, **không call AuthService lúc chạy**.
- **AIService không ghi DB** — trả kết quả qua callback (`X-Internal-Token`).
- File SeaweedFS: **lưu key/path, không lưu full URL**.
- **Tiền:** chỉ PaymentService ghi bảng payment; cộng credit/kích hoạt **chỉ khi webhook PayOS đã verify**.

## Quy tắc làm việc (WIP = 1)
- **Plan trước, chờ duyệt:** mặc định **LẬP PLAN → trình người dùng → CHỜ DUYỆT** rồi mới sửa file / chạy lệnh thay đổi. Đọc–tìm–review để dựng plan thì cứ làm; **không tự thực thi thay đổi** (edit/migration/commit) khi plan chưa duyệt. *(Đồng bộ prompt doc-sync: "LẬP PLAN, DỪNG, CHỜ DUYỆT".)*
- **Mỗi người/agent chỉ 1 task `active`** tại một thời điểm. Xong + **xác minh end-to-end pass** mới bắt task kế.
- **KHÔNG "tiện thể tái cấu trúc"** thứ khác khi đang làm task A → thấy việc đáng làm thì **ghi vào backlog/board**, làm sau.
- Mỗi task có **1 lệnh xác minh chạy được**; "Xong" theo **3 lớp** (mục dưới), **không phải** "code trông ổn".
- **Commit theo đơn vị nguyên tử** (1 task = 1 commit; message: *làm gì + vì sao*).
- **Giao hàng = 1 PR duy nhất để duyệt** — gộp toàn bộ work của một đợt (nhiều concern/nhiều service) vào **1 branch → 1 PR** cho người duyệt, giữ các commit nguyên tử bên trong. **KHÔNG** tách mỗi concern 1 branch/PR rời, **KHÔNG** merge nhiều nhánh thẳng vào `main` (bỏ qua review). FE (repo riêng) cũng gộp về 1 branch → 1 PR sang `master`. *(User nhắc 2026-07-23; xem cả DB5 2026-07-17.)*
- **Nguồn task = [tasks.md](docs/tasks.md)** (nguồn sự thật task-level): chọn task `not_started` không bị block; chỉ đánh **`passing` khi lệnh xác minh PASS** + ghi **bằng chứng** (commit/log) vào ô Status. Đừng tự nâng trạng thái.
- *WIP=1 là per-người/per-agent* — team vẫn chạy 5 stream S1–S5 **song song**, mỗi stream WIP=1.

## Định nghĩa "Xong" — 3 lớp (đừng tin "cảm giác xong")
Agent/người **tự tin thái quá có hệ thống** → "Xong" do **xác minh THỰC THI** quyết định, không phải "code trông ổn". Qua **lần lượt cả 3 lớp**, fail lớp dưới thì KHÔNG xét lớp trên:
1. **Build/static**: `dotnet build` (+ lint) sạch.
2. **Hành vi runtime**: test pass **+ service khởi động tới `/health` 200** + đường đi chính chạy (lệnh trong [tasks.md](docs/tasks.md)).
3. **End-to-end hệ thống**: chạy **trọn luồng thật** của dòng sản phẩm liên quan — **B2B**: mua credit org → tạo campaign → ứng viên làm bài → chấm → ranking; **B2C**: đăng ký cá nhân → mua credit prepaid → tự luyện từ CV → ghi âm → chấm rubric → trừ 1 credit ví cá nhân → xem lịch sử. Gồm **DB/migration nhất quán + dịch vụ ngoài cấu hình đủ** (PayOS/SeaweedFS/RabbitMQ/email). **Unit test xanh ≠ Xong.**

- **Người làm ≠ người kiểm:** task `passing` chỉ khi xác minh pass **VÀ** **người khác review PR** (tự chấm luôn lệch dương). *Không xây agent-evaluator cho capstone — dùng PR review.*
- **Thứ tự ưu tiên:** đúng chức năng → hiệu năng → phong cách. **Không refactor/tối ưu khi chức năng cốt lõi chưa qua 3 lớp.**
- **Báo lỗi kèm cách sửa:** test/CI fail nên nói *sai ở đâu + sửa thế nào* (vd "POST /payment/order 500 — thiếu `PayOS:ChecksumKey` trong env"), không chỉ "failed".

## Gen code từ doc (playbook cho agent)
Doc này + `docs/services/<svc>.md` + `tasks.md` là **đủ để 1 agent gen code cho TỪNG task**. Không gen cả hệ một lượt — đi từng task, WIP=1.

**Ngữ cảnh agent đọc trước mỗi task (đúng thứ tự, KHÔNG lệch):**
1. **File này** — ràng buộc cứng + Định nghĩa "Xong" + WIP=1.
2. [tasks.md](docs/tasks.md) — chọn 1 task `not_started` không bị block; lấy *hành vi* + *lệnh xác minh* + *dep*.
3. **Spec service** — *hợp đồng để code theo*: API (DTO + req/res + status) · DB (cột + kiểu + FK) · business rules.
4. [decisions.md](docs/decisions.md) các **D** liên quan — để **không lật lại** thiết kế đã chốt.
5. [architecture.md](docs/architecture.md) §5/§6 — quy ước chung + mã lỗi.

**Spec service nằm ở đâu** (`docs/services/` là source of truth; bản trong folder code là **copy**, chấp nhận lặp):
| Service | Spec để code theo |
|---|---|
| Auth · Interview · AI | **`src/services/Isas.<Svc>/AGENTS.md`** (copy local — agent làm trong folder tự thấy) **+** `docs/services/<svc>.md` |
| **Campaign** | ✅ **trong tree**: **`src/services/Isas.CampaignService/AGENTS.md`** (copy local) **+** `docs/services/campaign.md`. |
| **Payment** | ✅ **trong tree** (P1 đưa vào từ vòng 1, nhánh tích hợp): **`src/services/Isas.PaymentService/AGENTS.md`** (nếu chưa có bản copy → đồng bộ từ) `docs/services/payment.md`. *(Nhánh `features/payment-b2c` cũ đã obsolete — code hiện dùng owner_type/reserve/consume/postpaid.)* |

> Sửa thiết kế → sửa ở `docs/services/<svc>.md` (source of truth) rồi **copy lại** xuống folder, đừng sửa bản copy lệch.

**Vòng lặp (1 task/lần):** code + migration + ≥1 test → chạy **lệnh xác minh của task** (lớp 2/3) → PASS → **người khác review PR** → đánh `passing` (ghi commit) + cập nhật `docs/services/<svc>.md` nếu API/DB đổi → **commit nguyên tử**. Fail → sửa, báo *sai đâu + fix sao*.

**Prompt mẫu (thay `<ID>`):**
```
Task <ID> (tasks.md): <hành vi>.
Đọc trước, KHÔNG lệch: AGENTS.md · docs/services/<svc>.md (phần API+DB liên quan) · decisions.md <D…> · architecture.md §5/§6.
Phạm vi: CHỈ task này (WIP=1), không tiện tay refactor việc khác.
Làm: code + migration + 1 test ĐÚNG spec trong service doc. Doc sai/thiếu → HỎI, đừng code lệch doc.
Xong khi: chạy `<lệnh xác minh của task>` PASS (build → runtime → e2e). Lỗi → báo kèm cách sửa.
Sau pass: cập nhật status <ID> trong tasks.md (kèm commit) + service doc nếu API/DB đổi. Commit nguyên tử.
```

**Guardrail (để không "code trông ổn"):**
- **Phase 0 còn lại** (`P0.1`,`P0.2`,`P0.5`): chưa verify `docker compose up` máy sạch · chưa `make setup/test/check` → lớp-3 e2e thật (compose + broker + PayOS sandbox + Gemini) **verify tay ngoài worker**. (`P0.3`/`P0.4` xong — 4 test project + pytest đã có.)
- **Nhánh làm việc:** toàn bộ feature ở nhánh tích hợp **`docs/sync-design-d18-d21`** (6 service đều trong tree; `main` mới có nền pre-vòng-1 — xem [progress.md](docs/progress.md) §Main-vs-branch). KHÔNG checkout branch cũ.
- Agent **KHÔNG bịa ngoài doc**, **KHÔNG đổi thiết kế trong doc** (thấy sai → hỏi), **KHÔNG tự nâng `passing`** khi lệnh xác minh chưa PASS.
- **Bắt đầu:** đọc [progress.md](docs/progress.md) §Bước tiếp theo → chọn task `not_started` không bị block trong [tasks.md](docs/tasks.md).

## Bản đồ tài liệu (`docs/`)
- [work-division.md](docs/work-division.md) — **source of truth**: phạm vi, phân việc 4 người, thứ tự build.
- [decisions.md](docs/decisions.md) — **vì sao** các quyết định (D1–D23) + phương án bị loại.
- [progress.md](docs/progress.md) — trạng thái hiện tại + bước kế.
- [tasks.md](docs/tasks.md) — **task nguyên tử + lệnh xác minh + trạng thái** (WIP=1; chọn task `not_started` không bị block).
- [architecture.md](docs/architecture.md) — tổng quan + quy ước + routing.
- `docs/services/<service>.md` — API + DB + business rules từng service (đã chi tiết: **req/res mẫu · validation · bảng mã lỗi · sequence · index/edge**). Bản copy cho agent ở `src/services/Isas.<Svc>/AGENTS.md` (đồng bộ từ `docs/`).

## Vào ca (bắt đầu phiên)
1. Đọc [progress.md](docs/progress.md) (đang ở đâu, bước kế) + [decisions.md](docs/decisions.md) (đừng lật lại quyết định đã chốt).
2. `git status` / `git log --oneline -5` để nắm trạng thái thật.
3. Tiếp tục từ mục **"Bước tiếp theo"** trong `progress.md`.

## Tan ca — trạng thái sạch (điều kiện hoàn thành phiên)
**Clean state = đủ 5, thiếu 1 = phiên CHƯA xong** ("dọn sau = không bao giờ dọn"):
- [ ] **Build pass** (`dotnet build`) — đừng để phiên sau sửa build hỏng.
- [ ] **Test pass** (`dotnet test`, kể cả test cũ — không phá chức năng đang có).
- [ ] **Tiến độ cập nhật**: [progress.md](docs/progress.md) + trạng thái [tasks.md](docs/tasks.md); quyết định mới → [decisions.md](docs/decisions.md); đổi service → `docs/services/<service>.md`.
- [ ] **Không rác tạm**: bỏ debug log / file tạm / code comment-out / TODO rác.
- [ ] **Đường khởi động chạy** (`docker compose up`) — phiên sau code tiếp ngay, không phải dập lửa.
- [ ] **Commit nguyên tử** trạng thái sạch (message: *làm gì + vì sao*).
