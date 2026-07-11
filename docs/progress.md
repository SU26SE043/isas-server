# ISAS — Progress / Handoff

> Trạng thái hiện tại + bước kế tiếp, để phiên/người mới nối tiếp nhanh. Kế hoạch đầy đủ & phân việc: [work-division.md](work-division.md). Lý do quyết định: [decisions.md](decisions.md).
> **Cập nhật mỗi khi đổi trạng thái** (tan ca). Cập nhật lần cuối: **2026-07-11** (vòng giám sát: **D1·BC6·E2·P1** (vòng 1) + **P7·E3·E4·BC13** (vòng 2) passing, integrated vào `docs/sync-design-d18-d21`).

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

### Vòng giám sát 2026-07-11 (vòng 2) — 4 task passing
| Task | Nội dung | Trạng thái |
|---|---|---|
| **P7** | `OrderCodeGenerator` time+random (`yyMMddHHmmss`+4 random), guard ≤2^53−1, retry ≤10 khi đụng UNIQUE (index sẵn từ P1) | ✅ **passing (7ffe1c9)** · Payment 7/7 · không migration mới · chưa wire `OrderService` (P2) |
| **E3** | `SessionAbandoned` + `SessionAbandonSweeper` (2 phút) phát key `session.abandoned` trên `interview.events` | ✅ **passing (50cab51)** · Interview 38/38 · ⚠ deadline placeholder `CreatedAt+30min` (`campaigns.expires_at` không đọc xuyên service) → I2 materialize deadline lên session; sweeper là plumbing forward |
| **E4** | `SessionScoredConsumer` (queue `campaign.ranking`) → upsert `campaign_rankings` idempotent theo `session_id`; B2C no-op | ✅ **passing (19fb040)** · Campaign 17/17 · migration `AddCampaignRankings` (không apply Neon) · re-anchor v2 sau khi base có D1 |
| **BC13** | AIService 3 endpoint roadmap sync (`/generate-roadmap`·`/generate-lesson-theory`·`/summarize-roadmap`), không ghi DB | ✅ **passing (b8d018f)** · pytest 29/29 (11 BC6+18) · layer trên BC6 · BC12/14/15 (DB) chưa build |

> Base test sau vòng 2: **Auth 4 · Payment 7 · Campaign 17 · Interview 38 = 66 .NET pass** (+ AIService pytest 29), `dotnet build` 0 error.
> **⚠ Lưu ý điều phối (worktree):** worker isolation tạo worktree off `a4c80d5` (merge-base), KHÔNG off tip base → 2 worker (E4/BC13) phải re-anchor onto tip (`git switch -c <br> <base-sha>`; worktree chia object store nên với tới được); P7/E3 tự nhận ra & tự sửa. **Vòng sau: brief phải ghi rõ base SHA để worker tạo nhánh off đúng tip.**

> Test project trong tree hiện có: `Isas.InterviewService.Tests`, `Isas.AuthService.Tests`, `Isas.CampaignService.Tests`. Payment **chưa** có (Phase 0 `P0.4`).

## Vấn đề đã biết / cần xác minh
- **E1 → e2e thật bị chặn bởi D2:** chưa có HTTP entry tạo session B2B (magic-link) → luồng B2B end-to-end (campaign → ứng viên làm bài → chấm → ranking) chưa chạy trọn được. Chứng minh E1 ở mức engine bằng unit/integration test.
- **Campaign chưa wire `org_id`:** code dùng `employer_id` (claim). A1 đã có `Organization`/`org_members` trên main → cần task nối campaign sang `org_id` (credit theo org cần cái này).
- **PaymentService** trên branch theo **credit cá nhân (`user_id`)** — cần refactor sang **org-credit + reserve/consume + postpaid** theo [services/payment.md](services/payment.md). Chưa vào CI/pipeline (CI build 4 service: Auth/Campaign/Gateway/Interview; AIService deploy tay trên Mac).
- **AIService** `/suggest-criteria` đã **live HTTP** nhưng deploy qua `docker cp`+restart → **ephemeral**; muốn permanent phải **rebuild image** từ Dockerfile. Các vấn đề nền còn nguyên: 🔴 `/ai/**` public không auth · 🔴 Whisper `large-v3` CPU chậm · 🔴 prompt injection bộ chấm · 🔴 `nack` không DLQ ([services/ai.md](services/ai.md) §Vấn đề).
- ✅ **Trần `orderCode` PayOS đã verify** (D12, 2026-06-28): ≤ **9.007.199.254.740.991** (2^53−1) → snowflake 64-bit vượt trần (loại). Thêm ràng buộc PayOS: `description` ≤25 ký tự, webhook **HMAC-SHA256** — bảng đầy đủ [services/payment.md](services/payment.md) §PayOS.
- **CI/CD chung Neon (DB server):** không tự apply migration lên DB chung — schema apply qua pipeline/tay trước deploy.

## Bước tiếp theo (thứ tự đề xuất)
> ✅ Vòng 1: **E2, D1, P1, BC6**. ✅ Vòng 2: **P7, E3, E4, BC13** (xem bảng trên). Frontier mới bên dưới.
1. **D2** (S3, Campaign+Interview — **cross-service**): mở token → account Candidate + create-or-get session. **Gỡ chặn HTTP entry B2B** cho I1/E1 (D1 ✅ + I1 ✅). ⚠ đụng Campaign+Interview (+ Auth account nhẹ) → **chạy solo**, không song song worker Campaign/Interview khác.
2. **P4** (Payment, `/internal/credits/reserve|release`) → **P2** (mua pack → webhook cộng credit, dùng P7 ✅). Mở khoá BC1/BC2 + E7. Rồi **P5/P6** (consume/release).
3. **S5 B2C**: **BC11** (seed rubric B2C — prerequisite chấm B2C) · **BC7** (Interview `cv-analysis` + `cv_analyses`, cần BC6 ✅) · **BC9** (tổng kết điểm) · rồi BC1/BC2 (cần P2/P4).
4. **E5** (Campaign, xếp hạng + pass/fail — cần E4 ✅) → **E6** (export CSV/PDF) → **E7** (Payment consume/release — cần P4/P5/P6 + E2 ✅/E3 ✅).
5. **Lọc CV B2B** (`C13`→`C14`→`C15`): C13 (bulk upload + hard-filter, Campaign) không bị chặn; C14 cần C13 + BC6 ✅.
6. **I2** (per-question time-limit + materialize deadline lên session — gỡ design gap E3) · **E8** (guard điểm C# ở callback).
7. **AIService roadmap DB side** `BC12`→`BC14`→`BC15` (D20, cần BC13 ✅ + BC9/BC11).
8. **Auth A4/A5**: A5 (bật lại `[Authorize(Roles)]` mọi service) — **cross-cutting**, chạy vòng ít worker; A4 cần P2.
9. **Phase 0 còn lại**: `P0.1` (compose máy sạch), `P0.2` (`make setup/test/check`), `P0.5` (readiness + checkpoint). *(P0.3/P0.4 ✅.)*

> Quy trình **vào ca / tan ca**: xem [../AGENTS.md](../AGENTS.md).
