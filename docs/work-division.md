# ISAS — Source of Truth: Phạm vi & Phân chia công việc

> **Mục đích.** Đây là **tài liệu gốc duy nhất** để team SU26SE043 (4 người) thống nhất *làm cái gì* và *ai làm mảng nào*.
> **ISAS giao 2 dòng sản phẩm dùng chung 1 engine phỏng vấn**: **(B2C)** luyện phỏng vấn cá nhân · **(B2B)** tuyển dụng theo chiến dịch. Phân biệt bằng `campaign_id` trên session; máy chấm dùng chung. **Cả hai đều là deliverable** — B2C không phải "engine phụ" của B2B.
> Tài liệu kỹ thuật chi tiết: [architecture.md](architecture.md) (global) + `docs/services/<service>.md` (API + DB + rules mỗi service). File này không lặp lại chúng — chỉ map *scope → service → người → thứ tự build*.

---

## ✅ Quyết định kiến trúc đã chốt

**Mô hình: Engine + Orchestrator.** Không tách mỗi module một service. Tổng cộng **6 service**:

```
Gateway · AuthService · AIService · PaymentService · CampaignService · InterviewService
```

- **InterviewService = engine phỏng vấn DÙNG CHUNG** (B2B + B2C). Session có `campaign_id` *nullable*: null = B2C luyện tập; có giá trị = bài thi B2B của 1 campaign. Toàn bộ máy chấm điểm (answer, audio, rubric/tiêu chí, RabbitMQ, callback AI, republisher) tái dùng — **không build lại cho B2B**.
- **CampaignService = điều phối B2B**: tạo campaign, câu hỏi/tiêu chí (gọi AIService gợi ý câu hỏi từ JD), distribution (link/email/khóa), ranking, result/export. Khi ứng viên mở link → gọi InterviewService tạo session gắn campaign.
- **PaymentService = riêng** (thanh toán PayOS, **credit/lượt phỏng vấn theo ORG**, prepaid + postpaid, cô lập dữ liệu tiền).
- **AuthService** có **3 role Candidate/Employer/Admin**; thêm **Organization + org-role (OrgAdmin/HrMember)**. **AIService** mở rộng chấm theo tiêu chí campaign.

> Vì sao không nhét hết B2B vào CampaignService: máy chấm điểm đã chạy ổn trong InterviewService; chép lại Whisper + scoring + state machine + republisher là rủi ro thời gian lớn nhất của dự án.

---

## 0. Đọc trước khi chia việc

- **2 dòng sản phẩm, 1 engine.** Cả B2C lẫn B2B đều là sản phẩm phải giao; chúng **dùng chung** InterviewService (engine phỏng vấn) + AIService (chấm) + PaymentService (credit). Phân biệt bằng `campaign_id`: null = B2C, có giá trị = B2B.
- **B2C — luyện phỏng vấn cá nhân:** người dùng tự đăng ký → mua credit prepaid → tự tạo session từ CV/JD → AI sinh câu hỏi → ghi âm → chấm rubric `JobCategory` → xem lịch sử của mình. **Engine + lịch sử đã chạy** (`POST/GET /api/practice/sessions*`); **thiếu**: ví credit cá nhân + reserve/consume khi luyện (D15). Chi tiết: [services/interview.md](services/interview.md).
- **B2B — tuyển dụng:** Employer tạo *chiến dịch đánh giá* từ JD → phát link cho ứng viên → AI chấm theo tiêu chí & xếp hạng → xuất kết quả. **Phần lớn B2B chưa build.**
- Nguyên tắc: B2B **tái dùng nguyên engine B2C**, chỉ thêm lớp điều phối (campaign, distribution, ranking, result). B2C chỉ thiếu lớp **thanh toán ví cá nhân** nối vào engine.

---

## 1. Sản phẩm — 2 dòng (B2C + B2B)

### 1a. B2C — Luyện phỏng vấn cá nhân (4 module)

| # | Module | Mô tả ngắn | Service phụ trách | Hiện trạng |
|---|---|---|---|---|
| BC1 | **Personal Account & Wallet** | Đăng ký/đăng nhập cá nhân (Candidate, không org) + mua **credit prepaid** ví cá nhân qua PayOS (`owner_type=User`, D15) | AuthService + PaymentService | 🟡 ví chưa wire |
| BC2 | **Self-serve Practice** | Tự tạo session từ CV/JD → AI sinh câu hỏi (**bám CV/JD**, ưu tiên JD>CV>JobCategory) → ghi âm → chấm **rubric `JobCategory`**; reserve→consume credit ví cá nhân | InterviewService + AIService + PaymentService | ✅ engine + sinh câu hỏi từ CV/JD chạy · 🟡 thiếu reserve/consume |
| BC3 | **Personal History & Results** | Xem lại các buổi luyện của mình: điểm, transcript, feedback | InterviewService (đọc local) | ✅ có (`GET /api/practice/sessions/history`) |
| BC4 | **CV Analysis & Insights** | (a) **Feedback CV độc lập** (tóm tắt + điểm mạnh/yếu + gợi ý cải thiện); (b) **điểm khớp CV↔JD** (% phù hợp + kỹ năng thiếu/đủ); (c) mục **"CV vs câu trả lời"** trong báo cáo buổi luyện | InterviewService + AIService | ❌ chưa có (feature mới — D17) |

### 1b. B2B — Tuyển dụng (5 module SRS)

| # | Module | Mô tả ngắn | Service phụ trách |
|---|---|---|---|
| M1 | **Plan & Payment** | Gói dịch vụ, **credit (lượt phỏng vấn)**, thanh toán PayOS | PaymentService |
| M2 | **Assessment Campaign Mgmt** | Employer tạo chiến dịch từ JD + bộ câu hỏi/tiêu chí | CampaignService |
| M3 | **Interview Distribution** | Link bảo mật, email hàng loạt, khóa 1 lần | CampaignService (phát) + InterviewService (làm bài) |
| M4 | **AI Evaluation & Ranking** | Chấm theo tiêu chí + xếp hạng ứng viên | InterviewService/AIService (chấm) + CampaignService (ranking) |
| M5 | **Result Mgmt** | Bảng kết quả, pass/fail, xuất CSV/PDF | CampaignService (đọc điểm từ InterviewService) |

> **Dùng chung:** BC2/BC3 và M3/M4/M5 chạy trên **cùng** engine InterviewService + AIService; BC1 và M1 chạy trên **cùng** PaymentService (khác `owner_type`). B2C **không** đụng CampaignService.

---

## 2. Hiện trạng — cái gì đã có, cái gì còn thiếu

| Phần | Trạng thái | Nằm ở đâu |
|---|---|---|
| Gateway (YARP, `/api/v1/*`) | ✅ Có | `src/gateway/Isas.Gateway` |
| AuthService (JWT, OAuth, profile) | ✅ Có — 3 role + **Organization + org-role (OrgAdmin/HrMember) trong JWT + `register-org`** (A1–A3, merged PR #23); còn A4/A5 | `src/services/Isas.AuthService` |
| InterviewService = **engine phỏng vấn** | ✅ Có (B2C) — `campaign_id` (nullable) + **I1** (session B2B + materialize tiêu chí, merged PR #24) + **E1** (chấm theo tiêu chí campaign, nhánh/PR); còn **intake B2B HTTP** (create-or-get qua magic-link = S3/D2) | `src/services/Isas.InterviewService` |
| AIService (sinh câu hỏi + chấm) | ✅ Có + Docker-ready + **`/suggest-criteria`** (C8); worker chấm dùng được tiêu chí campaign (E1 — message shape không đổi) — còn analyze-cv (BC4) + vá bảo mật/throughput | `src/services/Isas.AIService` |
| Shared lib | ✅ Có | `src/shared/Isas.Shared` |
| **CampaignService (M2)** | 🟢 **merged main (PR #22)** — 6 bug fix + soft-delete (C9) + lifecycle (C7) + publish/`campaign_criteria` (C8) + audit (C10) + snake_case; còn wire `org_id`, distribution/ranking | `src/services/Isas.CampaignService` |
| **Distribution (M3 — phát link)** | ❌ Chưa | (sẽ vào CampaignService) |
| **Ranking + Result (M4/M5)** | ❌ Chưa | (sẽ vào CampaignService) |
| **PaymentService (M1)** | 🟡 **branch `features/payment-b2c`** — có Order/Package/PayOS; **cần: credit theo org, reserve/consume, postpaid + hóa đơn, active-polling** | `src/services/Isas.PaymentService` |
| Frontend B2B | 🟡 Prototype | `isas-demo/` (demo, **không trong repo này**) |

> **Hạ tầng — lưu ý:** `Redis` đã có trong compose nhưng **AuthService chưa wire** (refresh token đang lưu Postgres) — provision để dành cache/phase sau, **không phải việc của stream nào** hiện tại.

---

## 3. Kiến trúc mục tiêu (service map — Cách Engine + Orchestrator)

Giữ **microservices + Gateway**, mỗi service **1 DB riêng**, tham chiếu user **lỏng** (Guid, không FK xuyên service) — đúng quy ước [architecture.md](architecture.md) §5.

| Service | Có sẵn? | Phụ trách | Gateway prefix |
|---|---|---|---|
| **AuthService** | ✅ có 3 role | Identity: Candidate/Employer/Admin | `/api/v1/auth` |
| **PaymentService** | 🟡 branch | M1: gói, **credit (lượt phỏng vấn)**, PayOS | `/api/v1/payment` |
| **CampaignService** | 🟡 hoàn thiện | M2 + M3(phát) + M4(ranking) + M5: campaign, tiêu chí, distribution, kết quả | `/api/v1/campaign` |
| **InterviewService** | ✅ engine | Engine phỏng vấn **B2B & B2C**: session (`campaign_id?`), answer, chấm điểm | `/api/v1/interview` |
| **AIService** | ✅ mở rộng | Sinh câu hỏi + chấm (theo rubric B2C **hoặc** tiêu chí campaign) | `/api/v1/ai` |
| **Gateway** | ✅ | Reverse proxy + gộp OpenAPI | — |

**Hợp đồng giữa service cần team chốt sớm:**
1. **CampaignService → InterviewService**: **create-or-get** session gắn `campaignId` (idempotent), kèm câu hỏi + **bộ tiêu chí CÓ CẤU TRÚC**. Ứng viên vào bằng **magic-link → account Candidate**.
2. **InterviewService → Campaign + Payment (event `SessionScored` qua RabbitMQ)**: Campaign upsert **ranking read-model** (`campaign_rankings`), Payment **consume credit**. Giữ 1 endpoint HTTP backfill làm fallback.
3. **Chấm theo tiêu chí**: tiêu chí campaign **chuyển từ text → có cấu trúc** (AI đề xuất + HR duyệt lúc publish) → materialize thành `rubric_criteria(campaign_id)` → chấm như rubric thường.
4. **Campaign → PaymentService (reserve/consume/release)**: **reserve** credit của **org** khi ứng viên bắt đầu; **consume** khi `SessionScored`; **release** khi `SessionAbandoned`/lỗi. Idempotent theo `sessionId`; `X-Internal-Token`.
5. **CampaignService → AIService**: sinh câu hỏi + **đề xuất tiêu chí** từ JD/Criteria.

> Lưu ý: các service **không call Auth lúc chạy** — validate JWT offline bằng chung key. Đừng vẽ "mọi service → Auth".

---

## 4. Phân mảng công việc (4 người · 5 stream)

> **Stream = trách nhiệm nghiệp vụ, KHÔNG phải sở hữu độc quyền 1 service.** S3 và S4 đều chạm InterviewService + CampaignService — phân ranh theo *module* (S3 lo intake; S4 lo chấm + kết quả). Điền tên vào *Owner*.

| Stream | Owner | Module | Đụng service | Sản phẩm bàn giao |
|---|---|---|---|---|
| **S1 — Identity & Payment** | _____ | M1 + Org | AuthService, **PaymentService (branch)** | **Organization + org-role**; credit theo org; **prepaid + postpaid (hóa đơn)**; reserve/consume; webhook + active-polling |
| **S2 — Campaign Authoring** | _____ | M2 | CampaignService | Fix bug §7; CRUD campaign, câu hỏi, JD/tiêu chí (parse PDF); bật Auth |
| **S3 — Distribution & Execution** | _____ | M3 | CampaignService + InterviewService | Phát link 1 lần, email hàng loạt, khóa sau nộp; InterviewService nhận `campaign_id`, ứng viên vào làm bài qua token |
| **S4 — Evaluation, Ranking & Result** | _____ | M4 + M5 | AIService + InterviewService + CampaignService | Chấm theo tiêu chí campaign; tổng hợp + **xếp hạng**; bảng kết quả, pass/fail, **xuất CSV/PDF** |
| **S5 — B2C Product** | _____ | BC1–BC4 | PaymentService + InterviewService + AIService | Ví **credit cá nhân prepaid** (`owner=User`, D15); **reserve/consume khi luyện** (Interview gọi, hết → 402); xác minh E2E self-serve practice + lịch sử cá nhân; **phân tích CV** (feedback độc lập + khớp CV–JD + gắn báo cáo) |

> **S5 là stream nhẹ** — engine luyện + lịch sử (BC2/BC3) đã chạy; việc chính là **nối thanh toán ví cá nhân** vào engine. Có thể để **người S1 kiêm** (cùng đụng PaymentService) nếu đội mỏng; nhưng phải có **một owner B2C rõ ràng** để B2C không bị coi nhẹ. Cross-dep: S5 cần `credit_accounts(owner_type)` từ S1 (task `P1`).

**Việc dùng chung (luân phiên / 1 người lead):** Gateway routing, `Isas.Shared`, quy ước DB/migration, CI/CD, review chéo PR.

---

## 5. Ma trận phụ thuộc & thứ tự build

```
S1 (Auth roles) ──► tất cả        (mọi nơi cần biết Employer/Candidate/Admin)
S1 (Payment credit_accounts) ──► S3, S5   (reserve credit: S3 owner=Org · S5 owner=User)
S2 (Campaign + tiêu chí) ──► S3   (có campaign mới phát link)
S2 (tiêu chí) ──► S4              (chấm cần tiêu chí campaign)
S3 (session + answer) ──► S4      (có bài nộp mới chấm/xếp hạng)
S5 (B2C) ⟂ S2/S3/S4               (độc lập — B2C không đụng campaign; chỉ cần ví credit từ S1)
```

**Phase 0 — Foundation/Init (LÀM TRƯỚC mọi feature, tách riêng — Bài 06):**
Mục tiêu = baseline **đã xác minh + sẵn sàng bàn giao**, không viết feature B2B. Xong khi **4 điều kiện sẵn sàng** xanh:
- [ ] **Chạy được**: `docker compose up` lên đủ 6 service + hạ tầng từ máy sạch.
- [ ] **Test được**: mỗi service .NET có **≥1 test mẫu pass** (hiện **Campaign/Payment chưa có** test project) + chốt cách test AIService (Python).
- [ ] **Lệnh chuẩn hoá**: `make setup/dev/test/check` (hoặc script tương đương) — đừng để mỗi người tự đoán.
- [ ] **Bàn giao được**: `AGENTS.md` + `progress.md` + `decisions.md` trả lời "chạy/test/đang ở đâu/bước kế" không cần hỏi người.
- [ ] **Commit checkpoint sạch** làm mốc bắt đầu.

**Thứ tự khởi động (sau Phase 0):**
1. **S1 trước**: hoàn thiện luồng cấp role Employer (role đã có sẵn) → mở khóa mọi `[Authorize(Roles=...)]`; dựng `credit_accounts(owner_type)` (task `P1`).
2. **S2 song song**: hoàn thiện CampaignService + fix bug (chỉ cần role từ S1).
3. **S5 song song**: ngay sau `P1` — nối ví prepaid cá nhân + reserve/consume vào engine luyện (engine + lịch sử đã chạy). Độc lập với S2/S3/S4 → có thể là **luồng B2B-independent đầu tiên cho ra E2E demo được**.
4. **S3** khi `GET /campaign/{id}` ổn định + chốt được hợp đồng "tạo session gắn campaign" với InterviewService.
5. **S4** khi S3 sinh được answer thật; trước đó dựng trước phần nạp tiêu chí vào job chấm.

> Để không chặn nhau: **chốt API contract (§3) trước**, mock response khi service kia chưa xong. Mỗi task ở §8 nên có **tiêu chí chấp nhận** (definition of done) — xem dòng đầu §8.

---

## 6. Quy ước chung (bắt buộc)

Kế thừa nguyên tắc đang dùng — chi tiết [architecture.md](architecture.md) §5:

- **Branch:** `features/<service>` cho mảng lớn, PR vào `dev`.
- **Gateway:** API public đi qua `/api/v1/<service>/...` (StripPrefix). Callback nội bộ **không** qua gateway.
- **DB:** mỗi service 1 database riêng, EF Core, cột **snake_case**, enum lưu **string**, **không FK xuyên service**.
- **AI không ghi DB:** AIService chỉ trả kết quả qua **callback** về service .NET (`X-Internal-Token`). Service mới nhờ AI chấm → theo pattern publish RabbitMQ + callback này.
- **Auth:** JWT Bearer dùng **chung** `Jwt:Key`/`Issuer`/`Audience`.
- **File:** lưu SeaweedFS (S3) — **lưu *key/path* trong DB, không lưu full URL** (xem bug §7.1).
- **B2B/B2C phân biệt bằng `campaign_id`** trên session, không tách engine.

---

## 7. Việc cần dọn ngay ở CampaignService (S2)

> ✅ **ĐÃ FIX hết 6 bug dưới + merged main (PR #22)** — giữ lại để tham chiếu lịch sử review. Code: `src/services/Isas.CampaignService/Services/CampaignService.cs`.

**🔴 Phải fix:**
1. **Lưu full URL nhưng download/delete dùng làm S3 key** → tải/xóa file hỏng. Lưu *path* (`campaigns/{id}/jd.pdf`), chỉ ghép URL khi trả response.
2. **`GET /campaign` trả campaign của mọi employer** → rò rỉ dữ liệu. Phải lọc theo `EmployerId`.
3. **`UpdateCampaign` luôn ghi đè `AntiCheatEnabled`** (field `bool` không nullable) → đổi `bool?`.

**🟡 Nên fix:**
4. Download trả sai `application/zip` + tên `.zip` cho 1 file PDF; `FileNotFoundException` không được catch → 500 thay vì 404.
5. Thông báo lỗi nói "PDF and DOCX" nhưng chỉ nhận PDF.
6. **Bật lại `[Authorize]` / `[Authorize(Roles="Employer")]`** (đang comment hết) — chờ role từ S1.

---

## 8. Backlog theo module (epic → việc lớn)

> **WIP=1 + bằng chứng chạy được:** bóc mỗi epic thành **task nguyên tử** (1 hành vi/ task), mỗi task có **1 lệnh xác minh chạy được** (curl / `dotnet test`) + ghi **phụ thuộc**. Người/agent làm **1 task active** → verify pass → commit → mới sang task kế; **đừng** mở nhiều task cùng lúc hay tiện tay refactor việc khác (xem [AGENTS.md](../AGENTS.md) §Quy tắc làm việc). Dòng **Chấp nhận** mỗi epic = đích end-to-end của cả epic. **Task nguyên tử + lệnh xác minh + trạng thái** đã liệt kê ở [tasks.md](tasks.md) (tạm; về sau đẩy sang board Jira/GitHub Projects).

**M1 Payment — PaymentService (S1)** *(branch `payment-b2c`; chi tiết: [services/payment.md](services/payment.md))*:
- **Credit theo chủ ví** (`credit_accounts(owner_type, owner_id)`): B2B = Org, B2C = User cá nhân (xem [decisions.md](decisions.md) D15). Mô hình **reserve → consume (Scored) → release** (idempotent theo `sessionId`).
- **Prepaid**: pack `OneTime` qua PayOS. **Postpaid (trả sau)**: org được PlatformAdmin duyệt → dồn nợ tới `credit_limit` → **hóa đơn cuối kỳ** → tất toán PayOS. (Subscription = phase 2.)
- PayOS: `order` + **webhook verify chữ ký** + **FE active-polling** (`/order/{id}/status` → server đối soát *get-payment-info*). Idempotent theo `payos_order_code`.
- **order_code** time+random (không snowflake/auto-increment). API nội bộ `/internal/credits/reserve|consume|release` (`X-Internal-Token`).
- Đình chỉ khi hết hạn mức/quá hạn → chặn tương lai, **không văng người đang thi**.
- **Chấp nhận:** mua pack → webhook PayOS (sandbox) cộng credit org; reserve→consume trừ đúng 1 credit/session (test idempotent); hết hạn mức → reserve trả 402.

**Auth + Org (S1)**: thêm **Organization** (`org_id`, tax_code) + **org_members (OrgAdmin/HrMember)** vào JWT · luồng tạo/đăng ký tổ chức · bật lại `[Authorize(Roles)]`. *(Sub-account RBAC đầy đủ = phase 2.)*
- **Chấp nhận:** JWT chứa `org_id`+`org_role`; HrMember bị chặn endpoint billing; test phân quyền pass.

**M2 Campaign — CampaignService (S2)**: fix §7 · lifecycle Draft→Active→Closed→Archived · gọi AI gợi ý câu hỏi · **tiêu chí text→cấu trúc (AI+HR)** · **soft-delete + audit_logs** · publish.
- **Chấp nhận:** 6 bug §7 đóng (có test); publish sinh `campaign_criteria` có weight; DELETE chỉ set `deleted_at`; GET lọc theo org.

**M3 Distribution (S3)**: *Campaign*: lời mời **magic-link** + email; **"1 lần" = 1 lần nộp** (resume câu chưa làm); lỗi/hết hạn → **re-issue**. *Interview*: `campaign_id`, create-or-get session từ token, **danh tính ứng viên = account Candidate nhẹ**.
- **Chấp nhận:** mở token tạo session gắn `campaign_id`; mở lại token → đúng session cũ (resume); sau submit token khoá (403).

**M4 Evaluation & Ranking (S4)**: *AI/Interview*: chấm theo **tiêu chí campaign có cấu trúc**; phát **event `SessionScored`**. *Campaign*: consume event → **`campaign_rankings` read-model**, xếp hạng + pass/fail. **anti-cheat = flag cho HR** (không auto-hủy).
- **Chấp nhận:** session B2B `Scored` → `campaign_rankings` có điểm có trọng số; xếp hạng đúng thứ tự; consume event idempotent.

**M5 Result — CampaignService (S4)**: dashboard đọc local từ `campaign_rankings` · **xuất CSV/PDF** · lọc/sắp xếp.
- **Chấp nhận:** dashboard không gọi xuyên service; export CSV/PDF khớp ranking.

**B2C Personal Practice — PaymentService + InterviewService (S5)** *(chi tiết ví: [services/payment.md](services/payment.md) §B2C)*:
- **BC1 — ví prepaid cá nhân**: mua pack `OneTime` với `owner_type=User` (`owner_id` = `sub` JWT) → webhook PayOS cộng credit ví cá nhân. **Không** postpaid/hóa đơn (chỉ Org).
- **BC2 — reserve/consume khi luyện**: InterviewService `CreateSession` **reserve** 1 credit ví cá nhân trước khi gọi AI; hết → **402, không tạo session**. `SessionScored` → **consume**; bỏ ngang/lỗi → **release**. Idempotent theo `sessionId`.
- **BC3 — lịch sử cá nhân**: `GET /api/practice/sessions/history` + `GET /{id}` (đã có) — xác minh đọc đúng điểm/transcript của chính user.
- **BC4 — phân tích CV** *(feature mới, [decisions.md](decisions.md) D17; API: [services/ai.md](services/ai.md) `/analyze-cv` + [services/interview.md](services/interview.md) `cv-analysis`)*: (a) upload CV → AI trả **tóm tắt + điểm mạnh/yếu + gợi ý**; (b) kèm JD → thêm **% khớp + kỹ năng thiếu/đủ**; (c) sau buổi luyện, báo cáo có mục **"CV nói tốt nhưng trả lời chưa tới"**. Dùng AIService đồng bộ (1 call Gemini, **không** qua pipeline chấm); AIService không ghi DB, Interview lưu `cv_analyses`. **Miễn phí (không trừ credit) phase 1** — *team xác nhận có tính phí không*.
- **Chấp nhận:** user cá nhân nạp credit (sandbox PayOS) → ví +N; tạo session luyện reserve −1 (giữ chỗ), chấm xong consume đúng 1; hết credit → tạo session trả 402; `GET history` trả đúng buổi của mình, không thấy của người khác; **`POST cv-analysis` (chỉ cvId) → trả tóm tắt+mạnh/yếu+gợi ý; kèm jdId → có matchScore+skills; kết quả lưu `cv_analyses` đọc lại được**.

---

## 9. Dùng & cập nhật tài liệu này

- Đây là **nguồn chốt scope + phân việc**. Đổi phạm vi/owner → sửa ở đây.
- Service mới xong API/schema/nghiệp vụ → cập nhật **đúng `docs/services/<service>.md`** (cùng file). Quy ước/luồng chung → [architecture.md](architecture.md).
- Cột *Owner* (§4) và 3 hợp đồng service (§3) **team chốt buổi họp đầu sprint** rồi điền.
