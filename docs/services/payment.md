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
### Public/Org (JWT; `org_id` từ membership)
> **B2C:** cùng các endpoint mua pack / xem số dư / polling đơn, nhưng chủ ví = **User** (`owner_id` = `sub` trong JWT) thay cho org; chỉ Prepaid, không có hóa đơn/postpaid.
| Method | Path | Auth | Mô tả |
|---|---|---|---|
| GET | `/payment/package` · `/{id}` | — | Gói prepaid đang bán |
| POST/PUT/DELETE | `/payment/package…` | **PlatformAdmin** | CRUD gói |
| POST | `/payment/admin/orgs/{orgId}/postpaid` | **PlatformAdmin** | **Duyệt postpaid** + đặt `credit_limit` (cần MST) |
| POST | `/payment/admin/orgs/{orgId}/suspend` | **PlatformAdmin** | **Đình chỉ** org (nợ xấu / quá hạn) |
| GET/PUT | `/payment/admin/unit-price` | **PlatformAdmin** | **Đơn giá 1 lượt** (tính hóa đơn postpaid) |
| GET | `/payment/admin/transactions` | **PlatformAdmin** | Xem giao dịch / hóa đơn toàn hệ |
| POST | `/payment/admin/orgs/{orgId}/credits/adjust` | **PlatformAdmin** | **Cấp/hoàn credit thủ công** (ghi `credit_transactions(Refund)`) — *phase 2* |
| POST | `/payment/order` | OrgAdmin | Mua pack credit. `{ packageId }` → order + **link PayOS** `{ orderCode, checkoutUrl }` |
| GET | `/payment/order/{id}` · `/my-orders` | Org | Chi tiết / lịch sử đơn |
| **GET** | **`/payment/order/{id}/status`** | Org | **FE active-polling**: nếu server chưa nhận webhook → **server gọi PayOS đối soát ngay** rồi trả trạng thái |
| GET | `/payment/me/account` | Org | Số dư org: `mode`, `remaining`, `reserved`, `credit_limit`, `nợ kỳ này` |
| GET | `/payment/me/invoices` · `/{id}` | Org | Hóa đơn (postpaid) |
| POST | `/payment/invoices/{id}/pay` | OrgAdmin | Tất toán hóa đơn → **link PayOS** |
| POST | `/payment/webhook/payos` | 🔒 chữ ký | **Webhook PayOS**, không qua gateway, verify checksum |

### Nội bộ — Campaign/Interview → Payment — **`X-Internal-Token`, KHÔNG qua gateway**
| Method | Path | Mô tả |
|---|---|---|
| POST | `/internal/credits/reserve` | `{ ownerType, ownerId, sessionId }` → giữ 1 chỗ (B2B: owner=Org do Campaign gọi; B2C: owner=User do Interview gọi). Hết hạn mức/số dư → từ chối |
| POST | `/internal/credits/consume` | `{ ownerType, ownerId, sessionId }` → biến reservation thành trừ thật (gọi khi `SessionScored`) |
| POST | `/internal/credits/release` | `{ ownerType, ownerId, sessionId }` → nhả chỗ giữ (bỏ ngang / lỗi hệ thống) |

> Có thể cho **PaymentService consume/release tự động bằng cách lắng nghe event `SessionScored`/`SessionAbandoned`** thay vì Campaign gọi — chốt khi build.

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

### `credit_accounts` — ví của chủ sở hữu (Org hoặc User)
`id` · `owner_type` (`Org`/`User`) · `owner_id` (ref lỏng → Auth) · **unique (`owner_type`, `owner_id`)** · `payment_mode` (`Prepaid`/`Postpaid`; **User luôn `Prepaid`**) · `remaining_credits` (prepaid) · `reserved_credits` (đang giữ chỗ) · `credit_limit` int? (**chỉ Org/postpaid**) · `period_usage` (**chỉ Org/postpaid**, lượt đã dùng kỳ này) · `updated_at`.

### `credit_reservations` — giữ chỗ theo session
`id` · `owner_type` · `owner_id` (chủ ví bị giữ chỗ) · `session_id` (**unique**, ref lỏng → Interview) · `status` (`Reserved`/`Consumed`/`Released`) · `created_at`.

### `credit_transactions` — sổ cái
`id` · `owner_type` · `owner_id` · `order_id?` · `session_id?` · `delta` (+/−) · `reason` (`Purchase`/`Consume`/`Refund`) · `created_at`.

### `product_packages`
`id` · `name` · `type` (`OneTime` / `Subscription` *(phase 2)*) · `price_vnd` · `interview_credits` · `duration_days?` · `is_active`.

### `orders`
`id` · `owner_type` · `owner_id` · `kind` (`CreditPack` / `InvoiceSettlement`) · `package_id?` · `invoice_id?` · `amount_vnd` · `payos_order_code` bigint **UNIQUE** (time+random, xem rules) · `status` (`Pending`/`Paid`/`Failed`/`Expired`/`Cancelled`) · `expired_at` · `paid_at?` · `created_at`.

> `InvoiceSettlement` (tất toán hóa đơn postpaid) **chỉ có ở owner = Org**.

### `payment_transactions`
`id` · `order_id` (1–1) · `gateway` (`payos`) · `gateway_txn_id?` · `status` · `raw_webhook_payload?` (đối soát) · `created_at`.

### `invoices` — postpaid, theo kỳ
`id` · `org_id` · `period_start`/`period_end` · `interview_count` · `unit_price_vnd` · `amount_vnd` · `status` (`Issued`/`Paid`/`Overdue`) · `issued_at` · `due_at` · `paid_at?`.

### `subscriptions` *(phase 2)*
`id` · `org_id` · `order_id` · `package_id` · `status` · `started_at`/`expires_at`.

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
