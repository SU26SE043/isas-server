# ISAS — Task Surface (WIP=1)

> Bề mặt phạm vi máy-đọc-được (Bài 07). Mỗi task = **1 hành vi** + **lệnh xác minh chạy được** + **phụ thuộc** + **trạng thái**.
> **WIP=1 per-người/agent**: 1 task `active` → verify `passing` → commit → mới sang task kế. Đừng mở nhiều task, đừng tiện tay refactor. Quy tắc: [AGENTS.md](../AGENTS.md). Lý do thiết kế: [decisions.md](decisions.md). Phân việc: [work-division.md](work-division.md).
> **Trạng thái:** `not_started` · `active` · `blocked` · `passing`. **VCR** = passing / đã-active; chỉ mở task mới khi task đang active đã `passing`.
> **Nguồn sự thật task-level (Bài 08):** mọi "cần làm gì" bắt nguồn từ đây — KHÔNG từ chat / TODO rải rác. §8 work-division = nhóm + lý do; file này = trạng thái thật.
> **Pass-gating:** `passing` chỉ khi qua **3 lớp Xong** (build → runtime → end-to-end) **VÀ người khác review PR** — xem [AGENTS.md](../AGENTS.md) §Định nghĩa "Xong". Lệnh ở cột *Xác minh* là **lớp 2/3** của task; lớp 1 (`dotnet build`/lint) chạy chung. Ghi **bằng chứng** vào ô Status: `passing (commit abc1234)`. Không tự nâng trạng thái.
> *(Tạm để `docs/` dạng Markdown — đủ cho team người + agent thỉnh thoảng. Khi tự động hoá (scheduler/verifier) thì chuyển `feature_list.json`. Cập nhật trạng thái lúc "tan ca".)*

---

## Phase 0 — Foundation/Init (làm TRƯỚC mọi feature)
| ID | Hành vi | Xác minh | Dep | Status |
|---|---|---|---|---|
| P0.1 | 6 service + hạ tầng lên từ máy sạch | `docker compose up` → mỗi service `GET /health` trả 200 | — | not_started |
| P0.2 | Lệnh chuẩn hoá setup/dev/test/check | `make check` chạy lint + test, exit 0 | — | not_started |
| P0.3 | Test project Campaign + 1 test mẫu | `dotnet test Isas.CampaignService.Tests` → 1/1 pass | — | ✅ **xong (merged PR #22)** — `Isas.CampaignService.Tests` (SQLite in-mem, xUnit+Moq), **34 test pass** (cùng C1–C10); `Isas.AuthService.Tests` cũng đã có trong tree |
| P0.4 | Test project Payment + 1 test mẫu | `dotnet test Isas.PaymentService.Tests` → 1/1 pass | — | not_started |
| P0.5 | Readiness 4 điều kiện xanh + commit checkpoint | 4 điều kiện ([AGENTS.md](../AGENTS.md)) xanh; `git log` có commit "init baseline" | P0.1–P0.4 | not_started |

## S1 — Identity & Org (AuthService)
| ID | Hành vi | Xác minh | Dep | Status |
|---|---|---|---|---|
| A1 | Bảng `organizations` + `org_members` (migration) | migration apply; tạo 1 org + 1 member OK | — | ✅ **merged main (PR #23)** · entity `Organization`/`OrgMember` (PK `(org_id,user_id)`, `org_role` varchar(16)) + migration `AddOrganizations` + test ✅ (round-trip SQLite) · ⚠ apply schema DB chung (Neon) qua pipeline trước deploy |
| A2 | JWT mang `org_id` + `org_role` | login → decode token có claim `org_id`, `org_role` | A1 | ✅ **merged main (PR #23)** · `JwtService` thêm claim `org_id`+`org_role` khi user thuộc org; lookup `OrgMember` ở login+refresh + test ✅ (2/2) · ⚠ e2e login-HTTP chờ stack chạy |
| A3 | Đăng ký tổ chức → tạo org + OrgAdmin | `POST /auth/register-org` → org tạo, user = OrgAdmin | A1 | ✅ **merged main (PR #23)** · `POST /auth/register-org` (public) → role `Employer` + `Organization` + `OrgMember(OrgAdmin)` → `AuthResponse` (token mang org_id/org_role) + test ✅ · ⚠ e2e HTTP chờ stack chạy |
| A4 | HrMember bị chặn endpoint billing | HrMember gọi `POST /payment/order` → 403 | A2, P2 | not_started |
| A5 | Bật lại `[Authorize(Roles)]` mọi service | gọi ẩn danh endpoint cần auth → 401 | A2 | not_started |

## S1 — Payment (PaymentService, refactor theo doc)
| ID | Hành vi | Xác minh | Dep | Status |
|---|---|---|---|---|
| P1 | `credit_accounts(owner_type)` + `credit_reservations` + `credit_transactions` | migration apply; tạo account cho org | A1 | not_started |
| P7 | `order_code` time+random, unique + retry | sinh 10k code không trùng; **≤ 9.007.199.254.740.991** (2^53−1, trần PayOS đã verify — D12) | — | not_started |
| P2 | Mua pack OneTime → webhook PayOS cộng credit | order (sandbox) → webhook → `remaining += interview_credits`; webhook lần 2 **không** cộng lại | P1, P7 | not_started |
| P3 | `/order/{id}/status` active-polling đối soát | chưa có webhook → `GET status` → server gọi PayOS get-payment-info → trả Paid | P2 | not_started |
| P4 | `/internal/credits/reserve` (chặn khi hết) | reserve → `reserved+1`; hết quota/hạn mức → 402 | P1 | not_started |
| P5 | `/internal/credits/consume` khi `SessionScored` | consume → `credit_transactions(Consume,-1)`; gọi 2 lần cùng sessionId chỉ trừ 1 | P4, E2 | not_started |
| P6 | `/internal/credits/release` khi bỏ ngang/lỗi | release → reservation `Released`, không trừ credit | P4 | not_started |
| P8a | Postpaid: hạn mức + dồn nợ | postpaid org reserve tới `credit_limit` → vượt → 402 | P1, P4 | not_started |
| P8b | Postpaid: hóa đơn cuối kỳ + tất toán | chốt kỳ → `invoice` (`interview_count×unit_price`); `POST /invoices/{id}/pay` → PayOS → nợ về 0 | P8a, P2 | not_started |

## S2 — Campaign (CampaignService)
| ID | Hành vi | Xác minh | Dep | Status |
|---|---|---|---|---|
| C1 | Lưu **key** thay full URL | upload JD → `jd_file_url == "campaigns/{id}/jd.pdf"`; download → 200 | — | ✅ **merged main (PR #22)** · fix+build ✅ · ⚠ e2e runtime chờ stack |
| C2 | `GET /campaign` lọc theo chủ (org) | employer A → `GET /campaign` không thấy campaign của B | A2 | ✅ **merged main (PR #22)** · unit test ✅ — lọc theo **employer_id** (⚠ chưa wire `org_id` dù A1 đã có org) |
| C3 | `AntiCheatEnabled` → `bool?` | `PUT` không gửi field → giá trị không đổi | — | ✅ **merged main (PR #22)** · unit test ✅ |
| C4 | Download đúng content-type + 404 | id lạ → 404; pdf → `Content-Type: application/pdf` | C1 | ✅ **merged main (PR #22)** · fix+build ✅ · ⚠ e2e runtime chờ stack |
| C5 | Chỉ nhận PDF (sửa message) | upload `.docx` → 400 "Only PDF" | — | ✅ **merged main (PR #22)** · fix+build ✅ |
| C6 | Bật `[Authorize(Roles="Employer")]` | gọi ẩn danh → 401/403 | A5 | ✅ **merged main (PR #22)** · `[Authorize(Roles="Employer")]` bật (JWT wired) · ⚠ e2e runtime chờ stack |
| C7 | Lifecycle Draft→Active→Closed→Archived | sửa câu hỏi khi `Active` → 409; transition hợp lệ pass | — | ✅ **merged main (PR #22)** · guard + transition (publish: Draft→Active; `PUT /status`: Active→Closed→Archived; sai → 409) + unit test ✅ |
| C8 | Publish → AI đề xuất tiêu chí có cấu trúc | publish → `campaign_criteria` có `weight`, Σweight=1 | AI-crit | ✅ **merged main (PR #22)** · `POST /publish` → `campaign_criteria` Σ=1 + audit · AIService `/suggest-criteria` (Gemini) + fallback · unit test ✅ + live HTTP ✅; ⚠ image rebuild để permanent + HR duyệt UI |
| C9 | Soft delete + filter | DELETE → `deleted_at` set; GET không trả campaign đã xóa | — | ✅ **merged main (PR #22)** · soft-delete + filter + unit test ✅ |
| C10 | `audit_logs` khi mutation | đổi tiêu chí → 1 row `audit_logs(actor, action)` | — | ✅ **merged main (PR #22)** · ghi ở Create/EditQuestions/Delete/Publish/Transition + unit test ✅ |
| C11 | JD & Criteria nhập **text trực tiếp** (`jdText`/`criteriaText`) — không cần PDF | tạo/sửa campaign với body `jdText`/`criteriaText` (không file) → `jd_text`/`criteria_text` set, `*_file_url` null; nhập **cả text+file → ưu tiên text** (bỏ file); `GET /campaign/{id}` trả JD; publish vẫn sinh `campaign_criteria` từ text | — | not_started · spec: [campaign.md](services/campaign.md) §API · code hiện chỉ nhận PDF (C5) — thêm nhánh text, AI đọc `*_text` bất kể nguồn |
| C12 | Criteria **structured** HR khai thẳng (`criteria[]` = `CriterionItem[]`) | tạo/sửa campaign (Draft) với `criteria[]` (`{name,description?,weight,maxScore}`) → **replace-all atomic** `campaign_criteria(source=HrEdited)` (`order_no`, **UNIQUE name/campaign**); `Σweight∈[0.99,1.01]`→**chuẩn hoá Σ→1**, ngoài→**400**; `0<weight≤1`,`maxScore≥1`,name không trùng; sửa khi `Active`→**409**; **publish bỏ qua AI** khi đã có `criteria[]` | — | not_started · spec: [campaign.md](services/campaign.md) §`campaign_criteria` + §Tiêu chí chấm · **có migration** (thêm `order_no`/`updated_at` + 2 unique) · ưu tiên hơn text/PDF — HR khai đúng trường, không để AI suy từ text |

## S3 — Distribution & Execution
| ID | Hành vi | Xác minh | Dep | Status |
|---|---|---|---|---|
| I1 | Session nhận `campaign_id` + materialize tiêu chí | tạo session B2B → `rubric_criteria(campaign_id)` có rows | C8 | ✅ **merged main (PR #24)** · `CreateCampaignSessionAsync` (B2B: campaign cấp questions+criteria) → session gắn `campaign_id` + materialize criteria → `rubric_criteria(campaign_id)` idempotent theo campaign + test ✅ (2/2) · không cần migration (cột `campaign_id` có sẵn từ `AddCampaignIdToEngine`) · ⚠ HTTP entry chờ D2 |
| D1 | `campaign_invitations` + magic-link token | `POST …/invitations` → token tạo, email queue | C7 | not_started |
| D2 | Mở token → account Candidate + create-or-get session | mở token → Candidate account + session gắn `campaign_id` | D1, I1 | not_started |
| D3 | Resume — mở lại token → đúng session cũ | mở token 2 lần → cùng `session_id`; câu đã nộp giữ nguyên | D2 | not_started |
| D4 | Khóa sau submit + re-issue | sau submit token `used` (403); reissue → token mới, cũ vô hiệu | D2 | not_started |

## S4 — Evaluation, Ranking & Result
| ID | Hành vi | Xác minh | Dep | Status |
|---|---|---|---|---|
| E1 | Chấm B2B theo tiêu chí campaign | session B2B `Scored` → `answer_scores` trỏ tiêu chí campaign | I1 | active · chọn tiêu chí khi build job chấm **branch theo `campaign_id`** ở `AnswerService.TryPublishScoringJobAsync` + `StuckAnswerRepublisher` (B2B→`rubric_criteria(campaign_id)`; B2C→`job_category` + `campaign_id IS NULL`, chống rò chéo) + test ✅ (B2B publish chọn campaign / B2C isolation / republish B2B / Done: B2B `Scored`→`answer_scores` trỏ campaign) — `dotnet test` **31/31 pass** · không cần migration (cột `campaign_id` có sẵn) · worker Python KHÔNG đổi (D9) · ⚠ e2e đầy đủ chờ HTTP entry B2B (**D2**) + chờ PR review |
| E2 | Phát event `SessionScored` | session `Scored` → RabbitMQ có message (campaign_id + điểm) | E1 | not_started |
| E3 | Phát `SessionAbandoned` (TTL/0-answer) | `InProgress` quá hạn → message `SessionAbandoned` | — | not_started |
| E4 | Campaign consume event → `campaign_rankings` | event → 1 row ranking (điểm có trọng số); gửi 2 lần chỉ 1 row | E2 | not_started |
| E5 | Xếp hạng + pass/fail | `GET /campaign/{id}/results` → sắp theo điểm, pass/fail theo ngưỡng | E4 | not_started |
| E6 | Xuất CSV/PDF | `GET …/results/export?format=csv` → file khớp ranking | E5 | not_started |
| E7 | Payment phản ứng event (consume/release) | `SessionScored`→consume; `SessionAbandoned`→release | P5, P6, E2, E3 | not_started |
| E8 | Guard điểm phía C# ở callback chấm (phòng worker lỗi / image lệch) | `POST /internal/answers/{id}/result`: score > `maxScore` → **kẹp** về maxScore; `criterionId` không thuộc rubric của session → **bỏ** (không lưu); test pass | E1 | not_started · spec: [interview.md](services/interview.md) §Đánh giá cách chấm tiêu chí #4 · defense-in-depth (worker đã kẹp/lọc nhưng C# tin 100% + AIService deploy ephemeral); áp cho cả B2B & B2C |
| E9 | Chấm **neo theo mức** (levels/anchors) → đúng mức + ổn định | message chấm có `levels:[{score,descriptor}]`(+`anchors?`); AI trả `{score, levelMatched, reasoning}` với `score=levelMatched.score`; mức lạ → **reject** (worker+C#); lưu `answer_scores.level_matched`; **B2B sinh levels khi publish/materialize** | E1 | not_started · spec: [interview.md](services/interview.md) §Chất lượng & độ nhất quán · migration `answer_scores.level_matched` · áp B2B & B2C |
| E10 | Đo & chặn **chênh lệch** (self-consistency) | chấm N lần (`Scoring:SelfConsistencyN`) → điểm chốt = **median**/tiêu chí (attempt_no tăng); spread (max−min) > `Scoring:VarianceThreshold` → `practice_answers.needs_review=true`, không tự chốt | E9 | not_started · spec: §Chất lượng & độ nhất quán · migration `practice_answers.needs_review` · tốn N× AI → **bật chọn lọc** (throughput là trần) |
| E11 | Chuẩn **nhận xét** + HR chốt | `reasoning`/`overall_comment` trích **≥1 dẫn chứng** transcript, chặn rỗng, **bọc chống prompt-injection** ("chấm tối đa" không lái điểm); transcript+điểm+`needs_review` hiện cho HR review (điểm AI = gợi ý) | E9, BC10 | not_started · spec: §Chất lượng & độ nhất quán + [ai.md](services/ai.md) §Reliability |

## S5 — B2C Personal Practice (Payment + Interview)
> Engine luyện + lịch sử **đã chạy** (`PracticeController`); các task dưới = **nối thanh toán ví cá nhân** vào engine. Ví dùng chung `credit_accounts(owner_type)` của S1, khác `owner_type=User`.

| ID | Hành vi | Xác minh | Dep | Status |
|---|---|---|---|---|
| BC1 | Mua pack prepaid ví cá nhân (`owner=User`) → webhook cộng credit | order sandbox `owner=User` → webhook → ví user `remaining += credits`; webhook lần 2 **không** cộng lại | P1, P7 | not_started |
| BC2 | Interview `CreateSession` reserve credit ví cá nhân; hết → 402 | có credit → `POST /api/practice/sessions` tạo session + `reserved+1`; ví 0 credit → **402, không có row session** | P1, P4 | not_started |
| BC3 | Consume credit ví cá nhân khi `SessionScored` | session B2C `Scored` → `credit_transactions(Consume,-1)` ví user; 2 lần cùng `sessionId` chỉ trừ 1 | BC2, P5 | not_started |
| BC4 | Release credit ví cá nhân khi bỏ ngang/lỗi | session B2C `Abandoned` → reservation `Released`, không trừ credit | BC2, P6, E3 | not_started |
| BC5 | (verify) Lịch sử cá nhân đọc đúng chủ | `GET /api/practice/sessions/history` chỉ trả session của user; `GET /{id}` của người khác → 403/404 | — | not_started |
| BC6 | AIService `POST /analyze-cv` (feedback + khớp JD) | `{ cvText, jdText?, jobCategory }` → `{ summary, strengths[], weaknesses[], suggestions[], jdMatch? }`; có `jdText` → `jdMatch.{score,matchedSkills,missingSkills}` | — | not_started |
| BC7 | Interview `cv-analysis` endpoint + lưu `cv_analyses` | `POST …/practice/cv-analysis {cvId, jdId?}` → parse + gọi AIService → 201 + row `cv_analyses`; `GET …/cv-analysis/{id}` đọc lại đúng chủ | BC6 | not_started |
| BC8 | Báo cáo buổi luyện thêm "CV vs câu trả lời" | session `Scored` có CV → báo cáo có mục đối chiếu CV↔transcript (chỗ CV mạnh nhưng trả lời yếu) | BC7, E1 | not_started |
| BC9 | Tổng kết điểm B2C sau `Scored` (điểm tổng + điểm/tiêu chí + cần cải thiện) | session B2C `Scored` → DB có `practice_sessions.overall_score` + rows `session_criterion_scores`; `GET /api/practice/sessions/{id}` trả `overallScore` (0–100) + `criteriaScores[]` (điểm/tiêu chí / `maxScore`) + `needsImprovement[]` (tiêu chí dưới ngưỡng) | — | not_started · spec đầy đủ: [interview.md](services/interview.md) §Tổng kết điểm buổi luyện B2C (BC9) · **lưu DB** (cột `overall_score`/`answered_count` + bảng `session_criterion_scores`) khi `Scored`, **CÓ migration**, không AI, chỉ B2C |
| BC10 | Nhận xét chung buổi luyện B2C (AI sinh) | session B2C `Scored` → AIService `POST /summarize-session` sinh `overall_comment` (best-effort) → lưu `practice_sessions.overall_comment`; `GET /sessions/{id}` → `result.overallComment` có text; AI lỗi → `Scored` vẫn xong, comment null | BC9 | not_started · spec: [interview.md](services/interview.md) §Nhận xét chung buổi luyện B2C (BC10) + [ai.md](services/ai.md) `/summarize-session` · **AI sync** (D17 pattern, best-effort), cột `overall_comment` (migration), chỉ B2C |
| BC11 | Nguồn rubric B2C theo `JobCategory` (seed mặc định + tuỳ chọn CRUD) | DB có `rubric_criteria` (`campaign_id IS NULL`) cho mỗi `JobCategory` (BA/BE/FE), Σweight=1, `is_active`; tạo session B2C → upload answer → **có publish job chấm** (hết "không có tiêu chí active") | — | not_started · spec: [interview.md](services/interview.md) §Đánh giá cách chấm tiêu chí #3 · **prerequisite cho chấm B2C** (BC2/BC9): hiện repo chưa seed/CRUD rubric B2C → answer B2C không được chấm |

---

> **Lưu ý phụ thuộc chéo:** `A1` (org) mở khóa S1-Payment + S2(C2). `C8`→`I1`→toàn bộ S4. `E2/E3`→`E7` (Payment). `P1`/`P4`→**S5** (BC1–BC4) — S5 **độc lập B2B** nên là luồng cho **E2E demo được sớm nhất** sau khi có ví credit. Bám DAG này khi chọn task: ưu tiên task **không bị block** và **đang được dep cần tới**.
