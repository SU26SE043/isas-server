# PaymentService — Thanh toán PayOS (credit · prepaid + postpaid)

> 🟡 branch `features/payment-b2c`. Code: `src/services/Isas.PaymentService`. DB: `isas_payment`. Gateway: `/api/v1/payment`.
> **Billing theo chủ ví**: B2B = Org (xem Organization ở [auth.md](auth.md)), B2C = User cá nhân (xem [../decisions.md](../decisions.md) D15). Quy ước chung: [../architecture.md](../architecture.md) §5.

## Vai trò
- Thanh toán **PayOS** theo **mô hình credit** = **1 credit ≈ 1 lượt phỏng vấn AI chấm**. **Không** metering token LLM.
- **Chủ ví = Org HOẶC User** (`owner_type`/`owner_id`): **B2B** billing cấp **tổ chức** (`owner_type=Org`), không phải cá nhân HR; **B2C** = **ví cá nhân** (`owner_type=User` của chính người luyện), **prepaid-only**. Cùng pack/PayOS/reserve→consume, chỉ khác chủ ví (xem [decisions.md](../decisions.md) **D15**).
- **2 hình thức trả:**
  - **Prepaid (trả trước):** mua pack credit `OneTime` → tiêu dần. (`Subscription` = phase 2.) Áp cho **cả Org và B2C cá nhân**.
  - **Postpaid (trả sau):** **chỉ Org** được **PlatformAdmin duyệt** → dùng trước (dồn nợ tới **hạn mức**) → **cuối kỳ ra hóa đơn** → tất toán qua PayOS. **B2C/personal không áp dụng** (luôn Prepaid).
- **Đơn giá 1 lượt** = biến cấu hình (cần cho hóa đơn postpaid). Service riêng, cô lập dữ liệu tiền.

## Tiêu credit — Reserve → Consume (KHÔNG trừ thẳng)
Mô hình "giữ chỗ rồi mới trừ thật" (như pre-auth/capture của thẻ):
- **Reserve (giữ chỗ)** khi ứng viên **bắt đầu** bài → kiểm tra còn chỗ: prepaid `remaining ≥ 1`; postpaid `nợ + giữ < credit_limit`. Hết → **402/403**, không tạo session.
- **Consume (trừ thật)** khi session chuyển **`Scored`** (qua event `SessionScored`).
- **Release (nhả)** nếu bài **bỏ ngang quá hạn** (auto-submit/expire) hoặc **lỗi hệ thống** → trả lại chỗ đã giữ.
- **Idempotent theo `session_id`** (1 reservation/session; redeliver event không trừ trùng).
- **Ai gọi reserve:** **B2B** → CampaignService reserve `{owner=Org}` khi ứng viên bắt đầu (campaign biết org). **B2C** → **InterviewService** reserve `{owner=User=candidateId}` khi tạo session (không có orchestrator); hết credit → **402, không tạo session**. Consume/release ở cả hai vẫn theo event `SessionScored`/`SessionAbandoned`.

→ Ứng viên **rớt mạng / lỗi mic / bỏ ngang** ⇒ org **không mất credit oan** (giảm bão ticket refund).

### Token → tiền
Khách hiểu "lượt phỏng vấn", không hiểu token. Bài có **time limit** → chi phí AI/lượt **chặn trên** → giá cố định/lượt hợp lý. "Giá token" chỉ là **giá vốn nội bộ** (`Whisper(phút) + Gemini(token)`); đặt đơn giá bán **> giá vốn**.

---

## API — `/api/v1/payment`

> **Quy ước:** Base public `/api/v1/payment/*` (gateway → service). Auth: **JWT** (`OrgAdmin`/`Org` = thành viên org; **B2C** = `User`, `owner_id`=`sub`); **PlatformAdmin** cho nhánh admin; webhook + `/internal/*` **KHÔNG** qua gateway. **Kiểu dữ liệu:** `uuid` · `string` · `int` · `long` (VND) · `bool` · `datetime` · `enum(string)` · `T[]` · `?`. Mã lỗi chung: [../architecture.md](../architecture.md) §6 · **402** = hết credit/hạn mức. *(🟡 = có trên branch (cần refactor owner) · 🔜 = chưa build.)*
> **B2C:** cùng endpoint mua pack / xem số dư / polling đơn, nhưng chủ ví = **User**; chỉ Prepaid, không hóa đơn/postpaid.

### Schemas (DTO)

```
ProductPackage {
  id:               uuid
  name:             string
  type:             enum(string)        // OneTime · Subscription 🔜(phase 2)
  priceVnd:         long
  interviewCredits: int
  durationDays:     int?
  isActive:         bool
}

CreateOrderResponse {                   // POST /order, /invoices/{id}/pay
  orderId:     uuid
  orderCode:   long                     // payos_order_code (time+random)
  checkoutUrl: string                   // link PayOS
}

Order {
  id:             uuid
  ownerType:      enum(string)          // Org · User
  ownerId:        uuid
  kind:           enum(string)          // CreditPack · InvoiceSettlement
  packageId:      uuid?
  invoiceId:      uuid?
  amountVnd:      long
  payosOrderCode: long
  status:         enum(string)          // Pending · Paid · Failed · Expired · Cancelled
  expiredAt:      datetime
  paidAt:         datetime?
  createdAt:      datetime
}

CreditAccount {                         // GET /me/account
  ownerType:        enum(string)        // Org · User
  ownerId:          uuid
  paymentMode:      enum(string)        // Prepaid · Postpaid (User luôn Prepaid)
  remainingCredits: int
  reservedCredits:  int
  creditLimit:      int?                // chỉ Org/postpaid
  periodUsage:      int?                // chỉ Org/postpaid — lượt đã dùng kỳ này
  updatedAt:        datetime
}

Invoice {                               // postpaid, chỉ Org
  id:           uuid
  orgId:        uuid
  periodStart:  datetime
  periodEnd:    datetime
  interviewCount: int
  unitPriceVnd: long
  amountVnd:    long
  status:       enum(string)            // Issued · Paid · Overdue
  issuedAt:     datetime
  dueAt:        datetime
  paidAt:       datetime?
}

CreditOpRequest {                       // /internal/credits/reserve|consume|release
  ownerType: enum(string)               // Org · User
  ownerId:   uuid
  sessionId: uuid                       // idempotency key
}
```

### Public / Org / B2C (JWT)

**`GET /payment/package`** · **`GET /payment/package/{id}`** — Gói prepaid đang bán. Public. → `ProductPackage[]` / `ProductPackage`.

**`POST /payment/order`** 🟡 — Mua pack credit. Auth `OrgAdmin` (B2B) / `User` (B2C).
- Req: `{ packageId: uuid }` → Res **`201`** `CreateOrderResponse`. Lỗi: **400** (gói không bán) · **401**.

**`GET /payment/order/{id}`** 🟡 · **`GET /payment/my-orders`** 🟡 — Chi tiết / lịch sử đơn → `Order` / `Order[]`. Lỗi: **403/404**.

**`GET /payment/order/{id}/status`** 🔜 — **FE active-polling**: server chưa nhận webhook → gọi PayOS đối soát ngay. → `{ orderCode: long, status: enum(string), paidAt: datetime? }`.

**`GET /payment/me/account`** 🔜 — Số dư ví → `CreditAccount`. Lỗi: **401**.

**`GET /payment/me/invoices`** 🔜 · **`GET /payment/me/invoices/{id}`** 🔜 — Hóa đơn postpaid → `Invoice[]`/`Invoice`.

**`POST /payment/invoices/{id}/pay`** 🔜 — Tất toán hóa đơn. Auth `OrgAdmin`. → `CreateOrderResponse` (link PayOS). Lỗi: **404**.

**`POST /payment/webhook/payos`** 🟡 — **Webhook PayOS** (🔒 verify checksum), **KHÔNG** qua gateway. Cộng credit/tất toán **chỉ khi** `Paid`; idempotent theo `payos_order_code`. Req: payload PayOS → Res **`200`**.

### Admin (PlatformAdmin)

**`POST/PUT/DELETE /payment/package…`** 🟡 — CRUD gói (Req `ProductPackage`).
**`POST /payment/admin/orgs/{orgId}/postpaid`** 🔜 — Duyệt postpaid + đặt `credit_limit` (cần MST). Req: `{ creditLimit: int }`.
**`POST /payment/admin/orgs/{orgId}/suspend`** 🔜 — Đình chỉ org (nợ xấu/quá hạn).
**`GET/PUT /payment/admin/unit-price`** 🔜 — Đơn giá 1 lượt (`{ unitPriceVnd: long }`).
**`GET /payment/admin/transactions`** 🔜 — Giao dịch/hóa đơn toàn hệ.
**`POST /payment/admin/orgs/{orgId}/credits/adjust`** 🔜 *(phase 2)* — Cấp/hoàn credit thủ công.

### Nội bộ — Campaign/Interview → Payment — `X-Internal-Token`, **KHÔNG qua gateway** 🔜

**`POST /internal/credits/reserve`** — giữ 1 chỗ (B2B: Campaign gọi `owner=Org`; B2C: Interview gọi `owner=User`).
- Req: `CreditOpRequest` → Res **`200`** (`{ reservationId, reservedCredits }`). Hết hạn mức/số dư → **402**. Idempotent theo `sessionId`.

**`POST /internal/credits/consume`** — reservation → trừ thật (khi `SessionScored`). Req: `CreditOpRequest` → **`200`**.
**`POST /internal/credits/release`** — nhả chỗ giữ (bỏ ngang/lỗi). Req: `CreditOpRequest` → **`200`**.

> Có thể cho Payment **tự consume/release bằng cách nghe event `SessionScored`/`SessionAbandoned`** thay vì Campaign gọi — chốt khi build.

---

## DB — `isas_payment`
```
credit_accounts (1/chủ ví: Org HOẶC User)
   ├── credit_reservations (theo session_id)
   ├── credit_transactions (sổ cái)
   ├── orders 1──1 payment_transactions   (mua pack HOẶC tất toán hóa đơn)
   └── invoices (postpaid, CHỈ Org, theo kỳ)
product_packages 1──* orders        (owner ref lỏng → Auth: org_id hoặc user_id)
```

> **Quy ước kiểu DB:** `uuid·varchar(n)·text·int·bigint`(VND)·`numeric`·`bool·timestamptz·jsonb`, enum lưu **string**, `?`=nullable. Cột **snake_case**.

### `credit_accounts` — ví của chủ sở hữu (Org hoặc User)
```
id               uuid          PK
owner_type       varchar(8)    enum: Org · User
owner_id         uuid          ref lỏng → Auth
payment_mode     varchar(16)   enum: Prepaid · Postpaid (User LUÔN Prepaid)
remaining_credits int          prepaid: số credit còn
reserved_credits int           đang giữ chỗ
credit_limit     int?          CHỈ Org/postpaid
period_usage     int?          CHỈ Org/postpaid — lượt đã dùng kỳ này
updated_at       timestamptz
                               UNIQUE (owner_type, owner_id)
```

### `credit_reservations` — giữ chỗ theo session
```
id         uuid          PK
owner_type varchar(8)    enum: Org · User
owner_id   uuid          chủ ví bị giữ chỗ
session_id uuid          UNIQUE, ref lỏng → Interview (idempotency)
status     varchar(16)   enum: Reserved · Consumed · Released
created_at timestamptz
```

### `credit_transactions` — sổ cái
```
id         uuid          PK
owner_type varchar(8)    enum: Org · User
owner_id   uuid
order_id   uuid?         FK → orders
session_id uuid?         ref lỏng → Interview
delta      int           +/− (cộng pack / trừ lượt)
reason     varchar(16)   enum: Purchase · Consume · Refund
created_at timestamptz
```

### `product_packages`
```
id               uuid          PK
name             varchar
type             varchar(16)   enum: OneTime · Subscription 🔜(phase 2)
price_vnd        bigint
interview_credits int
duration_days    int?
is_active        bool
```

### `orders`
```
id               uuid          PK
owner_type       varchar(8)    enum: Org · User
owner_id         uuid
kind             varchar(20)   enum: CreditPack · InvoiceSettlement (chỉ Org)
package_id       uuid?         FK → product_packages
invoice_id       uuid?         FK → invoices
amount_vnd       bigint
payos_order_code bigint        UNIQUE (time+random — xem Business rules)
status           varchar(16)   enum: Pending · Paid · Failed · Expired · Cancelled
expired_at       timestamptz
paid_at          timestamptz?
created_at       timestamptz
```

### `payment_transactions`
```
id                  uuid          PK
order_id            uuid          FK → orders (1–1)
gateway             varchar(16)   "payos"
gateway_txn_id      varchar?
status              varchar(16)
raw_webhook_payload jsonb?        lưu để đối soát
created_at          timestamptz
```

### `invoices` — postpaid, CHỈ Org, theo kỳ
```
id             uuid          PK
org_id         uuid          ref lỏng → Auth
period_start   timestamptz
period_end     timestamptz
interview_count int
unit_price_vnd bigint
amount_vnd     bigint        = interview_count × unit_price_vnd
status         varchar(16)   enum: Issued · Paid · Overdue
issued_at      timestamptz
due_at         timestamptz
paid_at        timestamptz?
```

### `subscriptions` 🔜 *(phase 2)*
```
id         uuid   PK
org_id     uuid
order_id   uuid   FK → orders
package_id uuid   FK → product_packages
status     varchar(16)
started_at timestamptz
expires_at timestamptz
```

---

## Business rules

### `order_code` — duy nhất + KHÔNG đoán được
- **Không** auto-increment (lộ số lượng đơn) cũng **không** snowflake 64-bit (**có thể vượt trần số của PayOS**).
- Dùng **time-based + random** (vd `YYMMDDHHmmss` + vài số random) **trong phạm vi PayOS cho phép** (orderCode là số ≤ ~JS safe-int — **cần verify trần PayOS**).
- Đụng `UNIQUE(payos_order_code)` → **regenerate + retry**.

### Thanh toán — webhook + **active polling**
- Cộng credit / tất toán hóa đơn **chỉ khi PayOS webhook `Paid`** (verify **checksum/signature**). **Không** kích hoạt theo return-url FE. Idempotent theo `payos_order_code`.
- **Active polling:** FE gọi `GET /order/{id}/status`; nếu server **chưa** nhận webhook → server **chủ động gọi PayOS get-payment-info đối soát ngay** (cứu ca webhook delay/drop làm FE đứng hình). Lưu `raw_webhook_payload`.

### Reserve → Consume → Release (chi tiết ở mục trên)
- Reserve lúc bắt đầu, Consume lúc `Scored`, Release lúc bỏ ngang/lỗi hệ thống. Idempotent theo `session_id`.

### Postpaid (trả sau)
- **Chỉ org được PlatformAdmin DUYỆT** mới bật `Postpaid` (cần **pháp nhân/MST** để xuất hóa đơn + đòi nợ). Mặc định org mới = `Prepaid`.
- Chặn reserve khi **`nợ + giữ ≥ credit_limit`** hoặc **có hóa đơn `Overdue`**.
- Cuối kỳ: chốt `period_usage` → tạo `invoice` (`interview_count × unit_price`) → org **tất toán qua PayOS** (`orders.kind = InvoiceSettlement`) → reset kỳ.
- **Rủi ro nợ xấu:** AI cost đã đốt thật → hạn mức + duyệt + đình chỉ là bắt buộc.

### Đình chỉ / hết hạn (exception)
- Hết credit (prepaid) / chạm hạn mức / hóa đơn quá hạn (postpaid) → **chỉ chặn HÀNH ĐỘNG TƯƠNG LAI** (mời mới, reserve mới). **KHÔNG văng ứng viên đang thi** — reservation đã giữ chỗ nên **in-flight được bảo vệ**.

> Nguyên tắc tiền bạc: PaymentService **riêng**, không service nào khác ghi thẳng bảng payment — chỉ qua API nội bộ.
