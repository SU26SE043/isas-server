# ISAS — Progress / Handoff

> Trạng thái hiện tại + bước kế tiếp, để phiên/người mới nối tiếp nhanh. Kế hoạch đầy đủ & phân việc: [work-division.md](work-division.md). Lý do quyết định: [decisions.md](decisions.md).
> **Cập nhật mỗi khi đổi trạng thái** (tan ca). Cập nhật lần cuối: **2026-07-11** (vòng giám sát: **D1·BC6·E2·P1** (vòng 1) + **P7·E3·E4·BC13** (vòng 2) + **P4·C11·E8** (vòng 3) + **P5·C12·BC11** (vòng 4) + **P6·E5·BC9** (vòng 5) + **P8a·E6·BC7** (vòng 6) passing, integrated vào `docs/sync-design-d18-d21`).

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

### Vòng giám sát 2026-07-11 (vòng 3) — 3 task passing (3 worker song song, mỗi service 1 worker)
| Task | Nội dung | Trạng thái |
|---|---|---|
| **P4** | `POST /internal/credits/reserve` (`X-Internal-Token`) → `CreditAccountService.ReserveAsync`: reservation chèn trước (UNIQUE(session_id)=khoá idempotency) + bút toán atomic `ExecuteUpdate WHERE remaining≥1` → hết → **402** no-orphan | ✅ **passing (0409ba4)** · Payment 13/13 (+6) · reuse schema P1 (không migration) · ⚠ no-wallet→402(block); postpaid→P8a · chưa wire P2 |
| **C11** | JD/Criteria nhập **text trực tiếp** (`jdText`/`criteriaText`) — text ưu tiên file; publish vẫn sinh `campaign_criteria` từ text | ✅ **passing (4425461)** · Campaign 22/22 (+5) · không migration (cột có từ `InitialCreate`) · ⚠ `CreateCampaign` set `CreatedAt/UpdatedAt` trong code (SQLite-testable) |
| **E8** | Guard điểm C# ở callback chấm: criterion ngoài rubric session → bỏ; điểm → kẹp `[0,maxScore]` (INT-9); áp B2B & B2C | ✅ **passing (2dfb3c3)** · Interview 41/41 (+3) · không migration · defense-in-depth cho callback (dùng branch rubric E1) |

> Base test sau vòng 3: **Auth 4 · Payment 13 · Campaign 22 · Interview 41 = 80 .NET pass** (+ AIService pytest 29), `dotnet build` 0 error. Cả 3 worker được pin base SHA `6e1d93a` trong brief → re-anchor sạch (không lệch merge-base như vòng 2). Merge `--no-ff` vào `docs/sync-design-d18-d21`: `e45f6b6`(P4)·`dc9a25e`(C11)·`8997818`(E8).

### Vòng giám sát 2026-07-11 (vòng 4) — 3 task passing (3 worker song song ở phiên riêng, pin base `9ad7427`)
| Task | Nội dung | Trạng thái |
|---|---|---|
| **P5** | `POST /internal/credits/consume` khi Scored → reservation Reserved→Consumed (`ExecuteUpdate WHERE status=Reserved`) + `reserved−1` + `credit_transactions(Consume,−1)`; owner từ reservation | ✅ **passing (374db18)** · Payment 17/17 (+4) · reuse schema P1 (không migration) · absorbing PAY-11 (Consumed/Released/miss-reserve → no-op) · ⚠ chưa wire consumer RabbitMQ (E7/BC3); postpaid → P8a |
| **C12** | `criteria[]` structured HR khai thẳng → replace-all atomic `campaign_criteria(source=HrEdited,order_no)`; Σweight chuẩn hoá→1/ngoài→400; sửa Active→409; publish bỏ AI khi có criteria[] | ✅ **passing (656402c)** · Campaign 35/35 (+13) · migration `AddCriteriaOrderAndUpdatedAt` (không apply Neon; has-pending=No changes) · ⚠ **apply-caveat:** backfill `order_no` rows cũ trước UNIQUE(campaign_id,order_no) nếu >1 tiêu chí/campaign |
| **BC11** | Seed rubric B2C mặc định (BA/BE/FE, `campaign_id=null`, Σweight=1, is_active) qua `HasData` bọc `if(IsNpgsql())` → B2C upload có publish job chấm | ✅ **passing (e2b6016)** · Interview 47/47 (+6) · migration `SeedB2CRubric` (không apply Neon; has-pending=No changes) · ⚠ seed chỉ Npgsql (giữ SQLite controlled không phá E1); CRUD rubric để sau |

> Base test sau vòng 4: **Auth 4 · Payment 17 · Campaign 35 · Interview 47 = 103 .NET pass** (+ AIService pytest 29), `dotnet build` 0 error. 3 worker chạy phiên riêng (worktree `../isas-P5|C12|BC11`), pin base `9ad7427` → anchor sạch. Merge `--no-ff`: P5·C12·BC11.

### Vòng giám sát 2026-07-11 (vòng 5) — 3 task passing (3 worker phiên riêng, pin base `d5a2aa5`, prompt kèm Context7 EF Core)
| Task | Nội dung | Trạng thái |
|---|---|---|
| **P6** | `POST /internal/credits/release` → reservation Reserved→Released (`ExecuteUpdate WHERE status=Reserved`) + `reserved−1`/`remaining+1` (hoàn chỗ giữ, không ghi ledger); owner từ reservation | ✅ **passing (8c8c885)** · Payment 21/21 (+4) · reuse schema P1 (không migration) · absorbing PAY-11 (Consumed→no-op không hoàn oan) · ⚠ postpaid→P8a; chưa wire consumer (E7/BC4) |
| **E5** | `GET /campaign/{id}/results` → sắp `total_score` DESC + rank read-time (competition) + pass/fail theo `pass_score_pct`; chỉ Scored; ngoài org→404 | ✅ **passing (1774d50)** · Campaign 40/40 (+5) · migration `AddCampaignPassScorePct` (1 cột nullable; không apply Neon; has-pending=No changes) · ⚠ rank competition (đổi được sang ROW_NUMBER); cột rank/result thừa E4 drop sau |
| **BC9** | Session B2C Scored → `overall_score`(equal-weight INT-10)+`answered_count`+`session_criterion_scores`; GET trả `overallScore`+`criteriaScores[]`+`needsImprovement[]`; B2B no-op | ✅ **passing (1cc2fb0)** · Interview 60/60 (+13) · migration `AddSessionResultBC9` (cột + bảng UNIQUE(session_id,criterion_id); không apply Neon; has-pending=No changes) · ⚠ compute trong `SessionScoringNotifier` best-effort (lỗi không chặn Scored); `overallComment` chờ BC10 |

> Base test sau vòng 5: **Auth 4 · Payment 21 · Campaign 40 · Interview 60 = 125 .NET pass** (+ AIService pytest 29), `dotnet build` 0 error. 3 worker phiên riêng (worktree `../isas-P6|E5|BC9`), pin base `d5a2aa5`, prompt nhúng Context7 EF Core (ExecuteUpdate/tx · ordering-rank · GroupBy-Average+SQLite ef_avg). Merge `--no-ff`: P6·E5·BC9.

### Vòng giám sát 2026-07-11 (vòng 6) — 3 task passing (3 worker phiên riêng, pin base `46578c5`, Context7 CsvHelper/QuestPDF/EF)
| Task | Nội dung | Trạng thái |
|---|---|---|
| **P8a** | Postpaid: `ReserveAsync` nhánh postpaid — guard `(period_usage ?? 0)+reserved+1 ≤ credit_limit`, vượt → 402 no-orphan; prepaid P4 giữ nguyên | ✅ **passing (f2a4a5a)** · Payment 27/27 (+6) · không migration (credit_limit/period_usage sẵn) · ⚠ reserve KHÔNG cộng period_usage (payment.md:377) → **BK7** consume postpaid; `credit_limit=NULL`→402 |
| **E6** | Export CSV `GET /campaign/{id}/results/export?format=csv` (CsvHelper, khớp E5); pdf/xlsx→400; ngoài org→404 | ✅ **passing (01bbf28)** · Campaign 46/46 (+6) · +CsvHelper 33.1.0 · không migration · ⚠ **PDF hoãn** (SkiaSharp risk) → **BK8**; cột CSV chưa chốt doc |
| **BC7** | `POST /api/practice/cv-analysis` → parse + AIService `/analyze-cv` → 201 + `cv_analyses` (jsonb); GET chỉ chủ; AI lỗi→502; AI không ghi DB | ✅ **passing (e612908)** · Interview 70/70 (+10) · migration `AddCvAnalyses` (không apply Neon; has-pending=No changes) · ⚠ **cần chốt doc:** `jobCategory` thêm vào request → **BK6**; billing BC-4 mâu thuẫn → **BK5** |

> Base test sau vòng 6: **Auth 4 · Payment 27 · Campaign 46 · Interview 70 = 147 .NET pass** (+ AIService pytest 29), `dotnet build` 0 error. Merge `--no-ff`: P8a·E6·BC7. **Backlog từ ghi chú:** BK1–BK4 (dọn dẹp) + **BK5** (DECISION billing CV — cần team chốt) · **BK6** (ratify jobCategory) · **BK7** (postpaid consume period_usage) · **BK8** (E6 PDF) — xem §Backlog trong tasks.md.

> Test project trong tree hiện có: `Isas.InterviewService.Tests`, `Isas.AuthService.Tests`, `Isas.CampaignService.Tests`. Payment **chưa** có (Phase 0 `P0.4`).

## Vấn đề đã biết / cần xác minh
- **E1 → e2e thật bị chặn bởi D2:** chưa có HTTP entry tạo session B2B (magic-link) → luồng B2B end-to-end (campaign → ứng viên làm bài → chấm → ranking) chưa chạy trọn được. Chứng minh E1 ở mức engine bằng unit/integration test.
- **Campaign chưa wire `org_id`:** code dùng `employer_id` (claim). A1 đã có `Organization`/`org_members` trên main → cần task nối campaign sang `org_id` (credit theo org cần cái này).
- **PaymentService** trên branch theo **credit cá nhân (`user_id`)** — cần refactor sang **org-credit + reserve/consume + postpaid** theo [services/payment.md](services/payment.md). Chưa vào CI/pipeline (CI build 4 service: Auth/Campaign/Gateway/Interview; AIService deploy tay trên Mac).
- **AIService** `/suggest-criteria` đã **live HTTP** nhưng deploy qua `docker cp`+restart → **ephemeral**; muốn permanent phải **rebuild image** từ Dockerfile. Các vấn đề nền còn nguyên: 🔴 `/ai/**` public không auth · 🔴 Whisper `large-v3` CPU chậm · 🔴 prompt injection bộ chấm · 🔴 `nack` không DLQ ([services/ai.md](services/ai.md) §Vấn đề).
- ✅ **Trần `orderCode` PayOS đã verify** (D12, 2026-06-28): ≤ **9.007.199.254.740.991** (2^53−1) → snowflake 64-bit vượt trần (loại). Thêm ràng buộc PayOS: `description` ≤25 ký tự, webhook **HMAC-SHA256** — bảng đầy đủ [services/payment.md](services/payment.md) §PayOS.
- **CI/CD chung Neon (DB server):** không tự apply migration lên DB chung — schema apply qua pipeline/tay trước deploy.

## Bước tiếp theo (thứ tự đề xuất)
> ✅ Vòng 1: **E2, D1, P1, BC6**. ✅ Vòng 2: **P7, E3, E4, BC13**. ✅ Vòng 3: **P4, C11, E8**. ✅ Vòng 4: **P5, C12, BC11**. ✅ Vòng 5: **P6, E5, BC9**. ✅ Vòng 6: **P8a, E6, BC7** (xem bảng trên). Frontier mới bên dưới.
> **⚠ Cần người/team chốt (không tự quyết):** **BK5** — CV analysis tính phí hay free? (rules.md BC-4 ✗ interview.md/D17 mâu thuẫn) → chặn hướng billing của BC7/BC-4/BC1–BC4.
1. **D2** (S3, Campaign+Interview — **cross-service**): mở token → account Candidate + create-or-get session. **Gỡ chặn HTTP entry B2B** cho I1/E1 (D1 ✅ + I1 ✅). ⚠ đụng Campaign+Interview (+ Auth account nhẹ) → **chạy solo**, không song song worker Campaign/Interview khác.
2. **P2** (Payment, mua pack → webhook PayOS cộng credit — cần P7 ✅ + P4 ✅). ⚠ cần **PayOS sandbox + webhook thật** để e2e → verify tay ngoài worker nền. Mở khoá BC1/A4. (reserve/consume/release P4/P5/P6 ✅ + postpaid P8a ✅ — credit đủ.)
3. **S5 B2C** (BC11 ✅ rubric · BC9 ✅ tổng kết · BC7 ✅ cv-analysis): **BC2** (reserve ví cá nhân — cần P4 ✅) · **BC3** (consume — cần BC2 + P5 ✅) · **BC4** (release — cần BC2 + P6 ✅ + E3 ✅) · **BC10** (nhận xét AI buổi luyện — cần BC9 ✅) · **BC8** (báo cáo CV↔trả lời — cần BC7 ✅ + E1 ✅) · BC1 (cần P2).
4. **E7** (Payment consume/release phản ứng event — cần P5 ✅/P6 ✅ + E2 ✅/E3 ✅; chỉ còn wire consumer RabbitMQ). (E5 ✅ ranking · E6 ✅ export xong.)
5. **Lọc CV B2B** (`C13`→`C14`→`C15`): C13 (bulk upload + hard-filter, Campaign) không bị chặn; C14 cần C13 + BC6 ✅.
6. **I2** (per-question time-limit + materialize deadline lên session — gỡ design gap E3).
7. **AIService roadmap DB side** `BC12`→`BC14`→`BC15` (D20, cần BC13 ✅ + BC9/BC11 ✅).
8. **Auth A4/A5**: A5 (bật lại `[Authorize(Roles)]` mọi service) — **cross-cutting**, chạy vòng ít worker; A4 cần P2.
9. **Phase 0 còn lại**: `P0.1` (compose máy sạch), `P0.2` (`make setup/test/check`), `P0.5` (readiness + checkpoint). *(P0.3/P0.4 ✅.)*

> Quy trình **vào ca / tan ca**: xem [../AGENTS.md](../AGENTS.md).
