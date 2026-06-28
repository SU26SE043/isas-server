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
  status:           enum(string)        // Active · Suspended (đình chỉ nợ xấu/quá hạn)
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
status           varchar(16)   enum: Active · Suspended (mặc định Active) — đình chỉ nợ xấu/quá hạn → chặn reserve mới
remaining_credits int          prepaid: số credit còn (reserve trừ NGAY — xem §State machine)
reserved_credits int           đang giữ chỗ (Reserved chưa Consumed/Released)
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
reason     varchar(16)   enum: Purchase (+pack) · Consume (−1/lượt khi Scored) · Refund (admin hoàn — phase 2)
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
status              varchar(16)   soi gương kết quả PayOS (Paid/Failed…) — KHÔNG tự quyết; `orders.status` là nguồn chân lý (§State machine)
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

## State machine & vòng đời (chốt trạng thái + transition)

> Mỗi thực thể mang tiền phải có **transition tường minh + trạng thái cuối + xử lý redeliver / event ra ngoài thứ tự**. Enum trạng thái xem §DB. `★` = terminal (bất biến).

### Order — vòng đời đơn mua/tất toán
```
            ┌─(webhook Paid + verify checksum)─► Paid       ★ → cộng credit / tất toán hóa đơn
Pending ────┤
            ├─(quá expired_at — job nền quét)──► Expired    ★
            ├─(PayOS báo lỗi)─────────────────► Failed      ★
            └─(người mua / PayOS hủy)─────────► Cancelled   ★
```
- **Chỉ `Pending` mới đổi trạng thái.** 4 trạng thái cuối bất biến → webhook/redeliver tới sau khi đã terminal = **bỏ qua** (idempotent theo `payos_order_code`).
- **Webhook trả muộn sau `Expired`** (PayOS delay/drop rồi mới `Paid`): **KHÔNG tự cộng credit** — lưu `raw_webhook_payload`, gắn cờ **cần đối soát thủ công** (PlatformAdmin honor hoặc refund). Không để code tự quyết tiền trong ca mơ hồ.
- **Ai đẩy `Pending → Expired`:** job nền quét `expired_at < now()` (**không** tính lười) → đóng đơn rác.
- `orders.status` là **nguồn chân lý**; `payment_transactions.status` chỉ soi gương + lưu raw để đối soát, **không tự quyết**.

### credit_reservations — giữ chỗ theo session
```
Reserved ─(SessionScored → consume)──────► Consumed  ★ (ghi ledger −1)
        └─(SessionAbandoned/lỗi → release)► Released  ★ (không trừ)
```
- **Idempotent theo `session_id`** (UNIQUE 1 reservation/session) — redeliver cùng event không xử lý lại.
- ⚠ **Event ra ngoài thứ tự** (RabbitMQ **không đảm bảo thứ tự** giữa `SessionScored`/`SessionAbandoned`) — bắt buộc xử:
  - đã `Consumed` mà nhận `release` → **bỏ qua** (không hoàn).
  - đã `Released` mà nhận `consume` → **bỏ qua** (không trừ).
  - ⇒ `Consumed`/`Released` là **hấp thụ (absorbing)**: chỉ `Reserved` mới chuyển tiếp.
- **consume/release đến mà CHƯA có reservation** (miss event reserve): **không tạo bút toán**, log cảnh báo, trả `200` (tránh kẹt retry). Reserve là điều kiện vào bài → thiếu reserve = bất thường cần điều tra, **không âm thầm trừ**.

### Kế toán `remaining ↔ reserved` — bút toán ATOMIC (chống double-spend)
> Bất biến lõi của ví. Mọi bút toán chạy trong **1 transaction + điều kiện WHERE** (optimistic), **không** đọc-rồi-ghi rời.
```
reserve : UPDATE … SET remaining_credits = remaining_credits − 1,
                       reserved_credits  = reserved_credits  + 1
          WHERE owner=… AND remaining_credits ≥ 1     ← 0 row ⇒ hết credit ⇒ 402, KHÔNG tạo session
consume : UPDATE … SET reserved_credits  = reserved_credits − 1
          + INSERT credit_transactions(reason=Consume, delta=−1)
release : UPDATE … SET reserved_credits  = reserved_credits − 1,
                       remaining_credits  = remaining_credits + 1
```
- **Reserve trừ `remaining` NGAY** (không chỉ tăng `reserved`) → 2 reserve song song không cùng vượt check ⇒ **chống double-spend**. `remaining` = tiêu được thật; `reserved` = đang giữ.
- **Postpaid** (không có `remaining`): điều kiện reserve là `period_usage + reserved + 1 ≤ credit_limit` (và account `Active`, không có hóa đơn `Overdue`).

### Invoice — postpaid, CHỈ Org
```
Issued ─(tất toán PayOS OK)─► Paid    ★
       └─(quá due_at)───────► Overdue ─(tất toán)─► Paid
                                    (Overdue ⇒ chặn reserve mới, KHÔNG văng in-flight)
       (lập sai)─► Void      ★ (hủy hóa đơn, không tính nợ)
```
- **Chốt kỳ là 1 transaction:** snapshot `period_usage` → tạo `invoice(Issued)` → **reset `period_usage = 0` cùng transaction** (fail giữa chừng → rollback cả 2, không mất/nhân nợ).

### credit_accounts — trạng thái tài khoản (cột `status`)
```
Active ─(admin đình chỉ: nợ xấu / hóa đơn Overdue / hết hạn)─► Suspended
Suspended ─(admin gỡ / đã tất toán)─────────────────────────► Active
payment_mode:  Prepaid ─(PlatformAdmin duyệt + MST)─► Postpaid   (thu hồi → Prepaid: admin)
```
- `Suspended` ⇒ mọi **reserve trả 402/403**; **KHÔNG** chạm reservation đang `Reserved` (in-flight được bảo vệ) — bài đang chạy vẫn consume/release bình thường.

---

## Business rules

### `order_code` — duy nhất + KHÔNG đoán được
- **Không** auto-increment (lộ số lượng đơn) cũng **không** snowflake 64-bit (**vượt trần PayOS 2^53−1** — §PayOS).
- Dùng **time-based + random** (`YYMMDDHHmmss` + vài số random), giữ **≤ 9.007.199.254.740.991** (trần PayOS, 2^53−1).
- Đụng `UNIQUE(payos_order_code)` → **regenerate + retry**.

### Thanh toán — webhook + **active polling**
- Cộng credit / tất toán hóa đơn **chỉ khi PayOS webhook `Paid`** (verify **chữ ký HMAC-SHA256** — §PayOS). **Không** kích hoạt theo return-url FE. Idempotent theo `payos_order_code`.
- **Active polling:** FE gọi `GET /order/{id}/status`; nếu server **chưa** nhận webhook → server **chủ động gọi PayOS get-payment-info đối soát ngay** (cứu ca webhook delay/drop làm FE đứng hình). Lưu `raw_webhook_payload`.

### PayOS — ràng buộc cổng (đã verify payos.vn — 2026-06-28)
> Lấy từ tài liệu chính thức **payos.vn** (VietQR/VND). **Chốt cuối theo dashboard my.payos.vn** của tài khoản thật.

| Hạng mục | Ràng buộc PayOS | Hệ quả cho ISAS |
|---|---|---|
| `orderCode` | **số nguyên dương ≤ 9.007.199.254.740.991** (2^53−1, PayOS xử lý như JS number); **duy nhất vĩnh viễn**, không tái dùng | `payos_order_code bigint`; `YYMMDDHHmmss`+random giữ **< 9,007×10¹⁵**; **KHÔNG** snowflake 64-bit (vượt trần) — [D12](../decisions.md) |
| `description` | **chuỗi NGẮN**: ≤ **25 ký tự** (tài khoản liên kết payOS); **9 ký tự** nếu VietQR ngân hàng không liên kết | mô tả gọn (vd `ISAS <orderCode>`); **không** nhồi tên gói/email vào `description` |
| `amount` | số nguyên VND; tối thiểu ~**2.000đ** (theo ví dụ SDK) | `amount_vnd bigint` |
| Field bắt buộc create | `orderCode, amount, description, cancelUrl, returnUrl, signature` | phải truyền `returnUrl`+`cancelUrl` **dù không tin** return-url để cộng tiền (chỉ webhook mới cộng) |
| Chữ ký | **HMAC-SHA256**: sort field theo key **A→Z** → nối `key=value&…` → ký bằng **checksum key** | verify y hệt cho **cả** create lẫn webhook; checksum key ở my.payos.vn (đổi được) |
| Trạng thái PayOS | `PENDING · PAID · PROCESSING · CANCELLED · EXPIRED` (**không có "Failed"**) | map → `Order.status`: `PROCESSING`→giữ `Pending`; `CANCELLED`/`EXPIRED`→`Cancelled`/`Expired`; **`Failed`** của ta = lỗi tạo link/nội bộ, không phải status PayOS |
| Webhook | phải **đăng ký + confirm** URL; PayOS POST `{code, desc, success, data, signature}` | endpoint webhook **không** qua gateway; **verify signature trước** khi xử lý |

### Reserve → Consume → Release
- Reserve lúc bắt đầu, Consume lúc `Scored`, Release lúc bỏ ngang/lỗi hệ thống. Idempotent theo `session_id`.
- **Transition + bút toán atomic + xử lý event ra ngoài thứ tự**: xem **§State machine** (`credit_reservations` + kế toán `remaining↔reserved`) — đây là phần chống double-spend & double-process, **bắt buộc** khi build.

### Postpaid (trả sau)
- **Chỉ org được PlatformAdmin DUYỆT** mới bật `Postpaid` (cần **pháp nhân/MST** để xuất hóa đơn + đòi nợ). Mặc định org mới = `Prepaid`.
- Chặn reserve khi **`nợ + giữ ≥ credit_limit`** hoặc **có hóa đơn `Overdue`**.
- Cuối kỳ: chốt `period_usage` → tạo `invoice` (`interview_count × unit_price`) → org **tất toán qua PayOS** (`orders.kind = InvoiceSettlement`) → reset kỳ.
- **Rủi ro nợ xấu:** AI cost đã đốt thật → hạn mức + duyệt + đình chỉ là bắt buộc.

### Đình chỉ / hết hạn (exception)
- Hết credit (prepaid) / chạm hạn mức / hóa đơn quá hạn (postpaid) → admin đặt account `status = Suspended` (§State machine) → **chỉ chặn HÀNH ĐỘNG TƯƠNG LAI** (mời mới, reserve mới trả 402/403). **KHÔNG văng ứng viên đang thi** — reservation đã giữ chỗ nên **in-flight được bảo vệ**.

> Nguyên tắc tiền bạc: PaymentService **riêng**, không service nào khác ghi thẳng bảng payment — chỉ qua API nội bộ.
