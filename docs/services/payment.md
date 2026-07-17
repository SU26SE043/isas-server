# PaymentService — Thanh toán PayOS (credit · prepaid + postpaid)

> ✅ **Trong tree**: `src/services/Isas.PaymentService` — CI build+push image `isas.paymentservice`, gateway route `/api/v1/payment/**` live, có trong compose. DB: `isas_payment`. *(Nhánh `features/payment-b2c` cũ đã obsolete — code hiện dùng owner_type/reserve/consume/postpaid.)*
> **Billing theo chủ ví**: B2B = Org (xem Organization ở [auth.md](auth.md)), B2C = User cá nhân (xem [../decisions.md](../decisions.md) D15). Quy ước chung: [../architecture.md](../architecture.md) §5.
>
> ⚠ **Enum serialize dạng SỐ (integer) trên JSON** — PaymentService **KHÔNG** đăng ký `JsonStringEnumConverter` (xem `Program.cs`); các trường enum trong DTO (Order/Package/Invoice…) trả **số**, không phải chuỗi. **Ngoại lệ DUY NHẤT:** `GET /order/{id}/status` trả `status` dạng **chuỗi** (`OrderStatusResponse.Status = status.ToString()`). *(DB lưu enum dạng string qua `HasConversion<string>` — khác với JSON API; đừng nhầm.)* FE + client phụ thuộc số này (xem `isas-frontend/docs/api-spec.md` §Thanh toán + `isas-frontend/src/app/core/models/enums.ts`) → **KHÔNG** thêm converter.

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
- **Ai gọi reserve:** **InterviewService reserve cho cả hai dòng** khi tạo session (reserve-first). Owner do caller truyền: **B2B** → Campaign gửi `campaign.OrgId` khi ứng viên bấm Start (`/internal/sessions/campaign`) → reserve `{owner=Org}`; **B2C** → reserve `{owner=User=candidateId}`. Hết credit → **402, không tạo session**. Consume/release ở cả hai vẫn theo event `SessionScored`/`SessionAbandoned` (owner lấy từ reservation, E7). *(BK14 — B2B reserve wire tại Interview, không phải Campaign, để tránh orphan chicken-and-egg session_id.)*

→ Ứng viên **rớt mạng / lỗi mic / bỏ ngang** ⇒ org **không mất credit oan** (giảm bão ticket refund).

### Token → tiền
Khách hiểu "lượt phỏng vấn", không hiểu token. Bài có **time limit** → chi phí AI/lượt **chặn trên** → giá cố định/lượt hợp lý. "Giá token" chỉ là **giá vốn nội bộ** (`Whisper(phút) + Gemini(token)`); đặt đơn giá bán **> giá vốn**.

---

## API — `/api/v1/payment`

> **Quy ước:** Base public `/api/v1/payment/*` (gateway → service). Auth: **JWT** (`OrgAdmin`/`Org` = thành viên org; **B2C** = `User`, `owner_id`=`sub`); **PlatformAdmin** cho nhánh admin; webhook + `/internal/*` **KHÔNG** qua gateway. **Kiểu dữ liệu:** `uuid` · `string` · `int` · `long` (VND) · `bool` · `datetime` · `enum(int)` (serialize **SỐ** — xem note đầu file; ngoại lệ `/order/{id}/status`=chuỗi) · `T[]` · `?`. Mã lỗi chung: [../architecture.md](../architecture.md) §6 · **402** = hết credit/hạn mức. *(✅ = đã build · 🔜 = chưa build.)*
> **B2C:** cùng endpoint mua pack / xem số dư / polling đơn, nhưng chủ ví = **User**; chỉ Prepaid, không hóa đơn/postpaid.

### Schemas (DTO)

> ⚠ Enum serialize **SỐ** (int) — bản đồ số ở comment. `PackageResponse.interviewCredits` là **int**; **`priceVnd` là `long`** (✅ DB3 2026-07-17: `product_packages.price_vnd` int→bigint chống tràn VND giá gói lớn; JSON vẫn là số).

```
ProductPackage {                        // = PackageResponse (GET /package…)
  id:               uuid
  name:             string
  type:             enum(int)           // 1=OneTime · 2=Subscription 🔜(phase 2)
  priceVnd:         long                 // ✅ DB3: int→bigint (long)
  interviewCredits: int?
  durationDays:     int?
  isActive:         bool
  createdAt:        datetime
}

Order {                                 // = OrderResponse — POST /order, /invoices/{id}/pay TRẢ ĐẦY ĐỦ object này
  id:             uuid
  ownerType:      enum(int)             // 0=Org · 1=User
  ownerId:        uuid
  kind:           enum(int)             // 0=CreditPack · 1=InvoiceSettlement · 2=SubscriptionPurchase · 3=SubscriptionRenewal 🔜(phase 2)
  packageId:      uuid?
  invoiceId:      uuid?
  amountVnd:      long                  // amount_vnd bigint (long) — pack lớn/hóa đơn gộp kỳ vượt trần int
  payosOrderCode: long
  status:         enum(int)             // 1=Pending · 2=Paid · 3=Failed · 4=Expired · 5=Cancelled
  expiredAt:      datetime
  paidAt:         datetime?
  createdAt:      datetime
  checkoutUrl:    string?               // ✅ CHỈ có khi tạo đơn (POST /order · /invoices/{id}/pay) — link PayOS; null khi GET
}

CreditAccount {                         // GET /me/account 🔜 (chưa build) — enum cũng serialize SỐ khi build
  ownerType:        enum(int)           // 0=Org · 1=User
  ownerId:          uuid
  paymentMode:      enum(int)           // 0=Prepaid · 1=Postpaid (User luôn Prepaid)
  status:           enum(int)           // 0=Active · 1=Suspended (đình chỉ nợ xấu/quá hạn)
  remainingCredits: int
  reservedCredits:  int
  creditLimit:      int?                // chỉ Org/postpaid
  periodUsage:      int?                // chỉ Org/postpaid — lượt đã dùng kỳ này
  updatedAt:        datetime
}

Invoice {                               // ✅ P8b — postpaid, chỉ Org (= InvoiceResponse)
  id:           uuid
  ownerType:    enum(int)               // 0=Org (khớp credit_accounts owner model, thay orgId)
  ownerId:      uuid
  periodStart:  datetime
  periodEnd:    datetime
  interviewCount: int
  unitPrice:    decimal(16,2)           // = Billing:UnitPrice cấu hình
  amount:       decimal(16,2)           // interviewCount × unitPrice
  status:       enum(int)               // 0=Issued · 1=Paid · 2=Overdue · 3=Void
  createdAt:    datetime
  // 🔜 dueAt/paidAt/issuedAt: BK17 — hiện paid-ness derive từ status=Paid + order settle
}

OrderStatusResponse {                   // GET /order/{id}/status — NGOẠI LỆ: status là CHUỖI
  orderCode: long
  status:    string                     // "Pending"·"Paid"·"Failed"·"Expired"·"Cancelled" (ToString, khác các DTO khác)
  paidAt:    datetime?
}

CreditOpRequest {                       // /internal/credits/reserve|consume|release
  ownerType: enum(int)                  // 0=Org · 1=User (nội bộ; Interview/Campaign gửi)
  ownerId:   uuid
  sessionId: uuid                       // idempotency key
}
```

### Public / Org / B2C (JWT)

**`GET /payment/package`** · **`GET /payment/package/{id}`** — Gói prepaid đang bán. Public. → `ProductPackage[]` / `ProductPackage`.

**`POST /payment/order`** ✅ — Mua pack credit. Auth `OrgAdmin` (B2B) / `User` (B2C). Chủ ví lấy từ JWT (claim `org_id`→Org, else `sub`→User). `HrMember`→**403** (A4).
- Req: `{ packageId: uuid }` → Res **`201`** `Order` (**đầy đủ** — id, ownerType, ownerId, kind, packageId, invoiceId, status, amountVnd, payosOrderCode, expiredAt, paidAt, createdAt, **`checkoutUrl`** = link PayOS để redirect). Lỗi: **404** (packageId không tồn tại) · **400** (gói ngừng bán, `is_active=false`) · **403** (HrMember) · **401** · **502** (PayOS reject/misconfig → `PaymentGatewayException`). *(BK19 ratify 2026-07-13: unknown id → 404 "Package not found"; inactive → 400 "Package is no longer available".)*

**`GET /payment/order/{id}`** ✅ · **`GET /payment/order/my-orders`** ✅ — Chi tiết / lịch sử đơn → `Order` / `Order[]`. *(route thật = `/order/my-orders` — `[Route("order")]`+`[HttpGet("my-orders")]`, KHÔNG phải `/my-orders`.)* Lỗi: **404** (không tồn tại **hoặc** non-owner — không lộ tồn tại; BK15).

**`DELETE /payment/order/{id}`** ✅ — Huỷ đơn `Pending` (owner-scope). Res **`204`**. Lỗi: **400** (đơn không ở trạng thái `Pending`) · **404** (không tồn tại/non-owner).

**`GET /payment/order/{id}/status`** ✅ — **FE active-polling**: server chưa nhận webhook → gọi PayOS đối soát ngay. → `OrderStatusResponse` `{ orderCode: long, status: string, paidAt: datetime? }` (**ngoại lệ:** `status` là **CHUỖI** ở đây). Lỗi: **404** (không tồn tại/non-owner).

**`GET /payment/me/account`** 🔜 — Số dư ví → `CreditAccount`. Lỗi: **401**.

**`GET /payment/me/invoices`** ✅ P8b · **`GET /payment/me/invoices/{id}`** ✅ P8b — Hóa đơn postpaid (owner-scope; non-owner→404) → `Invoice[]`/`Invoice`.

**`POST /payment/invoices/{id}/pay`** ✅ P8b — Tất toán hóa đơn. Auth `Employer`, owner-scope; `HrMember`→**403** (A4). → **`200`** `Order` (**đầy đủ**, kind=`InvoiceSettlement`, invoice_id, kèm `checkoutUrl`; reuse `OrderService` tạo link PayOS). Lỗi: **404** (không tồn tại/non-owner) · **409** (đã Paid/Void) · **502** (PayOS reject).

**`POST /payment/webhook/payos`** ✅ — **Webhook PayOS** (🔒 verify checksum), **KHÔNG** qua gateway. Chỉ khi `Paid`, idempotent theo `payos_order_code`. **✅ P8b: BRANCH theo `Order.Kind`** — `CreditPack`→cộng credit (P2); `InvoiceSettlement`→invoice `Issued/Overdue→Paid` (ExecuteUpdate guard, KHÔNG cộng credit). Req: payload PayOS → Res **`200`**.

**`POST /payment/admin/invoices/close`** ✅ P8b — Chốt kỳ postpaid (1 transaction): snapshot `period_usage` → `Invoice(Issued, amount=count×Billing:UnitPrice)` → reset `period_usage=0`. Auth `Roles="Admin"` (PlatformAdmin ✅ A5) + guard `HrMember`→403.

### Admin (PlatformAdmin)

**`POST/PUT/DELETE /payment/package…`** ✅ **A5** — CRUD gói (Req `ProductPackage`). Auth `Roles="Admin"` (PlatformAdmin, AUTH-3/7 — trước v22 comment hở → mở toang, nay đóng). GET catalog (trên) = Public.
**`POST /payment/admin/orgs/{orgId}/postpaid`** 🔜 — Duyệt postpaid + đặt `credit_limit` (cần MST). Req: `{ creditLimit: int }`.
**`POST /payment/admin/orgs/{orgId}/suspend`** 🔜 — Đình chỉ org (nợ xấu/quá hạn).
**`GET/PUT /payment/admin/unit-price`** 🔜 — Đơn giá 1 lượt (`{ unitPriceVnd: long }`).
**`GET /payment/admin/transactions`** 🔜 — Giao dịch/hóa đơn toàn hệ.
**`POST /payment/admin/orgs/{orgId}/credits/adjust`** 🔜 *(phase 2)* — Cấp/hoàn credit thủ công.

### Nội bộ — Interview → Payment — `X-Internal-Token`, **KHÔNG qua gateway** ✅ (P4/P5/P6)

**`POST /internal/credits/reserve`** — giữ 1 chỗ; **InterviewService gọi cho cả hai dòng** khi tạo session (B2B `owner=Org` từ `campaign.OrgId` Campaign gửi kèm — **BK14**; B2C `owner=User`).
- Req: `CreditOpRequest` → Res **`200`** (`{ reservationId, reservedCredits }`). Hết hạn mức/số dư → **402**. Idempotent theo `sessionId`.

**`POST /internal/credits/consume`** — reservation → trừ thật (khi `SessionScored`). Req: `CreditOpRequest` → **`200`**.
**`POST /internal/credits/release`** — nhả chỗ giữ (bỏ ngang/lỗi). Req: `CreditOpRequest` → **`200`**.

> Có thể cho Payment **tự consume/release bằng cách nghe event `SessionScored`/`SessionAbandoned`** thay vì Campaign gọi — chốt khi build.

### Request/Response mẫu
```
POST /api/v1/payment/order   { "packageId":"<uuid gói OneTime>" }
→ 201 {   // OrderResponse đầy đủ — enum là SỐ; checkoutUrl để redirect PayOS
    "id":"…", "ownerType":1, "ownerId":"…", "kind":0, "packageId":"…", "invoiceId":null,
    "status":1, "amountVnd":200000, "payosOrderCode":260630153012,
    "expiredAt":"2026-06-30T15:45:12Z", "paidAt":null, "createdAt":"2026-06-30T15:30:12Z",
    "checkoutUrl":"https://pay.payos.vn/web/…"
  }

GET /api/v1/payment/order/{id}/status
→ 200 { "orderCode":260630153012, "status":"Paid", "paidAt":"2026-06-30T15:32:10Z" }   // NGOẠI LỆ: status CHUỖI · server đối soát PayOS nếu chưa có webhook

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
| 403 | ✅ **A4** `HrMember` (claim `org_role`) gọi billing money-mutation (`POST /order`·`/invoices/{id}/pay`·`/admin/invoices/close`) → `Forbid()`; B2C (không claim)/OrgAdmin không chặn. *(Non-owner đọc/huỷ order/invoice → **404**, không 403 — xem hàng dưới.)* |
| 404 | order/invoice không tồn tại **hoặc** non-owner (order/invoice/status; không phân biệt được từ ngoài → không lộ tồn tại; ✅ **BK15** gom về 404 khớp P3/P8b) |
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
subscriptions   1──* orders        (orders.subscription_id — N Order gia hạn 1 Subscription) 🔜 phase 2
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
                               CHECK ck_credit_accounts_non_negative: remaining_credits>=0 AND reserved_credits>=0 AND (period_usage IS NULL OR period_usage>=0)  ✅ DB1 (2026-07-17)
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
                          CHECK ck_credit_transactions_delta_nonzero: delta<>0  ✅ DB1 (2026-07-17)
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
subscription_id  uuid?         FK → subscriptions   (kind=SubscriptionPurchase/Renewal) 🔜(phase 2)
amount_vnd       bigint
payos_order_code bigint        UNIQUE (time+random — xem Business rules)
status           varchar(16)   enum: Pending · Paid · Failed · Expired · Cancelled
expired_at       timestamptz
paid_at          timestamptz?
created_at       timestamptz
```
> ⚠ **`amount_vnd` = `bigint` (long)** — VND nguyên với pack lớn / hóa đơn postpaid gộp kỳ vượt trần `int` (~2,1 tỷ ₫). `InitialCreate` tạo cột `integer`; migration **`AmountVndToBigint`** (Đợt-3, `20260715113108`) alter → `bigint`. **Phải apply tay** trên DB server trước khi bán pack lớn (rule no-auto-migrate). `Order.AmountVnd` + `OrderResponse.AmountVnd` = `long`. *(Lưu ý: `product_packages.price_vnd` vẫn `integer` — pack đơn lẻ trong trần int, chưa đổi.)*

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

### `invoices` — ✅ P8b (migration `AddInvoices`) — postpaid, CHỈ Org, theo kỳ
```
id             uuid          PK
owner_type     varchar        enum: Org (khớp credit_accounts owner model, thay org_id)
owner_id       uuid          ref lỏng → Auth
period_start   timestamptz
period_end     timestamptz
interview_count int
unit_price     numeric(16,2)  = Billing:UnitPrice cấu hình
amount         numeric(16,2)  = interview_count × unit_price
status         varchar(16)   enum: Issued · Paid · Overdue · Void
created_at     timestamptz
```
> **P8b reconcile (vòng 14):** dùng `owner_type/owner_id` + `numeric` (nhất quán schema payment còn lại) thay `org_id`+`*_vnd bigint`. Bỏ `issued_at/due_at/paid_at` — paid-ness derive từ `status=Paid` + order settle; thêm lại nếu HR cần hạn hóa đơn (**BK17**). `orders.invoice_id` (nullable FK Restrict, kind=InvoiceSettlement) + `orders.package_id`→nullable (đơn settle không gắn pack).

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
> FK nằm ở phía **`orders.subscription_id`** (KHÔNG đặt `order_id` ở đây) → 1 Subscription nhận **N Order** theo thời gian: `SubscriptionPurchase` (mua đầu) + mỗi kỳ `SubscriptionRenewal`. Cùng pattern với `orders.invoice_id`.

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
- **✅ DB4 (2026-07-17) — reconciler bất biến `reserved_credits == count(credit_reservations WHERE status=Reserved cùng owner)`:** `CreditReservationReconciler` (BackgroundService, `Reconcile:Enabled/ScanIntervalSeconds`, mặc định on/120s) quét định kỳ TỪ PHÍA `credit_accounts` → mỗi ví `CountAsync(Reserved)` → lệch → `ExecuteUpdate reserved_credits = count`. Sửa drift do crash giữa reserve/consume/release. Guard chống âm (count≥0 + CHECK `ck_credit_accounts_non_negative` DB1). **Scope core Payment-DB thuần** (reservation có sẵn owner); phần "reservation `Reserved` mà session Interview đã terminal (event settlement rớt)" = **DB4b** (backlog — `SettlementReconciler` bên Interview re-publish event; **DB2** outbox sẽ diệt gốc mất-event).

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
