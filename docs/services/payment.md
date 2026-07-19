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

CreditAccount {                         // GET /me/account ✅ (= CreditAccountResponse) — enum serialize SỐ. KHÔNG trả `id` (định danh ví = ownerType+ownerId, alternate key DB9)
  ownerType:        enum(int)           // 0=Org · 1=User
  ownerId:          uuid
  paymentMode:      enum(int)           // 0=Prepaid · 1=Postpaid (User luôn Prepaid)
  status:           enum(int)           // 0=Active · 1=Suspended (đình chỉ nợ xấu/quá hạn)
  remainingCredits: int                 // ✅ F7: ĐÃ GỒM credit dùng thử (không tách xô riêng)
  reservedCredits:  int
  freeCreditsGranted: int               // ✅ F7 — suất dùng thử đã tặng cho ví này (0 = chưa/ví Org). Ví chưa tồn tại → 0 (KHÔNG hứa trước quota cấu hình)
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

**`GET /payment/package`** · **`GET /payment/package/{id}`** — Gói prepaid đang bán. Public. → `ProductPackage[]` / `ProductPackage`. Cả hai **lọc `is_active=true`**; GET-by-id không thấy (id lạ **hoặc** gói đã ngừng bán) → **404** `{message:"Package not found"}`. *(Fix e2e 2026-07-18: trước đây GET-by-id id lạ ném NRE → **500** trên endpoint `[AllowAnonymous]`, và gói đã soft-delete vẫn trả 200 — lệch GET catalog.)*

**`POST /payment/order`** ✅ — Mua pack credit. Auth `OrgAdmin` (B2B) / `User` (B2C). Chủ ví lấy từ JWT (claim `org_id`→Org, else `sub`→User). `HrMember`→**403** (A4).
- Req: `{ packageId: uuid }` → Res **`201`** `Order` (**đầy đủ** — id, ownerType, ownerId, kind, packageId, invoiceId, status, amountVnd, payosOrderCode, expiredAt, paidAt, createdAt, **`checkoutUrl`** = link PayOS để redirect). Lỗi: **404** (packageId không tồn tại) · **400** (gói ngừng bán, `is_active=false`) · **403** (HrMember) · **401** · **502** (PayOS reject/misconfig → `PaymentGatewayException`). *(BK19 ratify 2026-07-13: unknown id → 404 "Package not found"; inactive → 400 "Package is no longer available".)*

**`GET /payment/order/{id}`** ✅ — Chi tiết đơn → `Order`. Lỗi: **404** (không tồn tại **hoặc** non-owner — không lộ tồn tại; BK15).

**`GET /payment/order/my-orders`** ✅ — Lịch sử đơn của **chính chủ ví** (JWT: `org_id`→Org, else `sub`→User; PAY-2/D15) → `Order[]`, mới nhất trước. *(route thật = `/order/my-orders` — `[Route("order")]`+`[HttpGet("my-orders")]`, KHÔNG phải `/my-orders`.)*
- **Query (opt-in, backward-compat):** `?status=` lọc `OrderStatus` (numeric: 1=Pending..5=Cancelled, đẩy xuống SQL) · `?limit=` (mặc định **và** tối đa **500**) · `?cursor=` opaque keyset. **Body vẫn là mảng JSON** — client không gửi gì thì hành vi y như trước.
- **Phân trang keyset** `(created_at DESC, id DESC)` (mẫu DB8 `Isas.Shared/Pagination`): next-cursor ở header **`X-Next-Cursor`** (vắng = hết trang); cursor rác → trang đầu (không 500). Đổi hành vi duy nhất: chủ ví có **>500 đơn** nay chỉ nhận 500 ở trang đầu.
- *Vì sao cần:* mỗi lần bấm checkout là INSERT 1 row `orders` (ý định trả tiền, KHÔNG phải trả xong) → đơn `Pending` bỏ dở tích lại vĩnh viễn, không job nào dọn.
- Index `ix_orders_owner_created` `(owner_type, owner_id, created_at DESC, id DESC)` (DB26) khớp sẵn hình dạng này → **không cần index/migration mới**.
- Lỗi: **401** · **403** (JWT không suy được chủ ví).

**`DELETE /payment/order/{id}`** ✅ — Huỷ đơn `Pending` (owner-scope). Res **`204`**. Lỗi: **400** (đơn không ở trạng thái `Pending`) · **404** (không tồn tại/non-owner).

**`GET /payment/order/{id}/status`** ✅ — **FE active-polling**: server chưa nhận webhook → gọi PayOS đối soát ngay. → `OrderStatusResponse` `{ orderCode: long, status: string, paidAt: datetime? }` (**ngoại lệ:** `status` là **CHUỖI** ở đây). Lỗi: **404** (không tồn tại/non-owner).

**`GET /payment/me/account`** ✅ (2026-07-18) — Số dư ví của **chính caller** → `CreditAccount`. Chủ ví suy từ JWT (D15: claim `org_id`→Org, else `sub`→User) nên **không có đường đọc ví người khác**; HrMember xem được (AUTH-6 chỉ chặn money-mutation). Chưa từng mua credit (chưa có row ví) → **200** ví rỗng `remainingCredits:0` (đọc thuần, KHÔNG tạo ví — ví tạo lazy ở webhook Paid P2). Lỗi: **401**.

**`GET /payment/me/credit-transactions`** ✅ **F19** (2026-07-19) — Lịch sử **biến động credit** của chính caller → `CreditTransaction[]`. Chủ ví suy từ JWT (D15) nên không có đường đọc sổ cái người khác. Trước F19 KHÔNG endpoint nào đọc `credit_transactions` cho **bất kỳ ai**: người dùng thấy số dư (`me/account`) nhưng mất credit thì không tra được nó đi đâu. Keyset-paged theo mẫu chung (DB8): body vẫn **mảng JSON**, `?cursor=&limit=` opt-in, next-cursor ở header `X-Next-Cursor`, default 500 ⇒ **FE không phải sửa**. `?reason=` lọc theo loại bút toán. **KHÔNG** trả `grantedBy`/`note` (thông tin vận hành nội bộ — chỉ bản admin có). Chưa có bút toán nào → **200** mảng rỗng. Lỗi: **401**.

**`GET /payment/me/subscription`** ✅ **F8** (2026-07-19) — Thuê bao đang hiệu lực của **chính caller** → `{ ownerType, ownerId, active: bool, billingCycle: "Monthly"|"Annual"|null, startedAt?, expiresAt? }`. Chủ ví suy từ JWT (D15) nên không có đường đọc thuê bao người khác; HrMember xem được membership org (AUTH-6 chỉ chặn money-mutation). Chưa có thuê bao → **200** `active:false` (không phải 404 — cùng lối `GET /me/account`). Đọc thuần. Lỗi: **401**.

**`POST /payment/order`** với gói `Subscription` ✅ **F8** — cùng endpoint mua pack, nhưng gói `type=Subscription` đi **đường riêng**: đơn mang `kind=SubscriptionPurchase` (hoặc `SubscriptionRenewal` nếu chủ ví còn hạn) thay vì `CreditPack`. Gói thiếu `duration_days` → **400** (chặn trước khi tiền rời tay). ⚠ **KHÔNG gỡ guard DB20** — bất biến `kind=CreditPack ⇒ package.interview_credits > 0` giữ nguyên; gói thuê bao chỉ đơn giản không bao giờ mang kind đó. Webhook Paid → nhánh kích hoạt kỳ hạn (**không cộng credit, không ghi `credit_transactions`**, mẫu `InvoiceSettlement`) → outcome `SubscriptionActivated`.

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

**`POST /payment/admin/orders/{id}/refund`** ✅ **F18** (2026-07-19) — Hoàn tiền đơn mua credit. Auth `Roles="Admin"`. Req `{ reason (bắt buộc, 3–500), gatewayRef?, allowPartialClawback? }` → **200** `{ orderId, amountVnd, creditsPurchased, creditsClawedBack, clawbackCeiling, refundTransactionId?, refundedAt }`.
- Đơn `Paid → Refunded` (trạng thái MỚI) + bút toán `Refund` âm gắn `reverses_transaction_id` → bút toán mua gốc + thu hồi credit khỏi `remaining`. Người hoàn lấy từ **JWT**, không nhận từ body.
- **Thu hồi kẹp trần**, KHÔNG trừ thẳng: trần = `max(0, remaining − quà chưa tiêu)`, quà chưa tiêu = `max(0, free_credits_granted − tổng đã tiêu)` (quà tiêu TRƯỚC vì được cấp ngay lúc tạo ví). Hai lý do: (a) trừ quá tay → `remaining` âm → nổ CHECK `ck_credit_accounts_non_negative` → rollback → đơn kẹt `Paid` vĩnh viễn (hình dạng DB20/DB22); (b) credit `FreeGrant` không phải tiền khách trả nên hoàn tiền không được biến quà thành tiền mặt.
- Thu hồi thiếu → **409** kèm `clawbackPossible`/`clawbackCeiling`; gửi lại `allowPartialClawback=true` để chấp nhận. Ledger ghi phần trừ **THẬT** (−2 khi chỉ lấy được 2), không ghi cho khớp đơn — nếu không thì `remaining + reserved = Σ delta` gãy. Thu hồi 0 → **không** ghi ledger (delta=0 vi phạm CHECK), đơn vẫn Refunded + log lỗi.
- `reserved` KHÔNG bao giờ bị đụng (PAY-12 — không văng người đang thi).
- Idempotent: gọi lại → 200 nguyên trạng; khoá thật nằm ở UNIQUE `reverses_transaction_id`.
- **Phạm vi CỐ Ý hẹp**: chỉ `kind=CreditPack`. `InvoiceSettlement`/`Subscription*` → **400** (hoàn hoá đơn postpaid = mở lại kỳ đã chốt, `InvoiceStatus.Void` mới là trạng thái đúng; thu hồi kỳ thuê bao là nghiệp vụ riêng). ⇒ `InvoiceStatus.Void` vẫn là enum chết, có chủ đích.
- Lỗi: **404** không có đơn · **409** đơn chưa Paid / thu hồi thiếu / ví vừa đổi giữa chừng.
- ⚠ Ngoại lệ có chủ đích của **PAY-10**: mọi cơ chế TỰ ĐỘNG (webhook · polling · sweeper hết hạn) đều guard `status == Pending` ⇒ đơn Refunded nằm ngoài đường đi của chúng, webhook muộn không cộng lại credit vừa thu hồi (có test khoá).

**`GET /payment/admin/revenue?from=&to=&groupBy=day|month`** ✅ **F19** (2026-07-19) — Doanh thu theo kỳ. Auth `Roles="Admin"`. Kỳ **nửa mở `[from, to)`** (hai kỳ liền nhau không đếm trùng); thiếu tham số → 30 ngày gần nhất; mốc ép về UTC. → **200** `{ from, to, granularity, grossRevenueVnd, paidOrderCount, refundedVnd, refundedOrderCount, netRevenueVnd, byKind[], buckets[] }`.
- Doanh thu **gộp** đếm theo `paid_at` (đơn Paid); **tiền hoàn** đếm theo `refunded_at` — nếu đếm hoàn theo `paid_at` thì một khoản hoàn hôm nay đi ngược sửa doanh thu kỳ ĐÃ CHỐT. Kỳ có thể có `netRevenueVnd` âm — đúng bản chất kế toán.
- **Quà không bao giờ thành doanh thu** theo cấu trúc: báo cáo đọc `orders`, còn `FreeGrant`/`PromoGrant` chỉ ghi `credit_transactions` và không sinh đơn nào (có test khoá).
- Lỗi: **400** `from >= to` hoặc `groupBy` lạ.

**`POST /payment/admin/credits/grant`** ✅ **F20** (2026-07-19) — Cấp credit khuyến mãi. Auth `Roles="Admin"`. Req `{ ownerType, ownerId, credits (1–10000), note (bắt buộc) }` → **200** `{ ownerType, ownerId, creditsGranted, remainingCredits, transactionId }`.
- `remaining += N` + bút toán `PromoGrant +N` mang `granted_by` (lấy từ **JWT**, request DTO cố ý KHÔNG có trường khai người cấp) trong CÙNG transaction ⇒ bất biến sổ cái giữ nguyên.
- Ví chưa tồn tại → tạo qua `CreateAccountAsync`, tức **đi qua đúng đường cấp suất dùng thử F7** (PAY-14) ⇒ user mới được tặng quà thì ví sinh ra kèm cả 3 credit dùng thử. Tự INSERT ví ở đây sẽ im lặng tước mất suất đó.
- `credits <= 0` → **400**: bút toán delta=0 vi phạm CHECK, và "cấp số âm" sẽ là một đường TRỪ credit không có bút toán đảo — trừ credit phải đi đường hoàn tiền F18.
- Ví `Suspended` **VẪN** nhận được quà: PAY-12 chặn hành động tương lai (reserve), còn cộng tiền là chiều ngược lại — chặn nó thì không đền bù được cho đúng tài khoản đang tranh chấp. Cấp quà không gỡ lệnh đình chỉ.
- ⚠ **CHƯA có idempotency**: bấm hai lần = cấp hai lần. Khác webhook (có bộ retry tự động), đây là hành động người bấm; nếu cần thì thêm khoá idempotency do client cấp.

**`GET /payment/admin/credits/{ownerType}/{ownerId}`** ✅ **F20** — Số dư ví **bất kỳ** → `CreditAccount`. Ví chưa tồn tại → **200** 0 credit (đọc thuần, cùng quy ước `me/account`). Đây là đường DUY NHẤT để admin đọc ví người khác — `me/account` suy chủ ví từ JWT nên chỉ bao giờ nói về chính người gọi.

**`GET /payment/admin/credits/{ownerType}/{ownerId}/transactions`** ✅ **F20** — Sổ cái ví **bất kỳ**, cùng hợp đồng keyset với `me/credit-transactions`, **có thêm** `grantedBy`/`note`.

**`GET /payment/admin/ai-usage?from=&to=&groupBy=day|month`** ✅ **F22** (2026-07-19) — Tiêu thụ token + **chi phí AI** theo kỳ. Auth `Roles="Admin"`. Kỳ **nửa mở `[from, to)`**, thiếu tham số → 30 ngày gần nhất, mốc ép về UTC (mẫu F19). → **200** `{ from, to, granularity, totalCalls, promptTokens, outputTokens, totalTokens, totalCostUsd, byOperation[], buckets[], resourceUrls? }`.
- **`byOperation[]`** = tiêu thụ **theo endpoint** (`score` · `generate_questions` · `decide_next` · `text_to_speech` …) → trả lời "tiền đi đâu", không chỉ "hết bao nhiêu". Đây là thứ cho biết bật `SelfConsistencyN` hay thêm 2 tiêu chí (F12) đắt lên bao nhiêu.
- **`resourceUrls`** (F15) = `{ proposed, rejected, rejectedRate }` — tỉ lệ URL tài liệu do AI sinh bị allowlist tên miền loại. Trước F22 allowlist loại URL trong **im lặng**: nếu Gemini bịa domain 90% số lần thì không ai biết, và cũng không có cơ sở nào để nói allowlist 26 domain đang quá chặt hay quá lỏng. **`null` khi kỳ không có lượt sinh tài liệu** — `null` ≠ `0/0`, vì hiện "0% bị loại" là một khẳng định không có cơ sở.
- Lỗi: **400** `from >= to` hoặc `groupBy` lạ.

### Nội bộ — AIService → Payment — `X-Internal-Token`, **KHÔNG qua gateway** ✅ (F22)

**`POST /internal/ai-usage`** ✅ **F22** — AIService đẩy số liệu 1 lượt gọi LLM. Req `{ operation, model, promptTokens, outputTokens, totalTokens, resourceUrlsProposed?, resourceUrlsRejected? }` → **200** `{ id }`.
- **Vì sao Payment nhận chứ không phải AIService tự lưu:** GEN-4 cấm AIService ghi DB, nên số liệu đi qua **callback nội bộ** — đúng cơ chế GEN-4 đã dựng cho kết quả AI. Payment giữ bảng vì chi phí AI là câu hỏi **tiền** và chỉ có nghĩa khi đọc cạnh doanh thu (F19 cũng ở đây): "tháng này thu bao nhiêu, đốt bao nhiêu" phải trả lời được ở **một** chỗ. *(Các phương án đã loại — trả usage kèm response cho caller · endpoint `/metrics` in-memory · gom qua log — ghi trong `src/services/Isas.AIService/app/usage.py`.)*
- **Caller KHÔNG gửi tiền, chỉ gửi token + tên model.** Đơn giá do Payment giữ (`AiPricing`, USD/1 triệu token) và **snapshot lên từng dòng** (mẫu `Invoice.UnitPrice`) → Google đổi giá **không hồi tố** số liệu lịch sử. Để AIService gửi luôn số tiền thì đơn giá phải sống ở hai nơi và sẽ lệch nhau vào đúng ngày đổi giá.
- **Không bao giờ ép caller xử lý lỗi:** ghi hỏng → **202** `{ status: "dropped" }` + log, KHÔNG phải 500. Caller là AIService gọi ngay sau một lượt LLM **đã tốn tiền**; bắt nó retry/nổ ở đó là biến một tính năng **quan sát** thành đường làm answer `Failed` ⇒ mất credit (PAY-13). Phía AIService cũng đã nuốt lỗi — đây là lớp thứ hai.
- Token âm → **kẹp về 0** (không từ chối): số âm lọt vào sẽ **trừ** vào tổng chi phí, tức báo cáo sai theo hướng có lợi cho ta.
- Model không có trong bảng giá → dùng `AiPricing:Default` + **log cảnh báo** (KHÔNG ghi `cost = 0` — cost 0 làm báo cáo chi phí trông đẹp một cách sai sự thật).

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
→ 200 { "ownerType":0,"ownerId":"…","paymentMode":0,"status":0,                       // enum SỐ (khớp §DTO dòng 68 + mọi DTO Payment khác)
        "remainingCredits":48,"reservedCredits":2,"creditLimit":null,"periodUsage":null,
        "updatedAt":"2026-07-18T14:03:02Z" }

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
remaining_credits int          prepaid: số credit còn (reserve trừ NGAY — xem §State machine); ✅ F7 gồm cả credit dùng thử
reserved_credits int           đang giữ chỗ (Reserved chưa Consumed/Released)
free_credits_granted int       ✅ F7 — suất dùng thử ĐÃ TẶNG ví này (0 = chưa tặng / ví Org). Denormalize từ sổ cái (reason=FreeGrant); KHÔNG phải xô riêng — credit tặng nằm chung remaining_credits
credit_limit     int?          CHỈ Org/postpaid
period_usage     int?          CHỈ Org/postpaid — lượt đã dùng kỳ này
updated_at       timestamptz
                               UNIQUE (owner_type, owner_id)
                               CHECK ck_credit_accounts_non_negative: remaining_credits>=0 AND reserved_credits>=0 AND free_credits_granted>=0 AND (period_usage IS NULL OR period_usage>=0)  ✅ DB1 (2026-07-17) · mở rộng free_credits_granted ✅ F7
```

### `credit_reservations` — giữ chỗ theo session
```
id         uuid          PK
owner_type varchar(8)    enum: Org · User
owner_id   uuid          chủ ví bị giữ chỗ
session_id uuid          UNIQUE, ref lỏng → Interview (idempotency)
status     varchar(16)   enum: Reserved · Consumed · Released
created_at timestamptz
                         FK (owner_type,owner_id) → credit_accounts(owner_type,owner_id) Restrict  ✅ DB9 (2026-07-17)
```
> **✅ DB9 (2026-07-17) — FK nội-service:** `credit_reservations`/`credit_transactions`/`invoices` đều thêm FK composite `(owner_type,owner_id) → credit_accounts` (`OnDelete=Restrict`). `credit_accounts` thêm `HasAlternateKey(owner_type,owner_id)` = UNIQUE CONSTRAINT (Postgres FK không ref được unique-index). Hệ quả: `ReserveAsync` đọc ví TRƯỚC khi chèn reservation (FK cấm reservation mồ côi) — no-wallet vẫn →402 (PAY-5). Ref XUYÊN service (`session_id`/`order_id`) giữ Guid lỏng (GEN-2).

### `credit_transactions` — sổ cái
```
id         uuid          PK
owner_type varchar(8)    enum: Org · User
owner_id   uuid
order_id   uuid?         FK → orders
session_id uuid?         ref lỏng → Interview
delta      int           +/− (cộng pack / trừ lượt)
reason     varchar(16)   enum: Purchase (+pack) · Consume (−1/lượt khi Scored) · Refund (✅ F18 — admin hoàn đơn, delta ÂM) · FreeGrant (+N suất dùng thử lúc tạo ví User — ✅ F7, order_id/session_id đều null) · PromoGrant (✅ F20 — admin cấp quà, +N, có granted_by)
reverses_transaction_id uuid?  ✅ F18 — FK TỰ THAM CHIẾU → credit_transactions(id): bút toán mua bị đảo.
                          Chỉ set trên row Refund. UNIQUE LỌC (WHERE NOT NULL) = khoá idempotency chống
                          hoàn hai lần cùng một khoản mua (cùng lối UNIQUE(session_id) chặn double-reserve).
granted_by uuid?          ✅ F20 — `sub` của admin cấp quà (ref lỏng → Auth). Chỉ set trên row PromoGrant:
                          quà là loại credit DUY NHẤT không qua thanh toán và không do luật tự động, nên
                          nếu không ký tên thì không truy được nguồn.
note       varchar(500)?  ✅ F20 — lý do cấp quà.
created_at timestamptz
                          CHECK ck_credit_transactions_delta_nonzero: delta<>0  ✅ DB1 (2026-07-17)
                          INDEX ix_credit_transactions_owner_created (owner_type, owner_id, created_at DESC,
                            id DESC) ✅ F19 — phục vụ GET /me/credit-transactions (keyset DB8). Migration
                            F19 DROP index cũ (owner_type, owner_id): index mới có nó là TIỀN TỐ TRÁI và
                            không có filter nên phủ trọn FK lookup ⇒ giữ lại là index chết (tiền lệ DB31).
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

### `subscriptions` ✅ **F8 (2026-07-19) — dựng LẠI, lần này cùng đường tiêu thụ thật**
> *(Lịch sử: DB15 2026-07-17 DROP bảng + entity vì là **dead scaffold** — 0 query dùng, `SubscriptionService` là stub `NotImplementedException`. F8 tái tạo qua migration `AddSubscriptionsF8`, gắn liền chuỗi order → webhook → activate → gate ở reserve.)*
```
id             uuid pk
owner_type     varchar(8)    enum: Org (membership B2B) · User (Premium B2C)
owner_id       uuid          FK composite (owner_type, owner_id) → credit_accounts (Restrict, DB9)
package_id     uuid?         FK → product_packages (Restrict)
order_id       uuid?         FK → orders (Restrict) · UNIQUE filtered (order_id IS NOT NULL)
billing_cycle  varchar(16)   enum: Monthly · Annual   (suy từ package.duration_days, ngưỡng ≥180 ngày)
status         varchar(16)   enum: Active · Expired · Cancelled
started_at     timestamptz
expires_at     timestamptz   CHECK ck_subscriptions_period_positive (expires_at > started_at)
created_at     timestamptz
updated_at     timestamptz
```
**Index:** `ix_subscriptions_owner_active (owner_type, owner_id, expires_at) WHERE status='Active'` (đường nóng — MỌI lần reserve đều hỏi) · `ix_subscriptions_active_expires_at (expires_at) WHERE status='Active'` (sweeper) · unique `order_id`.

**Một lần mua = MỘT row** (append-one-per-order, KHÔNG update row cũ để kéo dài hạn): `UNIQUE(order_id)` chính là khoá idempotency của webhook — cùng cơ chế `UNIQUE(session_id)` của `credit_reservations`. **Gia hạn** = row mới bắt đầu từ `expires_at` xa nhất đang còn hiệu lực (mua sớm không mất ngày đã trả tiền). **"Đang có thuê bao"** = tồn tại row `status=Active` **AND** `expires_at > now` — CỐ Ý không phụ thuộc sweeper đóng dấu `Expired`.

> ⚠ Lệch shape cũ có chủ ý: dùng `subscriptions.order_id` thay `orders.subscription_id` — thuê bao được tạo Ở webhook (sau đơn), nên đặt khoá ở phía subscription vừa cho idempotency ở tầng DB vừa khỏi phải quay lại UPDATE đơn.

### `ai_usage_logs` ✅ **F22 (2026-07-19)** — token + chi phí mỗi lượt gọi LLM (migration `AddAiUsageLogsF22`)
```
id                            uuid pk
operation                     varchar(64)   đường gọi: score · generate_questions · decide_next · text_to_speech …
model                         varchar(64)   model THẬT SỰ chạy (TTS dùng model + bảng giá riêng)
prompt_tokens                 int
output_tokens                 int
total_tokens                  int           LẤY TỪ SDK, không phải prompt+output (Gemini tính cả token nội bộ)
input_price_per_million_usd   numeric(18,6) SNAPSHOT đơn giá lúc ghi
output_price_per_million_usd  numeric(18,6) SNAPSHOT đơn giá lúc ghi
cost_usd                      numeric(18,8) tính TỪ 2 cột trên, không đọc lại cấu hình
resource_urls_proposed        int?          F15 — chỉ lượt generate_lesson_theory; null = không áp dụng
resource_urls_rejected        int?          F15 — số URL bị allowlist tên miền loại
created_at                    timestamptz
```
**Index:** `ix_ai_usage_logs_created_at` · `ix_ai_usage_logs_operation_created_at` (mọi câu hỏi báo cáo lọc theo kỳ trước, rồi mới gộp theo operation).

**KHÔNG PHẢI BẢNG TIỀN CỦA NGƯỜI DÙNG** — đây là **chi phí vận hành**: không FK tới `credit_accounts`, không ghi `credit_transactions`, không đụng bất biến `remaining + reserved = Σ delta`. Và **cố ý KHÔNG có CHECK constraint nào**: bảng này được ghi bởi một đường best-effort, nên một CHECK "hợp lệ" (vd `total_tokens > 0`) sẽ biến dữ liệu đo hơi lạ thành exception — đúng hình dạng lỗi **DB22**. Giá trị vô lý bị **kẹp** ở `AiUsageService.RecordAsync`, nơi hỏng thì chỉ mất một dòng thống kê.

**Đơn giá snapshot trên từng dòng** (mẫu `Invoice.UnitPrice`): giá là dữ liệu **sẽ đổi**. Nếu chỉ lưu token rồi nhân giá hiện hành lúc xem báo cáo thì mọi số liệu **lịch sử** tự động sai đi mỗi lần Google đổi giá — và sai trong im lặng.

⚠ **Bảng nhiều dòng nhất service** (1 dòng/lượt gọi LLM, nhân `SelfConsistencyN` nếu bật) và **CHƯA có job purge** — cùng nhóm với `refresh_tokens`/outbox ở **DB28**. Cần retention trước khi lưu lượng thật lớn.

### `credit_reservations.funded_by` ✅ **F8** — `varchar(16)` enum `Credit` (default) · `Subscription`
Nguồn chi trả của một chỗ giữ, **chốt MỘT LẦN lúc reserve**, không bao giờ đọc lại từ trạng thái thuê bao.

**Vì sao cần cột này thay vì hỏi lại "còn thuê bao không":** chỗ giữ kiểu `Subscription` không trừ cột số dư nào; nếu `Release` quyết định nhánh theo trạng thái *hiện tại* thì một thuê bao **hết hạn giữa buổi** sẽ đẩy release sang nhánh prepaid `remaining+1` ⇒ **đúc ra một credit trả tiền chưa từng được mua**. Chốt tại nguồn ⇒ nghịch đảo luôn khớp chiều thuận, và đó cũng chính là cách hiện thực PAY-12 "không văng người đang thi".

**Bất biến kèm theo (BẮT BUỘC):** bất biến DB4/DB21 thu hẹp thành
`reserved_credits = count(reservations WHERE status='Reserved' AND funded_by='Credit')`.
Bỏ vế `funded_by` ⇒ `CreditReservationReconciler` đếm cả chỗ giữ của subscriber → bơm `reserved_credits` → phá `remaining + reserved = Σ delta` → consume/release trừ xuống âm → nổ CHECK → rollback → reservation kẹt `Reserved` → nack-requeue vô hạn. Tức là tái tạo DB21 qua cửa khác.

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

SUBSCRIPTION (F8 — chủ ví còn thuê bao lúc reserve ⇒ funded_by='Subscription'):
reserve : UPDATE … SET updated_at = now()
          WHERE owner=… AND status='Active'   ← KHÔNG đổi số dư; 0 row ⇒ ví Suspended ⇒ 402 (PAY-12)
consume : (chỉ flip reservation → Consumed)   ← KHÔNG đổi số dư, KHÔNG ghi ledger
release : (chỉ flip reservation → Released)   ← KHÔNG đổi số dư, KHÔNG ghi ledger
```
- **Reserve trừ `remaining` NGAY** (không chỉ tăng `reserved`) → 2 reserve song song không cùng vượt check ⇒ **chống double-spend**. `remaining` = tiêu được thật; `reserved` = đang giữ.
- **Bất biến audit (prepaid):** `remaining_credits + reserved_credits = Σ(credit_transactions.delta)` tại mọi thời điểm (reserve/release không ghi ledger nên bảo toàn tổng; Purchase/Consume/Refund mới đổi tổng). Job đối soát định kỳ so 2 vế → lệch = có bug bút toán.
- **Postpaid** (không có `remaining`): điều kiện reserve là `period_usage + reserved + 1 ≤ credit_limit` (và account `Active`, không có hóa đơn `Overdue`). **Consume mới cộng `period_usage`** (không phải reserve) → bỏ ngang/release **không** dồn nợ; `period_usage` chính là nguồn snapshot ra `invoice.interview_count` cuối kỳ.
- **✅ F8 (2026-07-19) — gate "unlimited" ở đường reserve:** chủ ví có thuê bao còn hạn ⇒ chỗ giữ mang `funded_by='Subscription'` và **KHÔNG đụng cột số dư nào ở CẢ BA bước**. Bất biến audit được giữ **bằng cách không động vào gì**, chứ không bằng bút toán bù: vế trái (`remaining+reserved`) và vế phải (`Σ delta`) đều đứng yên. *(Các phương án khác đều hỏng: "chỉ `reserved+1`" làm vế trái tăng một mình ⇒ gãy ngay từ reserve; "ghi ledger +1 rồi −1" đúc credit khống vào sổ; "vẫn trừ remaining" thì thuê bao vẫn 402 khi ví rỗng = mất tính năng.)* **Kéo theo:** bất biến DB4 thu hẹp thành `... AND funded_by='Credit'` (xem §DB `credit_reservations.funded_by`). Ví `Suspended` vẫn chặn reserve mới (PAY-12) — thuê bao không mua được quyền đi vòng qua lệnh đình chỉ.
- **✅ DB4 (2026-07-17) — reconciler bất biến `reserved_credits == count(credit_reservations WHERE status=Reserved cùng owner)`:** `CreditReservationReconciler` (BackgroundService, `Reconcile:Enabled/ScanIntervalSeconds`, mặc định on/120s) quét định kỳ TỪ PHÍA `credit_accounts` → mỗi ví `CountAsync(Reserved)` → lệch → `ExecuteUpdate reserved_credits = count`. Sửa drift do crash giữa reserve/consume/release. Guard chống âm (count≥0 + CHECK `ck_credit_accounts_non_negative` DB1). **Scope core Payment-DB thuần** (reservation có sẵn owner); phần "reservation `Reserved` mà session Interview đã terminal (event settlement rớt)" = **DB4b** (backlog — `SettlementReconciler` bên Interview re-publish event; **DB2** outbox sẽ diệt gốc mất-event).
- **✅ DB18 (2026-07-17) — orphan-reservation compensation (khép DB4b):** `OrphanReservationReconciler` (BackgroundService, `OrphanReconcile:Enabled/ScanIntervalSeconds(120)/OrphanThresholdMinutes(10)/BatchSize`) quét `credit_reservations WHERE Status=Reserved AND CreatedAt < now−10'` → gọi Interview `POST /internal/sessions/exists` (batch, qua `InterviewSessionClient` — **lần đầu Payment→Interview**, cần env `Interview__BaseUrl`) → session **KHÔNG tồn tại** (crash reserve↔insert lúc Start) → `ReleaseAsync(session_id)` (idempotent/absorbing PAY-11). **AN TOÀN:** chỉ release khi Interview xác nhận DƯƠNG; Interview down/lỗi → **skip, KHÔNG release mù**. Đóng hở duy nhất còn lại (process-crash giữa reserve↔insert mà try/catch release không cover). Bao B2B(Org)+B2C(User)+lesson (owner từ reservation). Không set `Interview__BaseUrl` → call ném → reconciler safe-skip mỗi vòng.
- **✅ R1 (2026-07-20) — lưới cuối cho chỗ giữ của session ĐÃ TERMINAL:** DB18 chỉ dọn ca session **không tồn tại**; ca *session tồn tại nhưng đã terminal mà reservation vẫn `Reserved`* trước R1 **không ai dọn** → rò credit hai chiều (đo được trên production 2026-07-20: 1 ca `SessionAbandoned` = user mất oan 1 credit · 2 ca `Scored` = org/user được buổi miễn phí). Nguồn gốc là mất event settle (binding RabbitMQ vắng một cửa sổ, message bị topic exchange vứt im lặng trong khi outbox đã đóng dấu `published_at`) — lỗ cần vá là **thiếu lưới cuối**. Interview `/internal/sessions/exists` nay trả thêm `states[{sessionId,status}]` (**ADDITIVE**, status = tên enum dạng string, GEN-2); reconciler phân nhánh:

  | Interview trả | Hành động | Vì sao |
  |---|---|---|
  | session KHÔNG tồn tại | `ReleaseAsync` | DB18, giữ nguyên |
  | `Scored` | **`ConsumeAsync`** | buổi đã được AI chấm ⇒ PAY-1/PAY-13, phải thu |
  | `SessionAbandoned` · `Failed` | `ReleaseAsync` | bỏ ngang / lỗi sinh câu hỏi (E7, BK12) |
  | `GeneratingQuestions`·`Ready`·`InProgress`·`Completed`·`Scoring` | SKIP | đang bay hợp lệ |
  | lạ / thiếu / rỗng | **SKIP** + log | fail-safe, KHÔNG đoán |

  **🔴 AN TOÀN — nhánh CONSUME chặt hơn nhánh RELEASE** (trước R1 sai thì hoàn nhầm, còn cứu được; nay sai là **mất tiền người dùng**, không tự phục hồi): ①chỉ consume khi khẳng định **DƯƠNG** `Scored` — whitelist một-phần-tử, **KHÔNG có nhánh `default → Consume`**; ②công tắc riêng `OrphanReconcile:ConsumeTerminalScored` (mặc định `true`) tắt được nhánh trừ tiền bằng env mà **vẫn giữ nhánh release chạy**, khỏi rollback image; ③mốc `OrphanReconcile:ConsumeFromUtc` — **không cấu hình ⇒ lấy mốc KHỞI ĐỘNG reconciler** ⇒ "chỉ từ nay về sau"; chỗ giữ `Scored` **cũ hơn mốc → SKIP + log Warning, KHÔNG release** (release buổi đã chấm = tặng buổi miễn phí, đúng bug đang sửa) → tồn đọng để **người** đối soát tay (OPS2), vì đó là hệ quả sự cố hạ tầng của ta chứ không phải lỗi người dùng.
  ⚠ **Mốc tuyệt đối, KHÔNG phải cửa sổ trượt:** tồn đọng đo được chỉ ~1 ngày tuổi nên "bỏ qua reservation quá N ngày" vẫn nuốt chúng.
  ⚠ **Hệ quả phải nhìn thấy trong log:** restart dịch vụ đẩy mốc mặc định tiến lên ⇒ chỗ giữ sinh ngay trước restart có thể không bao giờ được consume (reconciler log mốc + nguồn mốc lúc khởi động, và log từng ca bị bỏ).
  ⚠ **`ExistingIds` vẫn là nguồn chân lý DUY NHẤT cho "session có tồn tại không"** — `States` chỉ làm giàu và **có thể RỖNG khi Interview còn image cũ**. Suy tồn-tại từ `States` sẽ khiến Payment mới + Interview cũ hiểu nhầm "không session nào tồn tại" → **release cả session đang thi**. Lệch phiên bản image là chuyện **đã xảy ra** trên hệ này. **KHÔNG migration.**

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

### Suất dùng thử B2C — ✅ **F7** (2026-07-19)
- **3 credit tặng khi TẠO ví của một `User`** (`Billing:FreeTrialCredits`, mặc định `3`, đặt `0` = tắt hẳn). **Chỉ `owner_type=User`** — ví Org không có suất dùng thử (B2B đi ví Org, BC-1); Org chưa có ví vẫn **402** như trước.
- **Cấp ở đúng MỘT chỗ: `CreateAccountAsync`, ngay trong câu INSERT tạo ví** (cả số dư lẫn bút toán sổ cái, cùng một `SaveChanges`). Vì vậy nó phủ **cả hai** đường tạo ví: webhook Paid lần mua đầu **và** lần `reserve` đầu tiên của user chưa từng mua (đường mới của F7). Tách phần cấp thành một `UPDATE` tiếp sau sẽ khiến **bên thua race cấp lần hai** → credit tặng vô hạn; `UNIQUE(owner_type, owner_id)` chính là thứ bảo đảm "một ví = một suất".
- **KHÔNG phải xô riêng.** Credit tặng nằm chung `remaining_credits` và tiêu theo đúng luật hiện hành (reserve/consume/release, PAY-4/PAY-11/PAY-13) — `ReserveAsync`/`ConsumeAsync`/`ReleaseAsync` **không đổi một dòng nào**. Mỗi lần cấp ghi **1 bút toán `FreeGrant +N`** (`order_id`/`session_id` null) nên bất biến **`remaining + reserved = Σ delta` vẫn đúng** ⇒ credit tặng bốc hơi do drift vẫn bị phát hiện y như credit khách trả tiền. Một "xô free không sổ sách" sẽ mất chính cái máy dò đó.
- **Ví đã tồn tại KHÔNG bao giờ được top-up** (kể cả ví cũ có `free_credits_granted = 0` từ trước F7). Thêm nhánh "chưa tặng thì cấp bù" = đường tặng credit vô hạn.
- **Không backfill.** Ví chỉ tồn tại với người **đã trả tiền**, nên `UPDATE ... WHERE owner_type='User'` sẽ tặng đúng nhóm khách đã trả tiền và **không chạm** user nào đang kẹt 402 — nhóm đó chưa có row nào trong bảng. Họ nhận suất dùng thử ở lần reserve đầu tiên.
- ⚠ **Rủi ro đã biết, chưa xử:** 1 email = 3 lượt, mà đăng ký **không xác minh email** ⇒ lạm dụng bằng email dùng-một-lần là có thật. Cần quyết định sản phẩm (xác minh email / giới hạn theo thiết bị), không thuộc phạm vi F7.

### Postpaid (trả sau)
- **Chỉ org được PlatformAdmin DUYỆT** mới bật `Postpaid` (cần **pháp nhân/MST** để xuất hóa đơn + đòi nợ). Mặc định org mới = `Prepaid`.
- Chặn reserve khi **`nợ + giữ ≥ credit_limit`** hoặc **có hóa đơn `Overdue`**.
- Cuối kỳ: chốt `period_usage` → tạo `invoice` (`interview_count × unit_price`) → org **tất toán qua PayOS** (`orders.kind = InvoiceSettlement`) → reset kỳ.
- **Rủi ro nợ xấu:** AI cost đã đốt thật → hạn mức + duyệt + đình chỉ là bắt buộc.

### Đình chỉ / hết hạn (exception)
- Hết credit (prepaid) / chạm hạn mức / hóa đơn quá hạn (postpaid) → admin đặt account `status = Suspended` (§State machine) → **chỉ chặn HÀNH ĐỘNG TƯƠNG LAI** (mời mới, reserve mới trả 402/403). **KHÔNG văng ứng viên đang thi** — reservation đã giữ chỗ nên **in-flight được bảo vệ**.

> Nguyên tắc tiền bạc: PaymentService **riêng**, không service nào khác ghi thẳng bảng payment — chỉ qua API nội bộ.
