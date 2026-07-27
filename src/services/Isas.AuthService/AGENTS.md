> **Bản sao cho agent** của [`docs/services/auth.md`](../../../docs/services/auth.md) — contract (API + DB + business rules) của AuthService. **Source of truth ở `docs/`**: sửa thiết kế tại đó rồi copy lại (đừng sửa bản này lệch). Ràng buộc chung + playbook gen code: [`/AGENTS.md`](../../../AGENTS.md).

---

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
- Req: `{ email: string, password: string, fullName: string }` → Res **`200`** `AuthResponse`. Lỗi: **409** (email đã tồn tại — body `{ error }`) · **400** (mật khẩu yếu).

**`POST /register-org`** — Đăng ký tổ chức (✅ A3). Public. Tạo user role **`Employer`** + `Organization` + `OrgMember(OrgAdmin)`.
- Req: `{ email: string, password: string, fullName: string, orgName: string, taxCode: string? }` → Res **`200`** `AuthResponse` (token mang `org_id`+`org_role`). Lỗi: **409** (email đã tồn tại — body `{ error }`) · **400** (mật khẩu yếu / `orgName` rỗng).

**`POST /login`** — Đăng nhập. Public.
- Req: `{ email: string, password: string }` → Res **`200`** `AuthResponse`. Lỗi: **400/401** (sai thông tin).

**`GET /login-google`** → redirect Google · **`GET /login-google-callback?returnUrl&remoteError`** → OAuth callback, trả `AuthResponse`. Public.

**`POST /refresh`** — Làm mới token. Public.
- Req: `{ refreshToken: string }` → Res **`200`** `RefreshTokenResponse`. Lỗi: **401** (token hết hạn / thu hồi / quá **cửa sổ ân hạn**).
- **Cửa sổ ân hạn xoay vòng** (`Jwt:RefreshTokenGraceSeconds`, mặc định **60s**, `0`=tắt): token vừa bị xoay vòng vẫn refresh được thêm ngần đó giây — đi theo `replaced_by` tới token **còn sống** ở cuối chuỗi rồi xoay tiếp. Mốc đo = `created_at` của token thay thế (không cần cột `revoked_at` ⇒ **không migration**). Token thu hồi **thẳng tay** (đăng xuất/đổi quyền, `replaced_by` NULL) **KHÔNG** ân hạn. *Vì sao:* đua refresh giữa nhiều tab. *Đánh đổi:* làm yếu reuse-detection trong đúng cửa sổ đó → giữ NGẮN.

**`POST /logout`** — Thu hồi **MỌI** refresh token của user đang đăng nhập. Auth (`Candidate·Employer·Admin`).
- Req: `{ refreshToken: string }` (giữ hợp đồng cũ; phạm vi thu hồi theo claim `sub`) → Res **`204`**. Lỗi: **401**.
- ⚠ access token đang lưu hành **không thu hồi được** (GEN-3) → còn hợp lệ tới hết TTL (15'); **FE phải xoá token khỏi storage**.

**`GET /me`** — Profile. Auth. → Res **`200`** `UserResponse`. Lỗi: **401**.
**`PUT /me`** — Cập nhật profile. Auth.
- Req: `{ fullName: string?, location: string?, title: string? }` → Res **`200`** `UserResponse`. Lỗi: **401**.

**`POST /forgot-password`** `{ email: string }` → gửi OTP · **`POST /verify-otp`** `{ email: string, otp: string }` · **`POST /reset-password`** `{ email: string, newPassword: string }`. Public. Lỗi: **400** (OTP sai/hết hạn).

**✅ Admin (PlatformAdmin) — đình chỉ account + đặt lại mật khẩu hộ (F20 / FR16, AUTH-7).** Cụm `auth/admin` gác `[Authorize(Roles="Admin")]` ở CLASS (A5). ⚠ **platform-role `Admin`, KHÁC `/auth/org/members`** (`Employer` + `OrgAdmin` trong một org — AUTH-4/AUTH-8).

- **`POST /auth/admin/users/{id}/ban`** `{ reason?: string }` (≤500) → **`200`** `AdminUserResponse`. Lỗi: **400** tự ban mình · **404** · **409** Admin hoạt động cuối cùng.
- **`POST /auth/admin/users/{id}/unban`** → **`200`**. Lỗi: **404**.
- **`POST /auth/admin/users/{id}/reset-password`** `{ newPassword: string }` → **`204`**. Lỗi: **400** policy · **404**. Thu hồi mọi refresh token.
- **`GET /auth/admin/users`** trả kèm `bannedAt`/`banReason` (additive).
- **`GET /auth/admin/organizations`** và **`GET /auth/admin/users`**: `cursor` hỏng hoặc `limit <= 0` → **400**; cursor vắng/hợp lệ giữ keyset paging cũ.

> ⚠⚠ **Ban KHÔNG tức thì (AUTH-5 / GEN-3).** Service khác validate JWT **offline** → access token đang lưu hành **không thu hồi được**, còn sống tối đa **1 TTL (15')**. Ban chặn **mọi** đường phát phiên mới (mật khẩu · Google · refresh · `provision-candidate` magic-link) + thu hồi mọi refresh token. Chặt hơn → **rút ngắn TTL access**; ❌ KHÔNG denylist/gọi mạng trong đường validate.

**🔜 Admin — chưa build:**
- **`POST /auth/admin/users/{id}/roles`** — gán/thu platform role (vd nâng user → `Employer`).
- **`GET/POST /auth/admin/orgs…`** — duyệt / khóa tổ chức (verify MST khi duyệt postpaid).
- *(✅ `register-org` → tạo `Organization` + `OrgAdmin`, JWT mang `org_id`+`org_role` — A1/A2/A3 xong. Còn admin-gated orgs + role-grant — A4/A5.)*

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
| `email` | bắt buộc; format email; chuẩn hoá `normalized_email` **UNIQUE** (trùng → 400) |
| `password` | bắt buộc; theo `PasswordOptions` Identity (độ dài ≥ 6, chữ+số…); **null chỉ** khi user Google-only |
| `fullName` | bắt buộc khi `register`/`register-org` |
| `orgName` | bắt buộc (register-org), non-empty (trim) |
| `taxCode` | optional; cần khi **duyệt postpaid** (verify MST) |
| `otp` | 6 số, TTL ngắn (~5'); sai/hết hạn → 400 |
| `refreshToken` | bắt buộc; `is_revoked=false` + `expires_at>now` |

### Bảng mã lỗi (đặc thù — mã chung [../architecture.md](../architecture.md) §6)
| Mã | Khi nào |
|---|---|
| 400 | email đã tồn tại · mật khẩu yếu (policy) · OTP sai/hết hạn · `orgName` rỗng |
| 401 | login sai email/mật khẩu · refresh hết hạn/đã revoke · thiếu/sai Bearer |
| 403 | role/`org_role` không đủ quyền (vd `HrMember` gọi admin/billing — A4) · **login của account bị đình chỉ** (F20 — 403 chứ không 401: mật khẩu ĐÚNG, cái bị từ chối là quyền dùng hệ thống) · `provision-candidate` cho account bị đình chỉ |
| 409 | (admin) tạo org trùng / gán role mâu thuẫn |
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
password_hash   text?         null nếu chỉ đăng nhập Google OAuth
email_confirmed bool
full_name       varchar?
location        varchar?
title           varchar?
created_at      timestamptz   default now()
updated_at      timestamptz?
banned_at       timestamptz?  ✅ F20 — mốc PlatformAdmin ĐÌNH CHỈ account (null = đang hoạt động)
ban_reason      varchar(500)? ✅ F20 — lý do (chỉ hiển thị cho admin)
banned_by       uuid?         ✅ F20 — admin ra quyết định (ref lỏng → users.id)
```
+ cột Identity chuẩn: `security_stamp`, `lockout_*`, `phone_*`…

> ⚠ **`banned_at` ≠ `lockout_end`.** `lockout_*` là khoá **TỰ ĐỘNG** do sai mật khẩu, Identity tự đặt/xoá → gộp ban vào đó thì không phân biệt được "bị cấm" với "gõ sai 5 lần", và một lần đăng nhập thành công / reset mật khẩu sẽ **vô tình gỡ lệnh cấm**. Migration `AddUserBanF20` — thuần `AddColumn` nullable, online-safe.

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

### Index / ràng buộc / edge case
- **UNIQUE**: `users.normalized_email`, `users.normalized_user_name` (Identity) — chống trùng tài khoản; `org_members` PK `(org_id, user_id)`; `roles.normalized_name`.
- **on-delete**: `user_roles` · `refresh_tokens` · `org_members` **Cascade** theo `user`; xoá `organization` → cascade `org_members`.
- **Refresh rotation**: 1 refresh dùng 1 lần → revoke + `replaced_by`. **Dùng lại token đã revoke** = dấu hiệu trộm token → **401**, TRỪ trong **cửa sổ ân hạn** (mặc định 60s) cho đua refresh nhiều tab: đi theo `replaced_by` tới token còn sống ở cuối chuỗi rồi xoay tiếp. Không trả lại chính token thay thế được vì DB chỉ lưu **hash** (raw chỉ ở client) → cấp cặp mới cho tab đến muộn; các tab hội tụ nhờ FE đồng bộ qua sự kiện `storage`. *(Revoke cả chuỗi khi nghi trộm — phase 2.)*
- **Lockout**: `access_failed_count` tăng mỗi login sai; chạm ngưỡng → khoá tới `lockout_end` (nếu bật `LockoutOptions`).
- **JWT offline ⇒ thu hồi quyền KHÔNG tức thì (ranh giới = ≤ 1 TTL access token, 15')**: access token **không thu hồi được** (validate offline bằng chung key — **GEN-3**). Đổi `org_role` / gỡ khỏi org / đăng xuất → **thu hồi mọi refresh token** của user ⇒ lần refresh kế phải nhận token mới mang quyền mới. Đánh đổi **có chủ đích** của auth offline ([../architecture.md](../architecture.md) §3); cần tức thì → **rút ngắn TTL access**, ❌ KHÔNG thêm denylist/gọi mạng vào đường validate của service khác.
- **Google-only user**: `password_hash=null` → chặn login mật khẩu, chỉ OAuth.
- **Tạo tài khoản là NGUYÊN TỬ**: 5 đường tạo account (`register` · `register-org` · đăng nhập Google lần đầu · `/internal/auth/provision-candidate` · `POST /auth/org/members`) chạy trong **một transaction** — hoặc tài khoản ra đời **đầy đủ** (`users` + `user_roles` + `organizations`/`org_members` + `user_logins` nếu có), hoặc **không có gì**. Trước đây mỗi đường ghi 3–5 lần `SaveChanges` rời rạc, lỗi giữa chừng để lại **user không role** (đăng nhập được nhưng mọi endpoint `[Authorize(Roles)]` trả 403) hoặc **Employer không thuộc org nào** (mất cả quyền lẫn billing — AUTH-8), mà **email đã UNIQUE nên không đăng ký/mời lại được để tự sửa** và không có job nào dọn. Kết quả `AddToRoleAsync` nay **được kiểm** — trước đây bị bỏ qua nên một thất bại không-ném vẫn commit user thiếu role trong im lặng.
  - ⚠ Transaction PHẢI mở qua **`Database.CreateExecutionStrategy()`**: `Program.cs` bật `EnableRetryOnFailure()`, và chiến lược retry của EF **từ chối transaction tự mở** ⇒ gọi thẳng `BeginTransactionAsync` sẽ ném ở **mọi** lần đăng ký trên Postgres trong khi test SQLite vẫn xanh (kiểu bug DB25b). Có test khoá hợp đồng này bằng một execution strategy giả có `RetriesOnFailure=true`.
  - ⚠ **`EnsureRoleExistsAsync` cố ý nằm NGOÀI transaction**: role là *dữ liệu tham chiếu* (Candidate/Employer), không phải dữ liệu của riêng tài khoản. Ở trong thì hai request đăng ký **đầu tiên** chạy song song sẽ đua tạo cùng role, bên thua đụng UNIQUE `roles.normalized_name` và **rollback theo cả việc tạo user** — một lần đăng ký hợp lệ bị 500 vì lý do không liên quan. Bên thua đua được xử lý là **thành công** (mục tiêu "role tồn tại" đã đạt).
  - ⚠ **`refresh_tokens` cố ý nằm NGOÀI transaction**: đó là trạng thái **phiên**, không phải trạng thái tài khoản. Hỏng ở bước đó để lại tài khoản đầy đủ và hợp lệ — người dùng chỉ cần đăng nhập lại. Với `register-org` thì việc này còn **bắt buộc**: token phải đọc `org_members` sau khi đã commit mới mang đúng `org_id`/`org_role` (A2).


## Xác thực (nguồn chân lý cho cả hệ)
- JWT phát bởi Auth, **các service khác validate bằng cùng** `Jwt:Key` / `Issuer` / `Audience` — **không** call Auth.
- Chỉ nên gọi Auth khi cần **dữ liệu tươi không có trong token** (vd email để xuất hóa đơn).
- `RoleClaimType = ClaimTypes.Role`, `MapInboundClaims = false` (giữ nguyên claim).
