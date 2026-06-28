# AuthService

> Định danh & phát JWT cho toàn hệ. Code: `src/services/Isas.AuthService`. DB: `isas`. Gateway: `/api/v1/auth`.
> Quy ước chung (snake_case, JWT key, ...): [../architecture.md](../architecture.md) §5. Phân việc: [../work-division.md](../work-division.md).

## Vai trò
- Đăng ký / đăng nhập (email + mật khẩu, liên kết **Google OAuth**), phát **JWT access + refresh token**, profile.
- **3 platform role đã có: `Candidate` / `Employer` / `Admin`** (gắn vào claim JWT). `Admin` ở đây = **PlatformAdmin** (quản trị hệ thống: duyệt postpaid, quản gói), **khác** org-admin bên dưới.
- `register` **auto gán `Candidate`**; **`register-org` cấp role `Employer`** + tạo org + OrgAdmin ✅ A3.
- Các service khác **không gọi Auth lúc chạy** — validate JWT **offline** bằng chung key.

## Organization & phân quyền nội bộ (multi-tenant) 🟢 core A1–A3 xong; RBAC đầy đủ = phase 2
B2B bán cho **doanh nghiệp**, không phải cá nhân → cần khái niệm **tổ chức**:
- Một **Organization** (`org_id`) = 1 doanh nghiệp; **billing/credit gắn org** (xem [payment.md](payment.md)), **campaign gắn `org_id`**.
- **Role nội bộ org** (claim kèm trong JWT): **`OrgAdmin`** (mua gói/trả tiền/xem billing, quản thành viên) vs **`HrMember`** (tạo & quản campaign, **không** xem billing).
- **Phase 1 (capstone):** data model có `org_id` + `org_role` từ đầu; có thể demo **1 org = 1 OrgAdmin**. **Sub-account HR đầy đủ (mời thành viên, phân quyền chi tiết) = phase 2.**

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
- Req: `{ email: string, password: string, fullName: string }` → Res **`200`** `AuthResponse`. Lỗi: **400** (email tồn tại / mật khẩu yếu).

**`POST /register-org`** — Đăng ký tổ chức (✅ A3). Public. Tạo user role **`Employer`** + `Organization` + `OrgMember(OrgAdmin)`.
- Req: `{ email: string, password: string, fullName: string, orgName: string, taxCode: string? }` → Res **`200`** `AuthResponse` (token mang `org_id`+`org_role`). Lỗi: **400** (email tồn tại / mật khẩu yếu).

**`POST /login`** — Đăng nhập. Public.
- Req: `{ email: string, password: string }` → Res **`200`** `AuthResponse`. Lỗi: **400/401** (sai thông tin).

**`GET /login-google`** → redirect Google · **`GET /login-google-callback?returnUrl&remoteError`** → OAuth callback, trả `AuthResponse`. Public.

**`POST /refresh`** — Làm mới token. Public.
- Req: `{ refreshToken: string }` → Res **`200`** `RefreshTokenResponse`. Lỗi: **401** (token hết hạn/thu hồi).

**`POST /logout`** — Thu hồi refresh token. Auth (`Candidate·Employer·Admin`).
- Req: `{ refreshToken: string }` → Res **`200`**. Lỗi: **401**.

**`GET /me`** — Profile. Auth. → Res **`200`** `UserResponse`. Lỗi: **401**.
**`PUT /me`** — Cập nhật profile. Auth.
- Req: `{ fullName: string?, location: string?, title: string? }` → Res **`200`** `UserResponse`. Lỗi: **401**.

**`POST /forgot-password`** `{ email: string }` → gửi OTP · **`POST /verify-otp`** `{ email: string, otp: string }` · **`POST /reset-password`** `{ email: string, newPassword: string }`. Public. Lỗi: **400** (OTP sai/hết hạn).

**🔜 Admin (PlatformAdmin) — quản trị Org/role chưa build (A4/A5):**
- **`POST /auth/admin/users/{id}/roles`** — gán/thu platform role (vd nâng user → `Employer`).
- **`GET/POST /auth/admin/orgs…`** — xem / duyệt / khóa tổ chức (verify MST khi duyệt postpaid).
- *(✅ `register-org` → tạo `Organization` + `OrgAdmin`, JWT mang `org_id`+`org_role` — A1/A2/A3 xong. Còn admin-gated orgs + role-grant — A4/A5.)*

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
token       varchar       NOT NULL
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
                       PK (org_id, user_id); JWT Employer mang kèm org_id + org_role ✅ A2 (claim "org_id"/"org_role", chỉ thêm khi user thuộc org)
```

+ bảng Identity phụ: `role_claims` · `user_claims` · `user_tokens` · `user_logins` (Google OAuth).

## Xác thực (nguồn chân lý cho cả hệ)
- JWT phát bởi Auth, **các service khác validate bằng cùng** `Jwt:Key` / `Issuer` / `Audience` — **không** call Auth.
- Chỉ nên gọi Auth khi cần **dữ liệu tươi không có trong token** (vd email để xuất hóa đơn).
- `RoleClaimType = ClaimTypes.Role`, `MapInboundClaims = false` (giữ nguyên claim).
