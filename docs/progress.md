# ISAS — Progress / Handoff

> Trạng thái hiện tại + bước kế tiếp, để phiên/người mới nối tiếp nhanh. Kế hoạch đầy đủ & phân việc: [work-division.md](work-division.md). Lý do quyết định: [decisions.md](decisions.md).
> **Cập nhật mỗi khi đổi trạng thái** (tan ca). Cập nhật lần cuối: **2026-07-11** (vòng giám sát: **D1·BC6·E2·P1** passing, integrated vào `docs/sync-design-d18-d21`).

## Pha hiện tại
**Đang code feature B2B (không còn ở pha thiết kế).** Thiết kế đã chốt (D1–D21; **mới 2026-07-02: D20 — roadmap ôn tập cá nhân hoá B2C, `BC12`–`BC15`** — mới ở doc, **chưa build**; 2026-06-30: D18/D19 — lọc CV hàng loạt B2B, `C13`–`C15` — mới ở doc, **chưa build**). Đã merge vào `main`: **S1 Auth org** (A1–A3, PR #23), **S2 Campaign** (C1–C10, PR #22 + đưa vào pipeline deploy), **S3 I1** (session B2B `campaign_id` + materialize tiêu chí, PR #24). **E1 đã merged `main`** (commit `796d8bb` — chấm B2B theo tiêu chí campaign); **tiếp theo E2** (phát `SessionScored`). **Time-limit (D21, 2026-07-11):** bỏ giới hạn tổng buổi — chỉ giới hạn từng câu, hết giờ câu → chốt câu → sang câu kế (task **I2**). PaymentService vẫn ở branch chưa refactor. **Doc (2026-06-30):** 5 service doc đã **chi tiết hoá** (req/res mẫu · validation · bảng mã lỗi · sequence · index/edge) + đồng bộ bản copy `src/services/Isas.*/AGENTS.md` (Auth/AI/Campaign/Interview; Payment ở branch).

## Trạng thái branch / task (so với `main`)
| Mục | Nội dung | Trạng thái |
|---|---|---|
| `main` | engine B2C + Auth(+Org) + Campaign + Gateway; Interview thêm I1 | ✅ chạy |
| Auth org (A1–A3) | `organizations`/`org_members` (migration `AddOrganizations`) + JWT mang `org_id`/`org_role` + `POST /auth/register-org` | ✅ **merged main (PR #23)** · test ✅ · ⚠ e2e HTTP chờ stack chạy |
| Campaign (C1–C10) | 6 bug + soft-delete (C9) + lifecycle (C7) + snake_case + publish/`campaign_criteria` Σ=1 (C8) + `audit_logs` (C10) + AI `/suggest-criteria` (Gemini + fallback) + Dockerfile/CI/gateway route | ✅ **merged main (PR #22)** · 34 unit test ✅ · thiếu M3/M4/M5 (distribution/ranking/export), `org_id` (code còn `employer_id`) |
| I1 | session B2B nhận `campaign_id` + materialize tiêu chí → `rubric_criteria(campaign_id)` (idempotent theo campaign) | ✅ **merged main (PR #24)** · test ✅ (2/2) · ⚠ HTTP entry B2B chờ **D2** |
| E1 | chấm B2B theo tiêu chí campaign (chọn tiêu chí branch theo `campaign_id` ở `AnswerService` + `StuckAnswerRepublisher`) | ✅ **merged main (commit 796d8bb)** · `dotnet test` 31/31 ✅ · ⚠ e2e đầy đủ chờ D2 |
| `features/payment-b2c` | PaymentService (Order/Package/PayOS theo `user_id`) — **chưa có folder trong tree `main`** | 🟡 cần refactor: `credit_accounts(owner_type)`, reserve/consume/release, postpaid + hóa đơn, active-polling |

### Vòng giám sát 2026-07-11 — 4 task passing (branch tích hợp `docs/sync-design-d18-d21`)
| Task | Nội dung | Trạng thái |
|---|---|---|
| **E2** | phát `SessionScored{SessionId,CampaignId?,CandidateId,TotalScore,ScoredAt}` từ `SessionScoringNotifier` (2 chỗ đóng `Scored`), best-effort | ✅ **passing (e2eaad4)** · Interview test 33/33 · ⚠ 2 điểm thiết kế chờ team chốt (tên exchange `interview.events`/`session.scored`; công thức `TotalScore` áp chung B2B/B2C) |
| **D1** | `campaign_invitations` + magic-link + `POST /campaign/{id}/invitations` (dedup/cap/`failed[]`) + `InvitationEmailPublisher` (RabbitMQ) | ✅ **passing (82c9faa)** · Campaign test 14/14 · `campaign_candidate_id` nullable không FK (chờ C13) · chưa wire `org_id` · ⚠ e2e email chờ stack |
| **BC6** | AIService `POST /analyze-cv` sync (1 Gemini call, AI không ghi DB) + anti prompt-injection | ✅ **passing (9cfa622)** · pytest 11/11 · queue B2B (C14) chưa build |
| **P1** | PaymentService vào tree + `credit_accounts(owner_type)`/`credit_reservations`/`credit_transactions` + migration `InitPaymentDb` (không apply Neon) | ✅ **passing (dfd57ce)** · Payment test 4/4 (P0.4 xong) · bỏ `CreditService`/`PayOSService` cũ (D15 retire `UserCredit`) → reimplement P2/P5 · chưa vào CI/compose |

> Base test sau tích hợp: **Auth 4 · Campaign 14 · Interview 33 · Payment 4 = 55 pass**, `dotnet build` 0 error.

> Test project trong tree hiện có: `Isas.InterviewService.Tests`, `Isas.AuthService.Tests`, `Isas.CampaignService.Tests`. Payment **chưa** có (Phase 0 `P0.4`).

## Vấn đề đã biết / cần xác minh
- **E1 → e2e thật bị chặn bởi D2:** chưa có HTTP entry tạo session B2B (magic-link) → luồng B2B end-to-end (campaign → ứng viên làm bài → chấm → ranking) chưa chạy trọn được. Chứng minh E1 ở mức engine bằng unit/integration test.
- **Campaign chưa wire `org_id`:** code dùng `employer_id` (claim). A1 đã có `Organization`/`org_members` trên main → cần task nối campaign sang `org_id` (credit theo org cần cái này).
- **PaymentService** trên branch theo **credit cá nhân (`user_id`)** — cần refactor sang **org-credit + reserve/consume + postpaid** theo [services/payment.md](services/payment.md). Chưa vào CI/pipeline (CI build 4 service: Auth/Campaign/Gateway/Interview; AIService deploy tay trên Mac).
- **AIService** `/suggest-criteria` đã **live HTTP** nhưng deploy qua `docker cp`+restart → **ephemeral**; muốn permanent phải **rebuild image** từ Dockerfile. Các vấn đề nền còn nguyên: 🔴 `/ai/**` public không auth · 🔴 Whisper `large-v3` CPU chậm · 🔴 prompt injection bộ chấm · 🔴 `nack` không DLQ ([services/ai.md](services/ai.md) §Vấn đề).
- ✅ **Trần `orderCode` PayOS đã verify** (D12, 2026-06-28): ≤ **9.007.199.254.740.991** (2^53−1) → snowflake 64-bit vượt trần (loại). Thêm ràng buộc PayOS: `description` ≤25 ký tự, webhook **HMAC-SHA256** — bảng đầy đủ [services/payment.md](services/payment.md) §PayOS.
- **CI/CD chung Neon (DB server):** không tự apply migration lên DB chung — schema apply qua pipeline/tay trước deploy.

## Bước tiếp theo (thứ tự đề xuất)
> ✅ Vòng 2026-07-11 xong: **E2, D1, P1, BC6** (xem bảng trên). Frontier mới bên dưới.
1. **D2** (S3, Campaign+Interview — **cross-service**): mở token → account Candidate + create-or-get session. **Gỡ chặn HTTP entry B2B** cho I1/E1 (D1 ✅ + I1 ✅ đã sẵn). ⚠ đụng cả Campaign lẫn Interview → không chạy song song worker Campaign/Interview khác cùng lúc.
2. **E4** (S4, Campaign): consume `SessionScored` → `campaign_rankings` (idempotent theo `session_id`). E2 ✅. **Cần chốt tên exchange E2** trước (xem flag).
3. **S1 Payment tiếp**: **P7** (order_code time+random — dep của P2) → **P4** (`/internal/credits/reserve|release`) → **P2** (mua pack → webhook cộng credit). Mở khoá BC1/BC2 + E7.
4. **S5 B2C** (độc lập, E2E sớm nhất): **BC11** (seed rubric B2C — prerequisite chấm B2C) · **BC7** (Interview `cv-analysis` + `cv_analyses`, cần BC6 ✅) · rồi BC1/BC2 (cần P7/P4).
5. **S4 còn lại**: **E3** (`SessionAbandoned`) · E5 (pass/fail) · E6 (export) · **E7** (Payment consume/release — cần P4/P5/P6 + E2/E3) · E8 (guard điểm C#).
6. **Lọc CV B2B** (`C13`→`C14`→`C15`): C13 (bulk upload + hard-filter, Campaign) không bị chặn; C14 cần C13 + BC6 ✅.
7. **Auth A4/A5**: A5 (bật lại `[Authorize(Roles)]` mọi service) — **cross-cutting**, chạy vòng ít worker; A4 cần P2.
8. **Phase 0 còn lại**: `P0.1` (compose máy sạch), `P0.2` (`make setup/test/check`), `P0.5` (readiness + checkpoint). *(P0.3/P0.4 ✅.)*
9. **AIService roadmap** `BC13` (độc lập) → BC12/BC14/BC15 (D20).

> Quy trình **vào ca / tan ca**: xem [../AGENTS.md](../AGENTS.md).
