# ISAS — Kiến trúc hệ thống

Sản phẩm phỏng vấn bằng AI — **2 dòng dùng chung 1 engine**: **(B2C)** luyện phỏng vấn cá nhân (tự tạo session từ CV/JD → AI sinh câu hỏi → ghi âm → chấm rubric → xem lịch sử) · **(B2B)** tuyển dụng (Employer tạo *chiến dịch đánh giá* từ JD → phát link → AI chấm theo tiêu chí → xếp hạng). Engine phỏng vấn dùng chung, phân biệt bằng `campaign_id` (null = B2C, có = B2B); **cả hai đều là deliverable**. Phạm vi & phân công: [work-division.md](work-division.md); lý do quyết định: [decisions.md](decisions.md).

## 1. Tổng quan

Kiến trúc **microservices** theo mô hình **Engine + Orchestrator** — **không** tách mỗi module một service. Một **API Gateway** (YARP) đứng trước; **6 service** (.NET + 1 AI Python). InterviewService là **engine phỏng vấn dùng chung** (B2C trước, B2B xây thêm lên trên); CampaignService điều phối B2B; PaymentService lo thanh toán.

```
                        ┌──────────────────────────┐
       Frontend ──────► │  Gateway (YARP)  /api/v1 │
       (Employer/       └─┬────┬────┬────┬────┬─────┘
        Candidate)        │    │    │    │    │
        /auth ────────────┘    │    │    │    └──── /ai ──► AIService (Python)
        /payment ──► PaymentSvc │    │                       ▲
        (credit/PayOS)          │    └── /campaign ──► CampaignService (B2B orchestrator)
                  /interview ───┘            │  campaign, tiêu chí,   │ sinh câu hỏi từ JD
                        │                    │  distribution, ranking └────────────┘
              ┌─────────▼─────────┐   tạo session (campaign_id)
              │  InterviewService │ ◄───────────────────────┘
              │  ENGINE phỏng vấn │ ── SessionScored (event) ─► Campaign (ranking) + Payment (consume)
              │  (B2B & B2C)      │
              └─────────┬─────────┘
        Postgres · SeaweedFS(S3) · RabbitMQ · Redis
```

> **Vì sao Engine + Orchestrator:** máy chấm điểm (Whisper+Gemini+RabbitMQ+state machine+republisher) đã chạy ổn ở InterviewService — chép lại cho B2B là rủi ro thời gian lớn nhất. **B2C = mode `campaign_id = null`; B2B = `campaign_id` có giá trị.** Chi tiết: [decisions.md](decisions.md) D1.
> **Hướng tiến hóa — đọc B2C trước, B2B sau:** **B2C (luyện phỏng vấn cá nhân) là nền** — engine sinh câu hỏi + chấm + lịch sử đã chạy. **B2B = xây thêm lớp điều phối (CampaignService) lên trên CÙNG engine đó**, không build lại. Vì vậy §4 mô tả luồng **B2C end-to-end (§4.1) trước**, rồi **B2B (§4.2) = B2C + orchestration**; engine chấm (§4.3) và thanh toán (§4.4) dùng chung.

## 2. Thành phần (6 service + hạ tầng)

| Service | Công nghệ | Vai trò | Trạng thái |
|---|---|---|---|
| **Gateway** | .NET, YARP | Reverse proxy `/api/v1/*` → service; gộp OpenAPI thành 1 doc | ✅ |
| **AuthService** | .NET, JWT, Google OAuth | Đăng nhập, JWT/refresh, profile; 3 role + **Organization (OrgAdmin/HrMember)** | ✅ (thêm Org) |
| **InterviewService** | .NET, EF Core | **Engine dùng chung**: session (`campaign_id?`), câu hỏi, câu trả lời, điểm, rubric/tiêu chí, file | ✅ (mở rộng B2B) |
| **AIService** | Python, FastAPI, faster-whisper, google-genai | Sinh câu hỏi + worker chấm điểm (rubric JobCategory **hoặc** tiêu chí campaign) | ✅ (mở rộng) |
| **CampaignService** | .NET, EF Core | Điều phối B2B: campaign + tiêu chí, distribution, ranking, result/export | 🟢 merged main (M2: CRUD + tiêu chí cấu trúc + publish/audit); M3/M4/M5 🟡 |
| **PaymentService** | .NET, EF Core | Thanh toán PayOS, **credit theo chủ ví** (org B2B / cá nhân B2C — D15), prepaid + postpaid, reserve→consume | ✅ trong tree (CI image, gateway route live, trong compose) |

**Hạ tầng:** PostgreSQL 18 — DB-per-service (`isas`/`isas_interview`/`isas_campaign`/`isas_payment`) · SeaweedFS (S3, cổng 8333; CV/JD/Criteria/audio) · RabbitMQ (job chấm `scoring_pipeline_queue` + event) · Redis (provision sẵn cho cache; **lưu ý: refresh token của Auth hiện ở Postgres** — Redis chưa được wire, để dành phase sau).

### 2.1. Tổng hợp "chưa làm" — gap toàn hệ thống
> **Bức tranh tổng** ở cấp hệ thống. **Tracking chi tiết** (đầu việc + lệnh xác minh + owner) là source of truth ở [work-division.md](work-division.md) §2/§8 + [tasks.md](tasks.md) + [progress.md](progress.md) — bảng này chỉ **trỏ tới**, không thay thế.

| Mảng | ✅ Đã có | ❌/🟡 Chưa làm | Tracking |
|---|---|---|---|
| **B2C (nền)** | Engine sinh câu hỏi **bám CV/JD** + chấm rubric `JobCategory` + lịch sử | Ví **credit cá nhân** + reserve/consume khi luyện; **phân tích CV (BC4)**; tổng kết điểm/nhận xét (BC9–BC11); **roadmap ôn tập (BC12–BC15, D20)** | tasks `BC1`–`BC15`; D15/D17/D20 |
| **B2B (điều phối)** | tiêu chí text→**cấu trúc** (C8) + soft-delete/audit (C9/C10) + **I1** session `campaign_id`/materialize + **E1** chấm theo tiêu chí campaign | **Distribution** (M3 magic-link/email), **Ranking+Result** (M4/M5 + CSV/PDF), wire `org_id`, **lọc CV hàng loạt** (C13–C15, D18/D19) | work-division §1b; tasks `C*`/`D*`/`E*` |
| **AuthService** | 3 role, JWT, Google OAuth, **Organization + org-role** (OrgAdmin/HrMember) trong JWT + `register-org` (A1–A3) | `A4` HrMember chặn billing, `A5` bật lại `[Authorize(Roles)]` mọi service | tasks `A1`–`A5` |
| **PaymentService** ✅ | `credit_accounts(owner_type)` + **reserve/consume/release** (P4/P5/P6) + mua pack/webhook (P2) + active-polling (P3) + **postpaid + hóa đơn** (P8) — in tree, CI image, gateway route, compose | verify tay: PayOS sandbox (webhook HMAC) | tasks `P1`–`P8` ✅; [services/payment.md](services/payment.md) |
| **CampaignService** 🟢 | merged main: CRUD + JD/Criteria (PdfPig) + 6 bug fix + lifecycle + publish tiêu chí cấu trúc + soft-delete/audit | distribution, ranking/result/export, wire `org_id` | tasks `C1`–`C10` |
| **AIService** | generate-questions, transcribe, worker chấm, **suggest-criteria** (C8) | **analyze-cv (BC4)**; ✅ đã bỏ `/ai/**` khỏi gateway (GEN-7) · còn `X-Internal-Token` nội bộ · Whisper nhẹ/GPU · chống prompt-injection · DLQ | [services/ai.md](services/ai.md) §Vấn đề |
| **Gateway / Infra** | Reverse proxy, compose service | ✅ `/ai/**` đã gỡ khỏi gateway (GEN-7, internal-only); **Redis chưa wire** | §6, §8 |
| **Nền tảng (Phase 0)** | test project Campaign/Auth/Interview ✅ (`P0.3`) | `docker compose up` máy sạch (verify), `make setup/test/check`, **test project Payment**, readiness 4 điều kiện | work-division §5; tasks `P0.1`–`P0.5` |
| **CI/CD** | Build+push Auth/Interview/**Campaign**/Gateway → server qua Tailscale | Thêm **Payment** vào pipeline; AIService deploy **tay trên Mac** | §8; [../DEPLOYMENT.md](../DEPLOYMENT.md) |

## 3. Giao tiếp giữa service

| Từ | Đến | Kiểu | Mục đích |
|---|---|---|---|
| Frontend | Gateway | HTTPS | Mọi request public, prefix `/api/v1` |
| Interview / **Campaign** | AIService | HTTP (`AiService:BaseUrl`) | Sinh câu hỏi (đồng bộ); Campaign còn **đề xuất tiêu chí có cấu trúc** từ JD |
| InterviewService | RabbitMQ | AMQP publish | Đẩy job chấm điểm |
| AIService worker | RabbitMQ / SeaweedFS | AMQP consume / S3 | Nhận job; tải audio transcribe |
| AIService worker | InterviewService | HTTP callback (`/internal/...`, `X-Internal-Token`) | Trả transcript + điểm |
| CampaignService | InterviewService | HTTP | create-or-get session gắn `campaignId` (kèm câu hỏi + **tiêu chí có cấu trúc**) |
| InterviewService | Campaign + Payment | **RabbitMQ event** | `SessionScored` → Campaign cập nhật **ranking read-model**, Payment **consume credit** |
| Campaign | PaymentService | HTTP nội bộ (`X-Internal-Token`) | **reserve** credit của org khi ứng viên bắt đầu; `release` khi bỏ ngang |
| FE/Employer | PaymentService | HTTP | Mua pack (prepaid) / tất toán hóa đơn (postpaid); **webhook PayOS + active-polling** |

> **AI không ghi DB:** AIService trả kết quả qua callback về .NET — .NET là **chủ DB duy nhất**.
> **Auth offline:** các service **validate JWT bằng chung `Jwt:Key/Issuer/Audience`, KHÔNG call Auth lúc chạy** (chỉ client gọi `login/register/refresh`; gọi Auth chỉ khi cần *dữ liệu tươi* ngoài token, vd email xuất hóa đơn).
> **3 hợp đồng team chốt trước khi code** (Campaign↔Interview tạo session + trả điểm; Interview/Campaign→Payment credit; Campaign→AI tiêu chí): [work-division.md](work-division.md) §3. Ref user/campaign giữa service = Guid **lỏng**.

## 4. Luồng chính

> **Đọc theo hướng tiến hóa: định danh (§4.0) → B2C nền (§4.1) → B2B thêm orchestration (§4.2).** Engine chấm (§4.3) + thanh toán (§4.4) dùng chung cả hai dòng.

### 4.0. Định danh & onboarding (trước cả hai dòng sản phẩm)
> Mọi luồng dưới giả định user **đã định danh**. Đây là bước nền: ai là ai, role gì, thuộc org nào.
- **B2C:** `POST /auth/register` → role **`Candidate`** (mặc định) → `login`/`refresh` → luyện tập ngay. Không cần org.
- **B2B:** đăng ký/nâng cấp **tổ chức** → user thành **`Employer`** + tạo **Organization** (`org_id`, MST) → người tạo = **`OrgAdmin`**; *(phase 2: mời `HrMember`)*. JWT mang kèm **`org_id` + `org_role`**. **⚠ Gap:** luồng cấp role Employer / register-org **chưa build** (tasks `A1`–`A5`).
- **PlatformAdmin (`Admin`):** **không** phải service riêng — endpoint admin-gated nằm trong Auth (cấp role, duyệt org/verify MST) + Payment (duyệt/đình chỉ **postpaid**, quản gói/đơn giá).
- Mọi service **validate JWT offline** (§3) — **không** gọi Auth lúc chạy. Chi tiết: [services/auth.md](services/auth.md).

### 4.1. Luồng B2C end-to-end (luyện phỏng vấn cá nhân) — NỀN
1. User đăng ký/đăng nhập **cá nhân** (role `Candidate`, **không** org).
2. Mua **credit prepaid** ví cá nhân qua PayOS (`owner_type=User` — §4.4).
3. Tự tạo session: upload **CV/JD** (optional) → `POST /interview/practice/sessions {cvId?, jdId?, jobCategory}` → InterviewService **reserve 1 credit ví cá nhân** (hết → **402, không tạo session**) → **AIService sinh câu hỏi bám CV/JD** (ưu tiên `JD > CV > JobCategory`).
4. Ghi âm trả lời từng câu → **chấm dần §4.3** theo **rubric `JobCategory`** → session `Scored` → **consume credit** (bỏ ngang/lỗi → release).
5. Xem **lịch sử** của mình (`GET /interview/practice/sessions/history`); **(BC4)** phân tích CV: feedback CV + khớp CV–JD + mục "CV vs câu trả lời".
6. **(BC5 🔜)** **Roadmap ôn tập cá nhân hoá** (D20): chọn BA/FE/BE + **level** → từ **report các buổi đã chấm** (điểm yếu) + CV → AI sinh **milestone/lesson** — mỗi lesson: *lý thuyết trước* (AI sinh bám điểm yếu, free) → *luyện session* (tiêu credit như bước 3–4) → xong mỗi mile xem **độ cải thiện** → hoàn thành → **report** (radar + đánh giá theo level + kết luận). Chi tiết: [services/interview.md](services/interview.md) §Roadmap ôn tập.

> **Hiện trạng B2C:** engine sinh câu hỏi (bám CV/JD) + chấm + lịch sử **đã chạy**; **còn thiếu**: wiring ví credit cá nhân (reserve/consume, bước 2–4) + BC4 phân tích CV + BC5 roadmap ôn tập. Xem [work-division.md](work-division.md) §1a, [decisions.md](decisions.md) D15/D17/D20.

### 4.2. Luồng B2B end-to-end (tuyển dụng) — XÂY TRÊN ENGINE B2C
> B2B = **đúng engine + cách chấm của B2C**, thêm **CampaignService điều phối**. Khác B2C ở 4 điểm: `campaign_id` **có giá trị** · chủ ví credit = **Org** (CampaignService reserve, không phải InterviewService) · dùng **tiêu chí campaign có cấu trúc** thay rubric `JobCategory` · thêm **distribution (magic-link/email) + ranking/export**.
>
> **2 phương thức lọc ứng viên (app B2B):** **(1) qua CV** — *tùy chọn*, **MIỄN PHÍ** (D18/D19): HR đổ loạt CV → hard-filter (rule cứng) + AI match-score theo `campaign_criteria` → **shortlist xếp hạng** → mời top N. **(2) qua phỏng vấn AI** — bước A–C dưới, **tính credit**. Hai cách **nối nhau** (sàng CV → mời → phỏng vấn → ranking cuối) hoặc **dùng độc lập** (chỉ sàng CV; hoặc mời thẳng không sàng). Sàng CV **không** chạm engine phỏng vấn / **không** tiêu credit — chi tiết [services/campaign.md](services/campaign.md) §Lọc ứng viên qua CV.

**A. Tổ chức tạo & phát chiến dịch**
1. Org có **credit** — prepaid (mua pack) hoặc postpaid (được duyệt trả sau) — xem §4.4.
2. HR tạo campaign từ JD (`POST /api/v1/campaign`) → upload JD/Criteria → bấm gợi ý → **AIService sinh câu hỏi (đồng bộ) + đề xuất tiêu chí**. **Publish `Active`** → tiêu chí **có cấu trúc** (name/weight/max_score), **HR duyệt**.
3. Distribution: phát **magic-link theo 2 đường** — **(a) mời thẳng**: HR upload **danh sách email** → validate/dedup/cap → gửi hàng loạt; **(b) từ shortlist sàng CV**: HR chọn top → hệ thống **tách email từ CV** → gửi (CV thiếu email → HR bổ sung rồi mời).

**B. Ứng viên làm bài (tái dùng engine)**
4. **✅ D2 (membership model):** Mở link → xem **intro campaign** (metadata) → **Join** (magic-link **provision/login `Candidate`** — `candidate_id` + JWT; đường (b): account mới **tự gắn** vào hồ sơ CV — `campaign_candidates.candidate_id`) → campaign vào **My Campaigns** → **Start Interview** → CampaignService **create-or-get** session gắn `campaign_id`. **Session CHỈ tạo khi bấm Start**, không phải khi mở link. *(🔜 reserve 1 credit org tại Start — BK14.)*
5. Trả lời (ghi âm) → **chấm dần §4.3** theo **tiêu chí campaign**. Khóa link sau **submit** (resume các câu chưa nộp).

**C. Đánh giá & kết quả (event-driven)**
6. Session `Scored` → InterviewService tính **điểm có trọng số** (`Σ điểm×weight`) + phát **`SessionScored`** (RabbitMQ).
7. **Payment** consume reservation (trừ thật); **Campaign** upsert `campaign_rankings` → xếp hạng + pass/fail. (Bỏ ngang quá hạn → `SessionAbandoned` → release credit.)
8. Employer xem dashboard (đọc local) → **xuất CSV/PDF**.

> **Vòng đời magic-link (Distribution M3) — ✅ D2 membership model:** HR phát **invitation → magic-link** (email hàng loạt) → ứng viên mở link (**intro-only**) → **Join** → **provision account `Candidate` nhẹ** (D8) + membership `CampaignCandidate(Joined)` → **My Campaigns** → **Start** → create-or-get session gắn `campaign_id`. **Session tạo khi Start, KHÔNG khi mở link.** **"1 lần" = 1 lần NỘP** (mở lại → **resume** câu chưa làm — 🔜 D3); sau **submit** → token **khóa** (🔜 D4); lỗi/hết hạn → HR **re-issue** (🔜 D4). Tracking: `D1`✅·`D2`✅·`D3`/`D4`🔜; [services/campaign.md](services/campaign.md) §Distribution.

### 4.3. Chấm điểm dần (engine, bất đồng bộ — dùng chung B2C & B2B)
1. FE upload từng câu trả lời → audio lên SeaweedFS → answer `Uploaded`.
2. InterviewService publish job (kèm **rubric JobCategory** [B2C] hoặc **tiêu chí campaign** [B2B]) lên RabbitMQ → answer `Scoring`.
3. Worker consume → tải audio → Whisper transcribe → Gemini chấm.
4. Worker callback `/internal/answers/{id}/result` → lưu điểm → answer `Scored`. Lỗi vĩnh viễn → `/failed` → `Failed`.
5. `POST .../submit` chốt session; mọi answer xong → session `Scored` → phát `SessionScored`.

> Phục hồi: `StuckAnswerRepublisher` (background) đẩy lại job kẹt. State machine + retry + chi tiết chấm theo tiêu chí campaign: [services/interview.md](services/interview.md).

### 4.4. Thanh toán PayOS — credit theo chủ ví (cá nhân B2C / Org B2B), prepaid + postpaid
**Prepaid (trả trước — cả B2C & B2B):** `POST /payment/order` → link PayOS → trả → **webhook (verify) cộng credit**. FE **active-polling** `GET /order/{id}/status`: server chưa nhận webhook → **gọi PayOS đối soát ngay** (cứu webhook delay/drop). Idempotent theo `payos_order_code`.
**Postpaid (trả sau — CHỈ Org B2B):** Org được **PlatformAdmin duyệt** → dồn nợ tới `credit_limit` → **hóa đơn cuối kỳ** (`interview_count × unit_price`) → `POST /invoices/{id}/pay` tất toán PayOS. *(Ví cá nhân B2C luôn prepaid, không postpaid/hóa đơn.)*
**Tiêu credit — reserve→consume:**
```
ứng viên/người luyện bắt đầu ─► reserve {owner, sessionId}
   (B2C owner=User · InterviewService gọi  |  B2B owner=Org · CampaignService gọi;
    prepaid: remaining≥1 · postpaid CHỈ Org: nợ+giữ < credit_limit; hết → 402)
session Scored ──(SessionScored)──► CONSUME (trừ thật)
bỏ ngang/lỗi ───(SessionAbandoned)─► RELEASE (nhả chỗ)
```
> `order_code` time+random (không snowflake/auto-increment). Đình chỉ khi hết hạn mức/quá hạn → chặn hành động tương lai, **không văng người đang thi**. **Credit ≠ token LLM** (bán theo "lượt", token chỉ là giá vốn nội bộ). Chi tiết: [services/payment.md](services/payment.md).

## 5. Quy ước chung (áp cho mọi service)

- **Gateway:** API public đi qua `/api/v1/<service>/...` (YARP StripPrefix). **Callback nội bộ (`/internal/...`) và webhook PayOS KHÔNG qua gateway** — gọi thẳng service.
- **DB-per-service:** mỗi service 1 database riêng, EF Core, cột **snake_case** (`UseSnakeCaseNamingConvention`), enum lưu **string** (`HasConversion<string>`). `__EFMigrationsHistory` riêng.
- **Tham chiếu lỏng:** ref giữa service là Guid (`candidate_id`/`user_id`/`employer_id`/`org_id`/`campaign_id`/`session_id`), **không FK xuyên service**.
- **Auth offline:** mọi service **validate JWT** bằng chung `Jwt:Key`/`Issuer`/`Audience` — **không call AuthService lúc chạy**.
- **AI không ghi DB:** AIService chỉ trả kết quả qua **callback** về service .NET (`X-Internal-Token`). Service mới nhờ AI chấm → theo pattern publish RabbitMQ + callback.
- **File:** lưu SeaweedFS (S3) — **lưu *key/path* trong DB, ghép URL khi đọc** (không lưu full URL).
- **Branch:** `features/<service>` cho mảng lớn, PR vào `dev`.

### 5.1. Biến ràng buộc thành check tự động (executable)
Ràng buộc "trên giấy" agent/người sẽ lách → mỗi cái nên có **check chạy được** (grep/lint/architecture test trong CI), kèm **báo lỗi cách sửa**. Ưu tiên:

| Ràng buộc | Check gợi ý |
|---|---|
| File lưu **key** không full URL | test: upload → DB lưu `campaigns/{id}/jd.pdf` (không phải `http://…`) — **đúng bug Campaign #1** |
| **AIService không ghi DB** | AIService không có model/migration ghi DB; mọi thay đổi qua callback |
| `/internal` + **webhook không qua gateway** | grep appsettings Gateway: route **không** chứa `/internal` hay `/webhook` |
| **Controller không gọi thẳng `DbContext`** | architecture test (vd NetArchTest): Controllers chỉ phụ thuộc Services |
| **snake_case + enum string** | test assert `UseSnakeCaseNamingConvention` bật (đặc biệt **CampaignService** — đang nghi chưa bật) |
| Không **FK xuyên service** | architecture test/review: DbContext không ref entity của service khác |

> **Thăng cấp review:** mỗi loại lỗi lặp trong PR review → thêm 1 check → harness mạnh dần. **E2E bắt buộc khi thay đổi XUYÊN service** (Campaign↔Interview↔Payment, event RabbitMQ). Mock 1 phía pass ≠ luồng thật chạy.

## 6. Routing gateway & mã lỗi

| Gateway path | Forward tới | Trạng thái |
|---|---|---|
| `/api/v1/auth/**` | AuthService `/auth/**` | ✅ |
| `/api/v1/interview/practice/**`, `/files/**` | InterviewService `/api/practice/**`, `/api/files/**` | ✅ |
| `/api/v1/campaign/**` | CampaignService `/campaign/**` | ✅ merged main (gateway route + CI build) |
| `/api/v1/payment/**` | PaymentService `/order`,`/package`,… | ✅ (gateway payment-cluster + CI build) |
| ~~`/api/v1/ai/**`~~ | — | ✅ **đã gỡ khỏi gateway (GEN-7, 2026-07-13)** — AI internal-only, gọi qua `AiService:BaseUrl` |

> ✅ **GEN-7 (2026-07-13):** `/api/v1/ai/**` **đã gỡ khỏi gateway** (route + cluster + OpenAPI aggregation) — đóng lỗ public + KHÔNG auth. AIService chỉ còn gọi **nội bộ** qua `AiService:BaseUrl` (Tailscale). Follow-up: siết thêm `X-Internal-Token` trên endpoint AIService. Xem [services/ai.md](services/ai.md) §Vấn đề đã biết.

**Mã lỗi chung:** `200/201/204` OK · `400` sai input · `401` thiếu/sai token · `403` không có quyền · `404` không thấy · `409` xung đột trạng thái · `500` lỗi hệ thống · `502` lỗi gọi AIService.

### 6.1. Event-bus convention (RabbitMQ) — chốt BK9

**Job chấm (point-to-point):** InterviewService → queue **`scoring_pipeline_queue`** (durable) → AIService worker consume (1 job/answer, hoặc N job khi self-consistency E10 — mỗi job mang `attemptNo`+`temperature`). Callback kết quả về InterviewService qua **HTTP** (`X-Internal-Token`), KHÔNG qua bus.

**Event buổi (pub/sub, fan-out):** InterviewService publish lên exchange **`interview.events`** (type **topic**, durable), **best-effort** (lỗi publish KHÔNG phá state `Scored`/`Abandoned` đã commit; có endpoint HTTP backfill làm fallback):

| Routing key | Phát khi | Payload | Consumer (queue durable riêng) |
|---|---|---|---|
| `session.scored` | session `Scored` (E2) | `{sessionId, campaignId?, candidateId, totalScore, scoredAt}` | Campaign **`campaign.ranking`** (E4 → upsert ranking) · Payment **`payment.credit`** (E7 → consume credit) |
| `session.abandoned` | session bỏ ngang/quá hạn/0-answer/Failed (E3, BK12) | `{sessionId, campaignId?, candidateId, reason, abandonedAt}` | Payment **`payment.credit`** (E7 → release credit) |

> Mỗi consumer bind **queue durable riêng** vào `interview.events` → 1 event tới nhiều consumer độc lập. `campaignId=null` = B2C → Campaign no-op. Consumer **idempotent/absorbing** (redeliver / out-of-order an toàn). Tên do E2/E3/E4/E7 đặt, nay **chốt** vào doc (khớp code).

> **Chi tiết theo service** (API + DB + rules): [services/auth.md](services/auth.md) · [services/interview.md](services/interview.md) · [services/campaign.md](services/campaign.md) · [services/payment.md](services/payment.md) · [services/ai.md](services/ai.md).

## 7. Chạy, kiểm thử & phạm vi demo

**Chạy (full stack):** `docker compose -f deploy/compose.yaml up -d` (Postgres/Redis/SeaweedFS/RabbitMQ + 5 service — cần `.env` + `seaweed-s3.json` cạnh file, xem [../DEPLOYMENT.md](../DEPLOYMENT.md) §4). Root `../compose.yaml` là **dev-partial** (Auth+Gateway+MinIO). AIService (Python) chạy riêng trên Mac. Env cần: connection string mỗi DB, `Jwt:Key/Issuer/Audience` (giống nhau mọi service), `Internal:Token`, `AiService:BaseUrl`, SeaweedFS keys, PayOS keys.
**Kiểm thử:** `dotnet test` (4 test project: `Isas.AuthService.Tests`, `Isas.InterviewService.Tests`, `Isas.CampaignService.Tests`, `Isas.PaymentService.Tests` ✅ P0.4) + AIService `pytest`. Cửa vào agent/người mới: [AGENTS.md](../AGENTS.md).

### Definition of Demo (chống "doc đẹp hơn sản phẩm")
Hai đường đi **sẽ trình hội đồng** — theo hướng tiến hóa B2C → B2B:

**Demo B2C (nền):**
1. User đăng ký cá nhân → mua **credit prepaid** ví cá nhân qua PayOS → webhook cộng credit.
2. Upload CV/JD → tạo session luyện → **reserve credit cá nhân**; AI sinh câu hỏi **bám CV/JD**.
3. Ghi âm trả lời → chấm rubric `JobCategory` → `Scored` → **consume credit** → xem **lịch sử**.
4. *(BC4)* Phân tích CV: feedback + khớp CV–JD.
5. *(BC5 🔜)* Roadmap ôn tập: từ điểm yếu → milestone/lesson (lý thuyết + luyện) → improvement → report radar/level.

**Demo B2B (xây trên B2C):**
1. Org mua credit qua **PayOS** (OneTime) → webhook cộng credit.
2. Tạo campaign từ JD → AI gợi ý câu hỏi + **tiêu chí có cấu trúc** → HR duyệt → publish.
3. Phát **magic-link** → ứng viên (account Candidate) làm bài.
4. Ứng viên bắt đầu → **reserve credit org**; AI chấm theo tiêu chí campaign → `Scored` → **consume**.
5. **Xếp hạng** + pass/fail → xuất CSV/PDF.

> Phần nào **chưa kịp build** ghi rõ trong doc service tương ứng (🟡/❌), **không** mô tả như đã chạy.

## 8. Hạ tầng & Deploy

- **CI/CD** ([.github/workflows/ci.yml](../.github/workflows/ci.yml)): push `main`/`dev`/`features/**` → build+test (dotnet) → build & push image lên **GHCR** → **SSH qua Tailscale vào server** → `docker compose pull && up -d`.
  - ✅ Hiện build **5 image** (Auth/Interview/Campaign/**Payment**/Gateway — ci.yml). **AIService** deploy **riêng trên Mac** (Whisper nặng), **không** qua ci.yml.
- Routing & prefix gateway: `appsettings.json` của Gateway (`/api/v1/{service}` + StripPrefix).
- Triển khai 2 host (server + Mac cho AIService): [DEPLOYMENT.md](../DEPLOYMENT.md).
