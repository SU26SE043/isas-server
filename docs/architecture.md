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
| **CampaignService** | .NET, EF Core | Điều phối B2B: campaign + tiêu chí, distribution, ranking, result/export | 🟡 branch |
| **PaymentService** | .NET, EF Core | Thanh toán PayOS, **credit theo chủ ví** (org B2B / cá nhân B2C — D15), prepaid + postpaid, reserve→consume | 🟡 branch |

**Hạ tầng:** PostgreSQL 18 — DB-per-service (`isas`/`isas_interview`/`isas_campaign`/`isas_payment`) · SeaweedFS (S3, cổng 8333; CV/JD/Criteria/audio) · RabbitMQ (job chấm `scoring_pipeline_queue` + event) · Redis (provision sẵn cho cache; **lưu ý: refresh token của Auth hiện ở Postgres** — Redis chưa được wire, để dành phase sau).

### 2.1. Tổng hợp "chưa làm" — gap toàn hệ thống
> **Bức tranh tổng** ở cấp hệ thống. **Tracking chi tiết** (đầu việc + lệnh xác minh + owner) là source of truth ở [work-division.md](work-division.md) §2/§8 + [tasks.md](tasks.md) + [progress.md](progress.md) — bảng này chỉ **trỏ tới**, không thay thế.

| Mảng | ✅ Đã có | ❌/🟡 Chưa làm | Tracking |
|---|---|---|---|
| **B2C (nền)** | Engine sinh câu hỏi **bám CV/JD** + chấm rubric `JobCategory` + lịch sử | Ví **credit cá nhân** + reserve/consume khi luyện; **phân tích CV (BC4)** | tasks `BC1`–`BC8`; D15/D17 |
| **B2B (điều phối)** | — (tái dùng engine B2C) | **Distribution** (M3 magic-link/email), **Ranking+Result** (M4/M5 + CSV/PDF), tiêu chí text→**cấu trúc**, soft-delete/audit | work-division §1b; tasks `C*`/`D*`/`E*` |
| **AuthService** | 3 role, JWT, Google OAuth | **Organization + org-role** (OrgAdmin/HrMember) trong JWT; bật lại `[Authorize(Roles)]` | tasks `A1`–`A5` |
| **PaymentService** 🟡 | Order/Package/PayOS (theo `user_id`) | `credit_accounts(owner_type)`, **reserve/consume/release**, **postpaid + hóa đơn**, active-polling | tasks `P1`–`P8`; [services/payment.md](services/payment.md) |
| **CampaignService** 🟡 | CRUD campaign + JD/Criteria (PdfPig) | **6 bug** (§7 work-division), lifecycle, publish tiêu chí cấu trúc, distribution, ranking | tasks `C1`–`C10` |
| **AIService** | generate-questions, transcribe, worker chấm | **analyze-cv (BC4)**; 🔴 bỏ `/ai/**` khỏi gateway + auth nội bộ · Whisper nhẹ/GPU · chống prompt-injection · DLQ | [services/ai.md](services/ai.md) §Vấn đề |
| **Gateway / Infra** | Reverse proxy, compose 6 service | `/api/v1/ai/**` **đang public, không auth** → chuyển nội bộ-only; **Redis chưa wire** | §6, §8 |
| **Nền tảng (Phase 0)** | — | `docker compose up` máy sạch (verify), `make setup/test/check`, **test project Campaign/Payment**, readiness 4 điều kiện | work-division §5; tasks `P0.1`–`P0.5` |
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

> **Hiện trạng B2C:** engine sinh câu hỏi (bám CV/JD) + chấm + lịch sử **đã chạy**; **còn thiếu**: wiring ví credit cá nhân (reserve/consume, bước 2–4) + BC4 phân tích CV. Xem [work-division.md](work-division.md) §1a, [decisions.md](decisions.md) D15/D17.

### 4.2. Luồng B2B end-to-end (tuyển dụng) — XÂY TRÊN ENGINE B2C
> B2B = **đúng engine + cách chấm của B2C**, thêm **CampaignService điều phối**. Khác B2C ở 4 điểm: `campaign_id` **có giá trị** · chủ ví credit = **Org** (CampaignService reserve, không phải InterviewService) · dùng **tiêu chí campaign có cấu trúc** thay rubric `JobCategory` · thêm **distribution (magic-link/email) + ranking/export**.

**A. Tổ chức tạo & phát chiến dịch**
1. Org có **credit** — prepaid (mua pack) hoặc postpaid (được duyệt trả sau) — xem §4.4.
2. HR tạo campaign từ JD (`POST /api/v1/campaign`) → upload JD/Criteria → bấm gợi ý → **AIService sinh câu hỏi (đồng bộ) + đề xuất tiêu chí**. **Publish `Active`** → tiêu chí **có cấu trúc** (name/weight/max_score), **HR duyệt**.
3. Distribution: phát **lời mời (magic-link)** + **email hàng loạt**.

**B. Ứng viên làm bài (tái dùng engine)**
4. Mở link → magic-link **provision/login `Candidate`** (có `candidate_id` + JWT) → CampaignService **create-or-get** session gắn `campaign_id` + **reserve 1 credit của org** (hết hạn mức → chặn ngay; mở link không tốn tiền).
5. Trả lời (ghi âm) → **chấm dần §4.3** theo **tiêu chí campaign**. Khóa link sau **submit** (resume các câu chưa nộp).

**C. Đánh giá & kết quả (event-driven)**
6. Session `Scored` → InterviewService tính **điểm có trọng số** (`Σ điểm×weight`) + phát **`SessionScored`** (RabbitMQ).
7. **Payment** consume reservation (trừ thật); **Campaign** upsert `campaign_rankings` → xếp hạng + pass/fail. (Bỏ ngang quá hạn → `SessionAbandoned` → release credit.)
8. Employer xem dashboard (đọc local) → **xuất CSV/PDF**.

> **Vòng đời magic-link (Distribution M3) — chi tiết bước 3–5:** HR phát **invitation → magic-link** (email hàng loạt) → ứng viên mở link → **provision/login account `Candidate` nhẹ** (D8) → create-or-get session gắn `campaign_id`. **"1 lần" = 1 lần NỘP** (mở lại token cho **resume** câu chưa làm); sau **submit** → token **khóa** (403); lỗi/hết hạn → HR **re-issue** token mới (cũ vô hiệu). Tracking: tasks `D1`–`D4`; [services/interview.md](services/interview.md).

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
| `/api/v1/ai/**` | AIService `/api/v1/**` | ✅ |
| `/api/v1/interview/practice/**`, `/files/**` | InterviewService `/api/practice/**`, `/api/files/**` | ✅ |
| `/api/v1/campaign/**` | CampaignService `/campaign/**` | 🟡 branch (gateway đã route + CI build) |
| `/api/v1/payment/**` | PaymentService `/order`,`/package`,… | 🟡 branch |

> ⚠ **`/api/v1/ai/**` đang public + KHÔNG auth** — endpoint AI đắt (CPU/tiền) → cần chuyển **nội bộ-only** (chỉ gọi qua `AiService:BaseUrl`, không expose gateway) **+** `X-Internal-Token`. Xem [services/ai.md](services/ai.md) §Vấn đề đã biết.

**Mã lỗi chung:** `200/201/204` OK · `400` sai input · `401` thiếu/sai token · `403` không có quyền · `404` không thấy · `409` xung đột trạng thái · `500` lỗi hệ thống · `502` lỗi gọi AIService.

> **Chi tiết theo service** (API + DB + rules): [services/auth.md](services/auth.md) · [services/interview.md](services/interview.md) · [services/campaign.md](services/campaign.md) · [services/payment.md](services/payment.md) · [services/ai.md](services/ai.md).

## 7. Chạy, kiểm thử & phạm vi demo

**Chạy (local):** `docker compose up` (xem `../compose.yaml` — Postgres/Redis/SeaweedFS/RabbitMQ + service). AIService (Python) chạy riêng (xem [../DEPLOYMENT.md](../DEPLOYMENT.md)). Env cần: connection string mỗi DB, `Jwt:Key/Issuer/Audience` (giống nhau mọi service), `Internal:Token`, `AiService:BaseUrl`, SeaweedFS keys, PayOS keys.
**Kiểm thử:** `dotnet test` (hiện chỉ có `Isas.InterviewService.Tests`). Cửa vào agent/người mới: [AGENTS.md](../AGENTS.md).

### Definition of Demo (chống "doc đẹp hơn sản phẩm")
Hai đường đi **sẽ trình hội đồng** — theo hướng tiến hóa B2C → B2B:

**Demo B2C (nền):**
1. User đăng ký cá nhân → mua **credit prepaid** ví cá nhân qua PayOS → webhook cộng credit.
2. Upload CV/JD → tạo session luyện → **reserve credit cá nhân**; AI sinh câu hỏi **bám CV/JD**.
3. Ghi âm trả lời → chấm rubric `JobCategory` → `Scored` → **consume credit** → xem **lịch sử**.
4. *(BC4)* Phân tích CV: feedback + khớp CV–JD.

**Demo B2B (xây trên B2C):**
1. Org mua credit qua **PayOS** (OneTime) → webhook cộng credit.
2. Tạo campaign từ JD → AI gợi ý câu hỏi + **tiêu chí có cấu trúc** → HR duyệt → publish.
3. Phát **magic-link** → ứng viên (account Candidate) làm bài.
4. Ứng viên bắt đầu → **reserve credit org**; AI chấm theo tiêu chí campaign → `Scored` → **consume**.
5. **Xếp hạng** + pass/fail → xuất CSV/PDF.

> Phần nào **chưa kịp build** ghi rõ trong doc service tương ứng (🟡/❌), **không** mô tả như đã chạy.

## 8. Hạ tầng & Deploy

- **CI/CD** ([.github/workflows/ci.yml](../.github/workflows/ci.yml)): push `main`/`dev`/`features/**` → build+test (dotnet) → build & push image lên **GHCR** → **SSH qua Tailscale vào server** → `docker compose pull && up -d`.
  - ⚠ Hiện build **4 service** (Auth/Interview/**Campaign**/Gateway). **Payment chưa có trong pipeline** — cần thêm build-push khi land. **AIService** deploy **riêng trên Mac** (Whisper nặng), **không** qua ci.yml.
- Routing & prefix gateway: `appsettings.json` của Gateway (`/api/v1/{service}` + StripPrefix).
- Triển khai 2 host (server + Mac cho AIService): [DEPLOYMENT.md](../DEPLOYMENT.md).
