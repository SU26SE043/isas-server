> **Bản sao cho agent** của [`docs/services/payment.md`](../../../docs/services/payment.md) — contract (API + DB + business rules) của PaymentService. **Source of truth ở `docs/`** (sửa thiết kế tại đó rồi copy lại, đừng sửa bản này lệch). Cửa vào + ràng buộc chung: [`/AGENTS.md`](../../../AGENTS.md).
>
> **Trạng thái (2026-07-11, P1):** ✅ đã vào tree (trước ở branch `features/payment-b2c`, `user_id`-only). **P1 xong**: `credit_accounts`/`credit_reservations`/`credit_transactions` theo `owner_type` (Org·User, D15) + migration `InitPaymentDb` + `ICreditAccountService.CreateAccountAsync`. CÒN (task sau, chưa làm ở P1): webhook cộng credit (P2) · `order_code` time+random (P7) · active-polling (P3) · reserve/consume/release (P4/P5/P6) · postpaid + hóa đơn (P8a/P8b). Order/ProductPackage/PaymentTransaction/Subscription vẫn giữ nguyên shape cũ (`user_id`, `OrderStatus` int-enum) từ branch cũ — refactor sang `owner_type` là phạm vi P2/P7, KHÔNG phải P1.

---

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
  kind:           enum(string)          // CreditPack · InvoiceSettlement · SubscriptionPurchase · SubscriptionRenewal 🔜(phase 2)
  packageId:      uuid?
  invoiceId:      uuid?
  subscriptionId: uuid?                 // 🔜 phase 2
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
  remainingCredits: int                 // ✅ F7: ĐÃ GỒM credit dùng thử (không tách xô riêng)
  reservedCredits:  int
  freeCreditsGranted: int               // ✅ F7 — suất dùng thử đã tặng ví này (0 = chưa/ví Org); ví chưa tồn tại → 0
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
  status:       enum(string)            // Issued · Paid · Overdue · Void
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

### Request/Response mẫu
```
POST /api/v1/payment/order   { "packageId":"<uuid gói OneTime>" }
→ 201 { "orderId":"…", "orderCode":260630153012, "checkoutUrl":"https://pay.payos.vn/web/…" }

GET /api/v1/payment/order/{id}/status
→ 200 { "orderCode":260630153012, "status":"Paid", "paidAt":"2026-06-30T15:32:10Z" }   // server đối soát PayOS nếu chưa có webhook

GET /api/v1/payment/me/account
→ 200 { "ownerType":"Org","ownerId":"…","paymentMode":"Prepaid","status":"Active",
        "remainingCredits":48,"reservedCredits":2,"creditLimit":null,"periodUsage":null }

POST /internal/credits/reserve  (X-Internal-Token)  { "ownerType":"Org","ownerId":"…","sessionId":"…" }
→ 200 { "reservationId":"…","reservedCredits":3 }   |   402 nếu hết credit/hạn mức
```
Webhook PayOS (KHÔNG qua gateway): `POST /payment/webhook/payos { code, desc, success, data:{orderCode,amount,…}, signature }` → verify HMAC-SHA256 → nếu `Paid` + `orderCode` chưa xử lý → cộng credit (idempotent) → `200`.

### Validation
| Field | Ràng buộc |
|---|---|
| `packageId` | bắt buộc; gói phải `is_active=true` (không → 400) |
| `amount` | ≥ ~2.000đ (PayOS); = `package.price_vnd` |
| `orderCode` | time+random, ≤ 2^53−1, **UNIQUE** (đụng → regenerate) |
| `CreditOpRequest` | `ownerType∈{Org,User}` · `ownerId` · `sessionId` (idempotency) — bắt buộc |
| webhook | **verify chữ ký TRƯỚC**; chỉ xử lý khi `success`/`Paid` |

### Bảng mã lỗi (đặc thù — chung [../architecture.md](../architecture.md) §6)
| Mã | Khi nào |
|---|---|
| 400 | gói không bán / không active · payload webhook thiếu field |
| 401 | thiếu Bearer (order public) · sai `X-Internal-Token` (internal) |
| 402 | **hết credit (prepaid)** / chạm `credit_limit` (postpaid) / có hóa đơn `Overdue` |
| 403 | `HrMember` gọi billing (A4) · non-owner đọc order/invoice |
| 404 | order/invoice không tồn tại |
| 409 | (admin) duyệt postpaid khi thiếu MST |

## Luồng (sequence)

**Mua pack prepaid (webhook + active-polling):**
```
FE ─POST /order {packageId}─► Payment ─tạo Order(Pending)+order_code─► PayOS ─► checkoutUrl
… người mua trả tiền …
PayOS ─webhook Paid (verify HMAC)─► Payment ─ Order→Paid + ledger(Purchase,+pack) + remaining_credits += credits   (idempotent /orderCode)
[song song] FE ─GET /order/{id}/status─► Payment ─ chưa có webhook? gọi PayOS get-payment-info đối soát NGAY ─► trạng thái
```

**Tiêu credit reserve→consume (1 lượt phỏng vấn):**
```
ứng viên bắt đầu ─► (B2B) Campaign | (B2C) Interview ─POST /internal/credits/reserve {owner,sessionId}─► Payment
      UPDATE … remaining−1, reserved+1 WHERE remaining≥1     ← 0 row ⇒ 402 (KHÔNG tạo session)
session Scored ─event SessionScored─► consume: reserved−1 + ledger(Consume,−1)
bỏ ngang/lỗi  ─event SessionAbandoned─► release: reserved−1, remaining+1
      (idempotent theo sessionId · out-of-order: Consumed/Released hấp thụ — §State machine)
```
> ⚠ **Sàng CV (D18/D19) KHÔNG đi qua luồng này** — không reserve/consume; billing chỉ ở lượt phỏng vấn thật.

**Postpaid chốt kỳ (chỉ Org):**
```
cuối kỳ ─► snapshot period_usage → tạo Invoice(Issued, count×unit_price) + reset period_usage=0   [1 transaction]
Org ─POST /invoices/{id}/pay─► PayOS ─webhook Paid─► Invoice→Paid   (Overdue ⇒ chặn reserve mới, KHÔNG văng in-flight)
```

---

## DB — `isas_payment`
```
credit_accounts (1/chủ ví: Org HOẶC User)
   ├── credit_reservations (theo session_id)
   ├── credit_transactions (sổ cái)
   ├── orders 1──* payment_transactions   (log sự kiện gateway: webhook/polling/redeliver — mua pack · tất toán · subscription)
   └── invoices (postpaid, CHỈ Org, theo kỳ)
product_packages 1──* orders        (owner ref lỏng → Auth: org_id hoặc user_id)
invoices        1──* orders        (orders.invoice_id — N Order tất toán 1 Invoice, retry được)
orders          1──1 subscriptions (subscriptions.order_id UNIQUE — 1 đơn ⇒ 1 kỳ hạn) ✅ F8
```
> ⚠ **F8 (2026-07-19)** ĐẢO CHIỀU khoá so với dòng cũ (`orders.subscription_id` 🔜 phase 2): thuê bao được tạo Ở webhook (sau đơn), nên khoá đặt phía `subscriptions` — vừa cho idempotency ở tầng DB (`UNIQUE(order_id)`), vừa khỏi phải quay lại UPDATE đơn. Kèm cột mới `credit_reservations.funded_by` (`Credit` default · `Subscription`). **Bản đầy đủ + lý do ở `docs/services/payment.md` (source of truth)**; copy này chỉ đồng bộ những chỗ F8 làm cho SAI, các mục `🔜 phase 2` còn lại trong file vốn đã lệch từ trước.

> **Quy ước kiểu DB:** `uuid·varchar(n)·text·int·bigint`(VND)·`numeric`·`bool·timestamptz·jsonb`, enum lưu **string**, `?`=nullable. Cột **snake_case**.

### `credit_accounts` — ví của chủ sở hữu (Org hoặc User) ✅ P1
```
id               uuid          PK
owner_type       varchar(8)    enum: Org · User
owner_id         uuid          ref lỏng → Auth
payment_mode     varchar(16)   enum: Prepaid · Postpaid (User LUÔN Prepaid)
status           varchar(16)   enum: Active · Suspended (mặc định Active) — đình chỉ nợ xấu/quá hạn → chặn reserve mới
remaining_credits int          prepaid: số credit còn (reserve trừ NGAY — xem §State machine); ✅ F7 gồm cả credit dùng thử
reserved_credits int           đang giữ chỗ (Reserved chưa Consumed/Released)
free_credits_granted int       ✅ F7 — suất dùng thử ĐÃ TẶNG ví này (0 = chưa tặng / ví Org); denormalize từ sổ cái reason=FreeGrant
credit_limit     int?          CHỈ Org/postpaid
period_usage     int?          CHỈ Org/postpaid — lượt đã dùng kỳ này
updated_at       timestamptz
                               UNIQUE (owner_type, owner_id)
```

### `credit_reservations` — giữ chỗ theo session ✅ P1 (schema — Reserve/Consume/Release = P4/P5/P6)
```
id         uuid          PK
owner_type varchar(8)    enum: Org · User
owner_id   uuid          chủ ví bị giữ chỗ
session_id uuid          UNIQUE, ref lỏng → Interview (idempotency)
status     varchar(16)   enum: Reserved · Consumed · Released
created_at timestamptz
```

### `credit_transactions` — sổ cái ✅ P1 (schema — Purchase/Consume ghi sổ = P2/P5)
```
id         uuid          PK
owner_type varchar(8)    enum: Org · User
owner_id   uuid
order_id   uuid?         FK → orders
session_id uuid?         ref lỏng → Interview
delta      int           +/− (cộng pack / trừ lượt)
reason     varchar(16)   enum: Purchase (+pack) · Consume (−1/lượt khi Scored) · Refund (admin hoàn — phase 2) · FreeGrant (+N suất dùng thử lúc tạo ví User — ✅ F7, order_id/session_id null)
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
kind             varchar(20)   enum: CreditPack · InvoiceSettlement (chỉ Org) · SubscriptionPurchase · SubscriptionRenewal 🔜(phase 2)
package_id       uuid?         FK → product_packages
invoice_id       uuid?         FK → invoices        (kind=InvoiceSettlement)
(F8: khoá nằm ở subscriptions.order_id, KHÔNG có orders.subscription_id — xem docs/services/payment.md)
amount_vnd       bigint
payos_order_code bigint        UNIQUE (time+random — xem Business rules)
status           varchar(16)   enum: Pending · Paid · Failed · Expired · Cancelled
expired_at       timestamptz
paid_at          timestamptz?
created_at       timestamptz
```
> ⚠ **Chưa refactor ở P1** — code hiện tại (từ branch cũ) vẫn `user_id` + `OrderStatus` int-enum (không `owner_type`/`owner_id`, không string-enum). Refactor `orders` sang `owner_type`/`owner_id` + `order_code` time+random là **P2/P7**, không phải P1.

### `payment_transactions` — log sự kiện gateway (append-only)
```
id                  uuid          PK
order_id            uuid          FK → orders (N–1: 1 order có NHIỀU sự kiện gateway)
gateway             varchar(16)   "payos"
event_source        varchar(16)   enum: webhook · polling · create — sự kiện đến từ đâu
gateway_txn_id      varchar?
status              varchar(16)   soi gương kết quả PayOS (Paid/Failed…) — KHÔNG tự quyết; `orders.status` là nguồn chân lý (§State machine)
raw_webhook_payload jsonb?        payload gốc — lưu để đối soát
created_at          timestamptz   index (order_id, created_at)
```
> **Vì sao N–1 (không phải 1–1):** 1 order có thể nhận **nhiều** sự kiện — webhook redeliver, kết quả active-polling, **webhook trả muộn sau `Expired`** (ca phải lưu bằng chứng để PlatformAdmin đối soát thủ công — §State machine). Log **append-only**, không ghi đè → không mất vết tiền. Trạng thái đơn đọc ở `orders.status`, KHÔNG đọc row mới nhất ở đây.

### `invoices` — postpaid, CHỈ Org
```
id             uuid          PK
org_id         uuid          ref lỏng → Auth
period_start   timestamptz
period_end     timestamptz
interview_count int
unit_price_vnd bigint
amount_vnd     bigint        = interview_count × unit_price_vnd
status         varchar(16)   enum: Issued · Paid · Overdue · Void (lập sai — không tính nợ)
issued_at      timestamptz
due_at         timestamptz
paid_at        timestamptz?
```

### `subscriptions` 🔜 *(phase 2)*
```
id         uuid   PK
owner_type varchar(8)    enum: Org · User
owner_id   uuid
package_id uuid   FK → product_packages
status     varchar(16)   enum: Active · Expired · Cancelled
started_at timestamptz
expires_at timestamptz
```
> ⚠ **F8 (2026-07-19) ĐÃ ĐẢO thiết kế này — shape ở trên là BẢN CŨ, đừng code theo.** Bản thật: FK nằm phía **`subscriptions.order_id`** (UNIQUE filtered) và **một lần mua = MỘT row** (gia hạn = row mới nối tiếp `expires_at` cũ), thay vì 1 Subscription nhận N Order. Lý do: thuê bao được tạo Ở webhook (sau đơn), nên `UNIQUE(order_id)` vừa là khoá **idempotency** ở tầng DB vừa khỏi phải quay lại UPDATE đơn. Thêm cột `billing_cycle`, `order_id`, `created_at/updated_at`, CHECK `expires_at > started_at`, và cột mới `credit_reservations.funded_by`. **Bản đầy đủ: [`docs/services/payment.md`](../../../docs/services/payment.md) §`subscriptions` (source of truth).**

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
PREPAID (có remaining):
reserve : UPDATE … SET remaining_credits = remaining_credits − 1,
                       reserved_credits  = reserved_credits  + 1
          WHERE owner=… AND remaining_credits ≥ 1     ← 0 row ⇒ hết credit ⇒ 402, KHÔNG tạo session
consume : UPDATE … SET reserved_credits  = reserved_credits − 1
          + INSERT credit_transactions(reason=Consume, delta=−1)
release : UPDATE … SET reserved_credits  = reserved_credits − 1,
                       remaining_credits  = remaining_credits + 1

POSTPAID (không remaining, dồn nợ vào period_usage):
reserve : UPDATE … SET reserved_credits  = reserved_credits + 1
          WHERE owner=… AND status='Active'
            AND period_usage + reserved_credits + 1 ≤ credit_limit   ← 0 row ⇒ 402
consume : UPDATE … SET reserved_credits  = reserved_credits − 1,
                       period_usage      = period_usage      + 1     ← ghi nợ kỳ (nguồn của interview_count)
          + INSERT credit_transactions(reason=Consume, delta=−1)
release : UPDATE … SET reserved_credits  = reserved_credits − 1      (period_usage KHÔNG đổi)
```
- **Reserve trừ `remaining` NGAY** (không chỉ tăng `reserved`) → 2 reserve song song không cùng vượt check ⇒ **chống double-spend**. `remaining` = tiêu được thật; `reserved` = đang giữ.
- **Bất biến audit (prepaid):** `remaining_credits + reserved_credits = Σ(credit_transactions.delta)` tại mọi thời điểm (reserve/release không ghi ledger nên bảo toàn tổng; Purchase/Consume/Refund mới đổi tổng). Job đối soát định kỳ so 2 vế → lệch = có bug bút toán.
- **Postpaid** (không có `remaining`): điều kiện reserve là `period_usage + reserved + 1 ≤ credit_limit` (và account `Active`, không có hóa đơn `Overdue`). **Consume mới cộng `period_usage`** (không phải reserve) → bỏ ngang/release **không** dồn nợ; `period_usage` chính là nguồn snapshot ra `invoice.interview_count` cuối kỳ.

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
- ⚠ **Sàng lọc CV (D18/D19) KHÔNG tiêu credit (phase 1).** `1 credit = 1 lượt phỏng vấn có audio` → đọc/chấm CV **không** phải lượt phỏng vấn ⇒ **không** reserve/consume, **không** chạm `credit_accounts`/`credit_reservations`. Chỉ **buổi phỏng vấn thật** mới reserve→consume (ứng viên sàng CV được mời → phỏng vấn = đi đúng luồng trên). Chi phí Gemini/CV là **giá vốn nội bộ**, CampaignService chặn bằng hard-filter + cap số CV/campaign ([campaign.md](campaign.md)). *(Phase 2 nếu tính phí sàng → **loại credit sàng riêng**, team xác nhận lại — như D17.)*

### Postpaid (trả sau)
- **Chỉ org được PlatformAdmin DUYỆT** mới bật `Postpaid` (cần **pháp nhân/MST** để xuất hóa đơn + đòi nợ). Mặc định org mới = `Prepaid`.
- Chặn reserve khi **`nợ + giữ ≥ credit_limit`** hoặc **có hóa đơn `Overdue`**.
- Cuối kỳ: chốt `period_usage` → tạo `invoice` (`interview_count × unit_price`) → org **tất toán qua PayOS** (`orders.kind = InvoiceSettlement`) → reset kỳ.
- **Rủi ro nợ xấu:** AI cost đã đốt thật → hạn mức + duyệt + đình chỉ là bắt buộc.

### Đình chỉ / hết hạn (exception)
- Hết credit (prepaid) / chạm hạn mức / hóa đơn quá hạn (postpaid) → admin đặt account `status = Suspended` (§State machine) → **chỉ chặn HÀNH ĐỘNG TƯƠNG LAI** (mời mới, reserve mới trả 402/403). **KHÔNG văng ứng viên đang thi** — reservation đã giữ chỗ nên **in-flight được bảo vệ**.

> Nguyên tắc tiền bạc: PaymentService **riêng**, không service nào khác ghi thẳng bảng payment — chỉ qua API nội bộ.
