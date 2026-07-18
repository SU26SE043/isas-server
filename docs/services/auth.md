# AuthService

> Định danh & phát JWT cho toàn hệ. Code: `src/services/Isas.AuthService`. DB: `isas`. Gateway: `/api/v1/auth`.
> Quy ước chung (snake_case, JWT key, ...): [../architecture.md](../architecture.md) §5. Phân việc: [../work-division.md](../work-division.md).

## Vai trò
- Đăng ký / đăng nhập (email + mật khẩu, liên kết **Google OAuth**), phát **JWT access + refresh token**, profile.
- **3 platform role đã có: `Candidate` / `Employer` / `Admin`** (gắn vào claim JWT). `Admin` ở đây = **PlatformAdmin** (quản trị hệ thống: duyệt postpaid, quản gói), **khác** org-admin bên dưới.
- `register` **auto gán `Candidate`**; **`register-org` cấp role `Employer`** + tạo org + OrgAdmin ✅ A3.
- Các service khác **không gọi Auth lúc chạy** — validate JWT **offline** bằng chung key.

## Organization & phân quyền nội bộ (multi-tenant) 🟢 core A1–A3 xong; RBAC ✅ A4 (HrMember→403 billing) · A5 (`[Authorize(Roles)]` mọi service, v22)
B2B bán cho **doanh nghiệp**, không phải cá nhân → cần khái niệm **tổ chức**:
- Một **Organization** (`org_id`) = 1 doanh nghiệp; **billing/credit gắn org** (xem [payment.md](payment.md)), **campaign gắn `org_id`**.
- **Role nội bộ org** (claim kèm trong JWT): **`OrgAdmin`** (mua gói/trả tiền/xem billing, quản thành viên) vs **`HrMember`** (tạo & quản campaign, **không** xem billing).
- **Phase 1 (capstone):** data model có `org_id` + `org_role` từ đầu. **✅ A6 (vòng 19):** OrgAdmin **thêm HrMember** vào org (`POST /auth/org/members` passwordless + `GET` list) → org **nhiều thành viên** (HrMember login mang `org_role=HrMember` → A4 chặn billing). **✅ A6b (vòng 21):** `PATCH /auth/org/members/{userId}` đổi role + `DELETE` xoá thành viên (guard **OrgAdmin cuối cùng** + **không tự xoá mình**) + cột `org_members.joined_at` thật (thay proxy `User.CreatedAt`). **Còn phase 2:** mời qua **email invitation** (nay tạo trực tiếp) · **attach account có sẵn** (nay dup email→409).

> **Admin KHÔNG phải service riêng.** Chức năng PlatformAdmin = endpoint **admin-gated** nằm trong service sở hữu dữ liệu — **Payment**: CRUD gói, đơn giá, duyệt/đình chỉ postpaid, xem giao dịch, cấp/hoàn credit; **Auth**: cấp role, quản tổ chức (verify MST khi duyệt postpaid) — cộng **1 FE admin dashboard**. **Giám sát/thống kê nền tảng** (#org · #campaign · #lượt phỏng vấn · doanh thu) = dashboard tổng hợp từ các service (*phase 2*). Không thêm AdminService (tránh coupling + phá Engine+Orchestrator).

## API — `/api/v1/auth`

> **Quy ước:** Base public `/api/v1/auth/*` (gateway → service `/auth/*`). Auth: **JWT Bearer**; `—` = public. **Kiểu dữ liệu:** `uuid` · `string` · `int` · `bool` · `datetime` (ISO-8601) · `enum(string)` · `T[]` · `?` = optional/nullable. Mã lỗi chung: [../architecture.md](../architecture.md) §6. *(🔜 = phần admin-gated Org/role chưa build — A4/A5; core Organization A1–A3 đã xong.)*

### Schemas (DTO)

```
AuthResponse {
  accessToken:  string                 // JWT (mang role; + org_id, org_role nếu thuộc org ✅ A2)
  refreshToken: string
  expiresAt:    datetime               // hạn của access token
}

RefreshTokenResponse {
  refreshToken: string
  expiresAt:    datetime
}

UserResponse {
  id:        uuid
  fullName:  string
  email:     string
  location:  string
  title:     string
  createdAt: datetime
  role:      enum(string)              // Candidate·Employer·Admin
}
```

### Endpoints

**`POST /register`** — Đăng ký (role mặc định `Candidate`). Public.
- Req: `{ email: string, password: string, fullName: string }` → Res **`200`** `AuthResponse`. Lỗi: **409** (email đã tồn tại — body `{ error }`) · **400** (mật khẩu yếu).

**`POST /register-org`** — Đăng ký tổ chức (✅ A3). Public. Tạo user role **`Employer`** + `Organization` + `OrgMember(OrgAdmin)`.

**`POST /auth/org/members`** — ✅ **A6** (chỉ OrgAdmin, org_role claim ≠OrgAdmin/thiếu org_id→403). Req `{email, fullName}` → tạo User(`Employer`) passwordless + `OrgMember(HrMember, org_id=caller)` → **201** member info. Email đã có account→**409**. HR đặt mật khẩu qua forgot/reset.
**`GET /auth/org/members`** — ✅ **A6** (OrgAdmin) → list thành viên org (email/org_role/joinedAt **thật** từ `joined_at`, order theo `joined_at` — ✅ A6b).
**`PATCH /auth/org/members/{userId}`** — ✅ **A6b** (chỉ OrgAdmin). Req `{orgRole}` đổi `OrgAdmin↔HrMember` → **200**. role sai→**400**; hạ cấp **OrgAdmin cuối cùng** của org→**409**; không phải member→**404**; caller ≠OrgAdmin/khác org→**403**.
**`DELETE /auth/org/members/{userId}`** — ✅ **A6b** (chỉ OrgAdmin) → **hard-remove** row `org_members` (account `User` **giữ nguyên**, chỉ gỡ tư cách thành viên org) → **204**. **Không tự xoá mình**→**400**; xoá **OrgAdmin cuối**→**409**; không phải member→**404**; caller ≠OrgAdmin/khác org→**403**. *(Ratify: `org_members` **không soft-delete** — bảng cascade theo user, không có `is_deleted`; nếu cần audit/khôi phục → phase 2 thêm audit.)*
- Req: `{ email: string, password: string, fullName: string, orgName: string, taxCode: string? }` → Res **`200`** `AuthResponse` (token mang `org_id`+`org_role`). Lỗi: **409** (email đã tồn tại — body `{ error }`) · **400** (mật khẩu yếu / `orgName` rỗng).

**`POST /login`** — Đăng nhập. Public.
- Req: `{ email: string, password: string }` → Res **`200`** `AuthResponse`. Lỗi: **400/401** (sai thông tin).

**Đăng nhập Google (OAuth) — Public.** Là **điều hướng cả trang**, không phải XHR: FE chỉ `window.location.href = {gateway}/api/v1/auth/login-google`, không fetch.

- **`GET /login-google?returnUrl`** — challenge Google. `redirect_uri` gửi Google dựng từ **`Gateway:PublicBaseUrl`** (config server): gateway strip `/api/v1` nên URL handler tự dựng sẽ thiếu tiền tố + mang host nội bộ → 404 ở edge.
- **`GET /signin-google`** — **CallbackPath của handler Google** (không phải action MVC). Đây là URI phải khai trên Google Cloud Console: `{Gateway:PublicBaseUrl}/auth/signin-google`. Phải **khác** route action bên dưới — middleware remote-auth chạy trước MVC và short-circuit đúng path nó giữ.
- **`GET /login-google-callback?returnUrl&remoteError`** — action MVC, đích cuối vòng OAuth. **Trả `302`**, KHÔNG trả JSON (người dùng đang ở điều hướng cả trang → đáp xuống JSON thô thì app Angular không bao giờ chạy lại để nhận token):
  - Thành công → `{Frontend:BaseUrl}/auth/google/callback#accessToken=…&refreshToken=…&expiresAt=…[&returnUrl=…]`
  - Thất bại → `{Frontend:BaseUrl}/auth/google/callback#error=<remote_error|no_login_info|login_failed>`

Token đi trong **fragment** (không phải query): fragment **không được trình duyệt gửi lên server** → không lọt access log / header `Referer`.

**Bảo mật đích redirect:** base URL LUÔN từ config server (`Frontend:BaseUrl` / `Gateway:PublicBaseUrl`). `returnUrl` do client truyền chỉ được chấp nhận khi là **đường dẫn tương đối** (bắt đầu `/`, không `//`, không `/\`, không scheme, không ký tự điều khiển) rồi ghép sau base đã cấu hình — nhận host từ client = open-redirect làm rò token.

**Account linking:** email Google trùng account mật khẩu sẵn có → **liên kết** external login vào account đó (`AddLoginAsync`) rồi đăng nhập, KHÔNG tạo user thứ hai. Chưa có account → tạo mới **passwordless** + role `Candidate` (AUTH-1; luồng này không mở đường Employer/org).

**Config bắt buộc:** `Authentication:Google:ClientId/ClientSecret` · `Frontend:BaseUrl` · `Gateway:PublicBaseUrl`.

**`POST /refresh`** — Làm mới token. Public.
- Req: `{ refreshToken: string }` → Res **`200`** `RefreshTokenResponse`. Lỗi: **401** (token hết hạn / thu hồi / quá **cửa sổ ân hạn** bên dưới).
- **Cửa sổ ân hạn xoay vòng** (`Jwt:RefreshTokenGraceSeconds`, mặc định **60s**, `0` = tắt): token vừa bị xoay vòng vẫn refresh được thêm ngần đó giây — server đi theo `replaced_by` tới token **còn sống** ở cuối chuỗi và xoay tiếp, trả cặp token mới. Mốc đo là `created_at` của token thay thế (không cần cột `revoked_at`). Token bị thu hồi **thẳng tay** (đăng xuất / đổi quyền — `replaced_by` NULL) **KHÔNG** hưởng ân hạn, chết ngay.
- *Vì sao:* mỗi tab giữ refresh token riêng nhưng chung một phiên; thu-hồi-tức-thì làm tab đến muộn ăn 401 → đăng xuất oan (mở 2 tab là dính; quay về từ PayOS gần như luôn tạo tab thứ hai). *Đánh đổi:* thu-hồi-tức-thì chính là cơ chế **phát hiện token bị đánh cắp** (reuse detection) — ân hạn làm yếu nó trong đúng cửa sổ đó, nên giữ NGẮN.

**`POST /logout`** — Thu hồi **MỌI** refresh token của user đang đăng nhập. Auth (`Candidate·Employer·Admin`).
- Req: `{ refreshToken: string }` (giữ hợp đồng cũ; phạm vi thu hồi lấy theo claim `sub`, **không** theo token gửi kèm) → Res **`204`**. Lỗi: **401**.
- Thu hồi đúng 1 token thì tab khác vẫn gia hạn phiên tiếp → "đã đăng xuất" mà phiên vẫn sống. ⚠ **access token đang lưu hành KHÔNG thu hồi được** (validate offline — GEN-3) nên còn hợp lệ tới hết TTL (**15'**); **FE phải tự xoá token khỏi storage** khi đăng xuất (đã làm: `AuthStore.logout()` gọi `clearSession()` trước khi gọi API).

**`GET /me`** — Profile. Auth. → Res **`200`** `UserResponse`. Lỗi: **401**.
**`PUT /me`** — Cập nhật profile. Auth.
- Req: `{ fullName: string?, location: string?, title: string? }` → Res **`200`** `UserResponse`. Lỗi: **401**.

**`POST /forgot-password`** `{ email: string }` → gửi OTP · **`POST /verify-otp`** `{ email: string, otp: string }` · **`POST /reset-password`** `{ email: string, newPassword: string }`. Public. Lỗi: **400** (OTP sai/hết hạn).

**🔜 Admin (PlatformAdmin) — quản trị Org/role chưa build (A4/A5):**
- **`POST /auth/admin/users/{id}/roles`** — gán/thu platform role (vd nâng user → `Employer`).
- **`GET/POST /auth/admin/orgs…`** — xem / duyệt / khóa tổ chức (verify MST khi duyệt postpaid).
- *(✅ `register-org` → tạo `Organization` + `OrgAdmin`, JWT mang `org_id`+`org_role` — A1/A2/A3 xong. ✅ A4 HrMember→403 billing · ✅ A5 `[Authorize(Roles)]` mọi service (v22): `OrgMembers`→`Employer`, auth-entry `[AllowAnonymous]` tường minh; `RoleClaimType=ClaimTypes.Role` khớp mọi service.)*

### Request/Response mẫu (luồng chính)
```
POST /api/v1/auth/register-org
{ "email":"hr@acme.vn", "password":"S3cret!2026", "fullName":"Nguyễn HR", "orgName":"ACME JSC", "taxCode":"0312345678" }
→ 200  { "accessToken":"eyJ…", "refreshToken":"f3a1…", "expiresAt":"2026-06-30T09:15:00Z" }
        // accessToken claims: sub, role="Employer", org_id, org_role="OrgAdmin"

POST /api/v1/auth/login    { "email":"hr@acme.vn", "password":"S3cret!2026" }  → 200 AuthResponse
POST /api/v1/auth/refresh  { "refreshToken":"f3a1…" }   → 200 RefreshTokenResponse  (token cũ revoke + replaced_by=token mới)
```

### Validation (đầu vào)
| Field | Ràng buộc |
|---|---|
| `email` | bắt buộc; format email; chuẩn hoá `normalized_email` **UNIQUE** (trùng → **409**) |
| `password` | bắt buộc; theo `PasswordOptions` Identity (độ dài ≥ 6, chữ+số…); **null chỉ** khi user Google-only |
| `fullName` | bắt buộc khi `register`/`register-org` |
| `orgName` | bắt buộc (register-org), non-empty (trim) |
| `taxCode` | optional; cần khi **duyệt postpaid** (verify MST) |
| `otp` | 6 số, TTL ngắn (~5'); sai/hết hạn → 400 |
| `refreshToken` | bắt buộc; `is_revoked=false` + `expires_at>now` |

### Bảng mã lỗi (đặc thù — mã chung [../architecture.md](../architecture.md) §6)
| Mã | Khi nào |
|---|---|
| 400 | mật khẩu yếu (policy) · OTP sai/hết hạn · `orgName` rỗng |
| 401 | login sai email/mật khẩu · refresh hết hạn/đã revoke/quá cửa sổ ân hạn · thiếu/sai Bearer |
| 403 | role/`org_role` không đủ quyền (vd `HrMember` gọi admin/billing — A4) |
| 409 | **email đã tồn tại** (`register` · `register-org` · `POST /auth/org/members` — thống nhất một mã cho "tài nguyên đã tồn tại") · (admin) tạo org trùng / gán role mâu thuẫn |
| 423 | tài khoản **lockout** (quá `access_failed_count`) *(nếu bật `LockoutOptions`)* |

## Luồng (sequence)

**Đăng ký tổ chức (`register-org` → A3):**
```
FE ─POST /register-org─► Auth
      ├─ validate (email UNIQUE · password policy · orgName)
      ├─ tạo user(role=Employer) + Organization(org_id, tax_code) + OrgMember(OrgAdmin)   [1 transaction]
      ├─ phát JWT access (claims: sub, role, org_id, org_role) + refresh_token (rotation)
      └─► 200 AuthResponse
```

**Login + Refresh rotation (chống reuse):**
```
FE ─POST /login {email,pwd}─► Auth ─verify password_hash─► 200 {access, refresh, expiresAt}
… access hết hạn …
FE ─POST /refresh {refreshToken}─► Auth
      ├─ hợp lệ? (tồn tại · is_revoked=false · expires_at>now)   ─ không ─► 401
      ├─ revoke token cũ (is_revoked=true, replaced_by=tokenMới)   ← 1 refresh dùng 1 lần
      └─► 200 {refresh mới, expiresAt}
```

**Validate JWT offline (mọi service khác — KHÔNG gọi Auth):**
```
FE ─Bearer access─► Service X ── verify chữ ký bằng Jwt:Key/Issuer/Audience (offline) ──► OK / 401
                                 (gọi Auth CHỈ khi cần dữ liệu tươi ngoài token, vd email xuất hóa đơn)
```

## DB — `isas`
ASP.NET Identity (`IdentityUser<Guid>`), cột **snake_case**. Kiểu: `uuid·varchar(n)·text·bool·timestamptz·enum(string)`, `?`=nullable.
```
users ─┬─*──* roles            (qua user_roles)
       ├─1──* refresh_tokens
       ├─*──* organizations ✅  (qua org_members, kèm org_role — migration AddOrganizations)
       └─1──* user_claims · user_tokens · user_logins   (Identity / Google OAuth)
roles ──1──* role_claims
```

### `users` (mở rộng AspNetUsers)
```
id              uuid          PK
user_name       varchar       (Identity)
email           varchar       (Identity)
                              ✅ DB11 (2026-07-17): UNIQUE INDEX normalized_email (EmailIndex) filtered `WHERE normalized_email IS NOT NULL` — enforce email không trùng ở tầng DB (kèm RequireUniqueEmail app-level)
password_hash   text?         null nếu chỉ đăng nhập Google OAuth
email_confirmed bool
full_name       varchar?
location        varchar?
title           varchar?
created_at      timestamptz   default now()
updated_at      timestamptz?
```
+ cột Identity chuẩn: `security_stamp`, `lockout_*`, `phone_*`…

### `roles` / `user_roles`
```
roles       { id uuid PK · name varchar }          // Candidate · Employer · Admin
user_roles  { user_id uuid FK→users · role_id uuid FK→roles }   PK (user_id, role_id)
```

### `refresh_tokens`
```
id          uuid          PK
user_id     uuid          FK → users
token       varchar       NOT NULL — lưu **SHA-256 hash** của refresh token (raw chỉ trả client); ✅ DB12 (2026-07-17): UNIQUE INDEX token (chống trùng + lookup index thay full-scan)
is_revoked  bool          default false
replaced_by uuid?         token thay thế (rotation)
expires_at  timestamptz   hạn theo Jwt:RefreshTokenDays
created_at  timestamptz   default now()
```

### `organizations` — B2B ✅ (migration `AddOrganizations`, A1)
```
id         uuid          PK
name       text          NOT NULL
tax_code   text?         MST (xuất hóa đơn postpaid)
created_at timestamptz
```

### `org_members` — ✅ (migration `AddOrganizations`, A1)
```
org_id   uuid          FK → organizations (cascade)
user_id  uuid          FK → users (cascade)
org_role varchar(16)   enum(string): OrgAdmin · HrMember
joined_at timestamptz   ✅ A6b — thời điểm vào org (set khi tạo member; rows cũ backfill defaultValueSql now() lúc apply)
                       PK (org_id, user_id); JWT Employer mang kèm org_id + org_role ✅ A2 (claim "org_id"/"org_role", chỉ thêm khi user thuộc org)
```

+ bảng Identity phụ: `role_claims` · `user_claims` · `user_tokens` · `user_logins` (Google OAuth).

### Index / ràng buộc / edge case
- **UNIQUE**: `users.normalized_email`, `users.normalized_user_name` (Identity) — chống trùng tài khoản; `org_members` PK `(org_id, user_id)`; `roles.normalized_name`; **`refresh_tokens.token`** (lookup theo token, chống trùng); **`organizations.tax_code`** (nullable-unique — 1 MST = 1 pháp nhân; trùng → 409, điều kiện để duyệt postpaid đúng org).
- **Index / self-FK**: `refresh_tokens(user_id)` (revoke-all theo user); `refresh_tokens.replaced_by` là **self-FK → refresh_tokens.id** (chuỗi rotation — truy ngược được token nào đẻ ra token nào khi nghi trộm token).
- **on-delete**: `user_roles` · `refresh_tokens` · `org_members` **Cascade** theo `user`; xoá `organization` → cascade `org_members`.
- **Refresh rotation**: 1 refresh dùng 1 lần → revoke + `replaced_by`. **Dùng lại token đã revoke** = dấu hiệu trộm token → **401**, TRỪ trong **cửa sổ ân hạn** `Jwt:RefreshTokenGraceSeconds` (mặc định 60s) dành cho đua refresh giữa nhiều tab: token vừa bị xoay → đi theo `replaced_by` tới token còn sống ở cuối chuỗi rồi xoay tiếp. Không trả lại chính token thay thế được vì DB chỉ lưu **hash** (raw chỉ ở client) → cấp cặp mới cho tab đến muộn; các tab hội tụ nhờ FE đồng bộ token qua sự kiện `storage`. Token revoke **không có** `replaced_by` (đăng xuất/đổi quyền) → không ân hạn. *(Revoke cả chuỗi khi nghi trộm — phase 2.)*
- **Lockout**: `access_failed_count` tăng mỗi login sai; chạm ngưỡng → khoá tới `lockout_end` (nếu bật `LockoutOptions`).
- **JWT offline ⇒ thu hồi quyền KHÔNG tức thì (ranh giới hiệu lực = ≤ 1 TTL access token, 15')**: access token **không thu hồi được** — các service validate JWT offline bằng chung key, không hỏi AuthService lúc chạy (**GEN-3**). Đây là đánh đổi **có chủ đích** của auth offline ([../architecture.md](../architecture.md) §3), KHÔNG phải thiếu sót. Cụ thể:
  - **Đổi quyền** (`PATCH /auth/org/members/{userId}`) và **gỡ khỏi org** (`DELETE …`) → server **thu hồi mọi refresh token của user đó** ⇒ lần refresh kế họ phải đăng nhập lại và nhận quyền mới, thay vì mang quyền cũ suốt 7 ngày của refresh token.
  - **Đăng xuất** → thu hồi mọi refresh token; access token cũ vẫn sống tới hết TTL ⇒ **FE phải xoá token khỏi storage**.
  - Trong cả hai ca, quyền/phiên cũ còn hiệu lực **tối đa 15'** (TTL access token). Cần tức thì hơn → **rút ngắn TTL access**, ❌ **KHÔNG** thêm denylist/gọi mạng vào đường validate của service khác (vi phạm GEN-3).
- **Google-only user**: `password_hash=null` → chặn login mật khẩu, chỉ OAuth.

## Xác thực (nguồn chân lý cho cả hệ)
- JWT phát bởi Auth, **các service khác validate bằng cùng** `Jwt:Key` / `Issuer` / `Audience` — **không** call Auth.
- Chỉ nên gọi Auth khi cần **dữ liệu tươi không có trong token** (vd email để xuất hóa đơn).
- `RoleClaimType = ClaimTypes.Role`, `MapInboundClaims = false` (giữ nguyên claim).
