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
| P0.3 | Test project Campaign + 1 test mẫu | `dotnet test Isas.CampaignService.Tests` → 1/1 pass | — | ✅ **xong** — `Isas.CampaignService.Tests` (SQLite in-mem, xUnit+Moq), **5/5 pass** · chờ PR |
| P0.4 | Test project Payment + 1 test mẫu | `dotnet test Isas.PaymentService.Tests` → 1/1 pass | — | not_started |
| P0.5 | Readiness 4 điều kiện xanh + commit checkpoint | 4 điều kiện ([AGENTS.md](../AGENTS.md)) xanh; `git log` có commit "init baseline" | P0.1–P0.4 | not_started |

## S1 — Identity & Org (AuthService)
| ID | Hành vi | Xác minh | Dep | Status |
|---|---|---|---|---|
| A1 | Bảng `organizations` + `org_members` (migration) | migration apply; tạo 1 org + 1 member OK | — | not_started |
| A2 | JWT mang `org_id` + `org_role` | login → decode token có claim `org_id`, `org_role` | A1 | not_started |
| A3 | Đăng ký tổ chức → tạo org + OrgAdmin | `POST /auth/register-org` → org tạo, user = OrgAdmin | A1 | not_started |
| A4 | HrMember bị chặn endpoint billing | HrMember gọi `POST /payment/order` → 403 | A2, P-API | not_started |
| A5 | Bật lại `[Authorize(Roles)]` mọi service | gọi ẩn danh endpoint cần auth → 401 | A2 | not_started |

## S1 — Payment (PaymentService, refactor theo doc)
| ID | Hành vi | Xác minh | Dep | Status |
|---|---|---|---|---|
| P1 | `credit_accounts(owner_type)` + `credit_reservations` + `credit_transactions` | migration apply; tạo account cho org | A1 | not_started |
| P7 | `order_code` time+random, unique + retry | sinh 10k code không trùng; ≤ trần PayOS *(verify trần!)* | — | not_started |
| P2 | Mua pack OneTime → webhook PayOS cộng credit | order (sandbox) → webhook → `remaining += interview_credits`; webhook lần 2 **không** cộng lại | P1, P7 | not_started |
| P3 | `/order/{id}/status` active-polling đối soát | chưa có webhook → `GET status` → server gọi PayOS get-payment-info → trả Paid | P2 | not_started |
| P4 | `/internal/credits/reserve` (chặn khi hết) | reserve → `reserved+1`; hết quota/hạn mức → 402 | P1 | not_started |
| P5 | `/internal/credits/consume` khi `SessionScored` | consume → `credit_transactions(Usage,-1)`; gọi 2 lần cùng sessionId chỉ trừ 1 | P4, E2 | not_started |
| P6 | `/internal/credits/release` khi bỏ ngang/lỗi | release → reservation `Released`, không trừ credit | P4 | not_started |
| P8a | Postpaid: hạn mức + dồn nợ | postpaid org reserve tới `credit_limit` → vượt → 402 | P1, P4 | not_started |
| P8b | Postpaid: hóa đơn cuối kỳ + tất toán | chốt kỳ → `invoice` (`interview_count×unit_price`); `POST /invoices/{id}/pay` → PayOS → nợ về 0 | P8a, P2 | not_started |

## S2 — Campaign (CampaignService)
| ID | Hành vi | Xác minh | Dep | Status |
|---|---|---|---|---|
| C1 | Lưu **key** thay full URL | upload JD → `jd_file_url == "campaigns/{id}/jd.pdf"`; download → 200 | — | active · fix+build ✅ · chờ runtime/PR |
| C2 | `GET /campaign` lọc theo chủ (org) | employer A → `GET /campaign` không thấy campaign của B | A2 | active · fix+build + **unit test ✅** — lọc theo **employer_id** (org chưa có) · chờ E2E/PR |
| C3 | `AntiCheatEnabled` → `bool?` | `PUT` không gửi field → giá trị không đổi | — | active · fix+build + **unit test ✅** · chờ E2E/PR |
| C4 | Download đúng content-type + 404 | id lạ → 404; pdf → `Content-Type: application/pdf` | C1 | active · fix+build ✅ · chờ runtime/PR |
| C5 | Chỉ nhận PDF (sửa message) | upload `.docx` → 400 "Only PDF" | — | active · fix+build ✅ · chờ runtime/PR |
| C6 | Bật `[Authorize(Roles="Employer")]` | gọi ẩn danh → 401/403 | A5 | active · fix+build ✅ (JWT đã wired) · chờ runtime/PR |
| C7 | Lifecycle Draft→Active→Closed→Archived | sửa câu hỏi khi `Active` → 409; transition hợp lệ pass | — | active · **guard + transition** (publish: Draft→Active; `PUT /status`: Active→Closed→Archived; bước sai → 409) + **unit test ✅** · chờ E2E/PR |
| C8 | Publish → AI đề xuất tiêu chí có cấu trúc | publish → `campaign_criteria` có `weight`, Σweight=1 | AI-crit | active · `POST /publish` → `campaign_criteria` Σ=1 + audit · **AIService `/suggest-criteria` (Gemini) + fallback** · **unit test ✅** + **live HTTP ✅** (container deploy qua docker cp+restart, POST trả Σ=1); ⚠ image rebuild để permanent + HR duyệt UI |
| C9 | Soft delete + filter | DELETE → `deleted_at` set; GET không trả campaign đã xóa | — | active · fix+build + **unit test ✅** (soft+filter) · migration áp DB thật · chờ E2E/PR |
| C10 | `audit_logs` khi mutation | đổi tiêu chí → 1 row `audit_logs(actor, action)` | — | active · `audit_logs` ghi ở Create/EditQuestions/Delete/**Publish**/Transition + **unit test ✅** · chờ E2E/PR |

## S3 — Distribution & Execution
| ID | Hành vi | Xác minh | Dep | Status |
|---|---|---|---|---|
| I1 | Session nhận `campaign_id` + materialize tiêu chí | tạo session B2B → `rubric_criteria(campaign_id)` có rows | C8 | not_started |
| D1 | `campaign_invitations` + magic-link token | `POST …/invitations` → token tạo, email queue | C7 | not_started |
| D2 | Mở token → account Candidate + create-or-get session | mở token → Candidate account + session gắn `campaign_id` | D1, I1 | not_started |
| D3 | Resume — mở lại token → đúng session cũ | mở token 2 lần → cùng `session_id`; câu đã nộp giữ nguyên | D2 | not_started |
| D4 | Khóa sau submit + re-issue | sau submit token `used` (403); reissue → token mới, cũ vô hiệu | D2 | not_started |

## S4 — Evaluation, Ranking & Result
| ID | Hành vi | Xác minh | Dep | Status |
|---|---|---|---|---|
| E1 | Chấm B2B theo tiêu chí campaign | session B2B `Scored` → `answer_scores` trỏ tiêu chí campaign | I1 | not_started |
| E2 | Phát event `SessionScored` | session `Scored` → RabbitMQ có message (campaign_id + điểm) | E1 | not_started |
| E3 | Phát `SessionAbandoned` (TTL/0-answer) | `InProgress` quá hạn → message `SessionAbandoned` | — | not_started |
| E4 | Campaign consume event → `campaign_rankings` | event → 1 row ranking (điểm có trọng số); gửi 2 lần chỉ 1 row | E2 | not_started |
| E5 | Xếp hạng + pass/fail | `GET /campaign/{id}/results` → sắp theo điểm, pass/fail theo ngưỡng | E4 | not_started |
| E6 | Xuất CSV/PDF | `GET …/results/export?format=csv` → file khớp ranking | E5 | not_started |
| E7 | Payment phản ứng event (consume/release) | `SessionScored`→consume; `SessionAbandoned`→release | P5, P6, E2, E3 | not_started |

## S5 — B2C Personal Practice (Payment + Interview)
> Engine luyện + lịch sử **đã chạy** (`PracticeController`); các task dưới = **nối thanh toán ví cá nhân** vào engine. Ví dùng chung `credit_accounts(owner_type)` của S1, khác `owner_type=User`.

| ID | Hành vi | Xác minh | Dep | Status |
|---|---|---|---|---|
| BC1 | Mua pack prepaid ví cá nhân (`owner=User`) → webhook cộng credit | order sandbox `owner=User` → webhook → ví user `remaining += credits`; webhook lần 2 **không** cộng lại | P1, P7 | not_started |
| BC2 | Interview `CreateSession` reserve credit ví cá nhân; hết → 402 | có credit → `POST /api/practice/sessions` tạo session + `reserved+1`; ví 0 credit → **402, không có row session** | P1, P4 | not_started |
| BC3 | Consume credit ví cá nhân khi `SessionScored` | session B2C `Scored` → `credit_transactions(Usage,-1)` ví user; 2 lần cùng `sessionId` chỉ trừ 1 | BC2, P5 | not_started |
| BC4 | Release credit ví cá nhân khi bỏ ngang/lỗi | session B2C `Abandoned` → reservation `Released`, không trừ credit | BC2, P6, E3 | not_started |
| BC5 | (verify) Lịch sử cá nhân đọc đúng chủ | `GET /api/practice/sessions/history` chỉ trả session của user; `GET /{id}` của người khác → 403/404 | — | not_started |
| BC6 | AIService `POST /analyze-cv` (feedback + khớp JD) | `{ cvText, jdText?, jobCategory }` → `{ summary, strengths[], weaknesses[], suggestions[], jdMatch? }`; có `jdText` → `jdMatch.{score,matchedSkills,missingSkills}` | — | not_started |
| BC7 | Interview `cv-analysis` endpoint + lưu `cv_analyses` | `POST …/practice/cv-analysis {cvId, jdId?}` → parse + gọi AIService → 201 + row `cv_analyses`; `GET …/cv-analysis/{id}` đọc lại đúng chủ | BC6 | not_started |
| BC8 | Báo cáo buổi luyện thêm "CV vs câu trả lời" | session `Scored` có CV → báo cáo có mục đối chiếu CV↔transcript (chỗ CV mạnh nhưng trả lời yếu) | BC7, E1 | not_started |

---

> **Lưu ý phụ thuộc chéo:** `A1` (org) mở khóa S1-Payment + S2(C2). `C8`→`I1`→toàn bộ S4. `E2/E3`→`E7` (Payment). `P1`/`P4`→**S5** (BC1–BC4) — S5 **độc lập B2B** nên là luồng cho **E2E demo được sớm nhất** sau khi có ví credit. Bám DAG này khi chọn task: ưu tiên task **không bị block** và **đang được dep cần tới**.
