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

// ⚠ KHÔNG còn là kiểu trả về của `POST /refresh` — giữ lại vì DTO vẫn tồn tại trong code.
// `/refresh` trả `AuthResponse` (có `accessToken`), xem ghi chú ở endpoint đó.
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
  orgId:     uuid?                     // nullable khi không thuộc org
  orgName:   string?
  orgRole:   enum(string)?             // OrgAdmin·HrMember
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
- **`GET /login-google-callback?returnUrl&remoteError`** — action MVC, đích cuối vòng OAuth. **Trả `302`**, KHÔNG trả JSON (người dùng đang ở điều hướng cả trang → đáp xuống JSON thô thì app Angular không bao giờ chạy lại để nhận token). **Redirect KHÔNG mang token, chỉ mang mã dùng-một-lần:**
  - Thành công → `{Frontend:BaseUrl}/auth/google/callback?code=<mã dùng-một-lần>[&returnUrl=…]`
  - Thất bại → `{Frontend:BaseUrl}/auth/google/callback?error=<remote_error|no_login_info|login_failed>`
- **`POST /google/exchange`** — chặng 2: đổi mã lấy phiên. Public (`[AllowAnonymous]` — lúc này user chưa có token).
  - Req: `{ code: string }` → Res **`200`** `AuthResponse`. Lỗi: **400** (mã sai / hết hạn / **đã dùng**) — cả 3 ca trả CÙNG một thông điệp để không giúp kẻ dò mã biết mình đoán gần đúng.

**Vì sao one-time code chứ không phải token trong URL:** bản trước đính token vào **fragment** — kín với access log và header `Referer` (fragment không được gửi lên server), nhưng vẫn nằm trong `location.hash` nên **script cùng trang, kể cả extension trình duyệt độc hại, đọc được**. Mã thì đọc trộm cũng vô dụng nếu FE đã đổi trước: đổi lần hai là thất bại.

**Tính chất của mã** — 32 byte từ `RandomNumberGenerator` (256 bit, base64url; **không dùng `Guid`** — chỉ 122 bit và không cam kết nguồn ngẫu nhiên mật mã) · **TTL ngắn** `Authentication:Google:OneTimeCodeTtlSeconds` (mặc định **60s**, kẹp trong `[5, 600]`) · **dùng một lần** (đọc-và-xoá nguyên tử dưới khoá) · **KHÔNG bao giờ ghi giá trị mã vào log**.

> ⚠ **Kho mã nằm trong BỘ NHỚ TIẾN TRÌNH** (`IMemoryCache`, `GoogleAuthCodeStore`) — không có bảng DB, không cần migration, hết hạn thì cache tự dọn (không cần sweeper). **Giới hạn phải biết khi vận hành:** mã phát ở instance nào chỉ đổi được ở **đúng instance đó**, và **restart/deploy làm mất mã đang bay** (hệ quả nhẹ: người dùng bấm đăng nhập Google lại). Deploy hiện tại **single-instance** nên chấp nhận được. **Scale ra nhiều instance AuthService ⇒ đăng nhập Google sẽ hỏng ngẫu nhiên** — khi đó phải bật sticky session hoặc chuyển kho sang Redis / bảng DB.

**Bảo mật đích redirect:** base URL LUÔN từ config server (`Frontend:BaseUrl` / `Gateway:PublicBaseUrl`). `returnUrl` do client truyền chỉ được chấp nhận khi là **đường dẫn tương đối** (bắt đầu `/`, không `//`, không `/\`, không scheme, không ký tự điều khiển) rồi ghép sau base đã cấu hình — nhận host từ client = open-redirect làm rò phiên.

**Account linking:** email Google trùng account mật khẩu sẵn có → **liên kết** external login vào account đó (`AddLoginAsync`) rồi đăng nhập, KHÔNG tạo user thứ hai. Chưa có account → tạo mới **passwordless** + role `Candidate` (AUTH-1; luồng này không mở đường Employer/org).

**Config bắt buộc:** `Authentication:Google:ClientId/ClientSecret` · `Frontend:BaseUrl` · `Gateway:PublicBaseUrl`. **Tuỳ chọn:** `Authentication:Google:OneTimeCodeTtlSeconds` (mặc định 60).

**Đăng nhập Google NATIVE (app mobile) — `POST /google/id-token`, Public.** App tự lấy ID token bằng Google Sign-In SDK của hệ điều hành rồi POST thẳng lên; **không có redirect, không có kho mã dùng-một-lần, không dùng `Frontend:BaseUrl`/`Gateway:PublicBaseUrl`** (những thứ đó chỉ phục vụ vòng OAuth trình duyệt ở trên).
- Req `{ idToken: string }` → Res **`200`** `AuthResponse` (**cùng shape đăng nhập mật khẩu** ⇒ client mobile dùng chung code xử lý phiên).
- **401** — token sai chữ ký / hết hạn / `aud` ngoài allowlist / **email chưa xác minh** / thiếu `sub`|email. Cố ý **một thông điệp chung** cho mọi lý do; lý do thật vào log.
- **403** — account bị đình chỉ (F20, soi gương đường mật khẩu: danh tính đúng, cái bị từ chối là quyền dùng).
- **500** — server chưa cấu hình allowlist `aud`. **Không** trả 401 ở ca này: đó là lỗi cấu hình của ta, trả 401 sẽ khiến dev mobile đi truy token của họ trong khi thứ hỏng nằm ở env server.

> 🔑 **Hai đường Google kết thúc ở CÙNG một hàm `LoginGoogleAsync`.** Web lấy `ExternalLoginInfo` từ cookie do handler OAuth ghi; mobile dựng `ExternalLoginInfo` từ ID token đã verify (`GoogleExternalLogin.Create`). Nhờ vậy account-linking, tạo user + role, chặn ban, `LoginEvent` chỉ có **một bản**. Thêm luồng đăng nhập song song mới là thứ sẽ trôi lệch dần.
>
> 🔑 **Bất biến:** `ProviderKey` = claim **`sub`** của Google và `LoginProvider` = **`"Google"`** — trùng khít thứ đường web sinh ra ⇒ người đăng nhập web hôm nay, mai đăng nhập app vào **đúng account cũ**. Lệch (ví dụ lấy email làm khoá) thì mỗi người dần có **hai** liên kết external trong `user_logins`, hỏng dần mà không lỗi nào nổ. Có test khoá (web→mobile cùng user **và** `AddLoginAsync` chỉ gọi 1 lần).
>
> 🔴 **`email_verified` là cửa chặn chiếm account, không phải kiểm tra cho đủ:** `LoginGoogleAsync` gắn external login vào account **mật khẩu** sẵn có khi trùng email. Đường web an toàn vì Google chỉ phát cookie cho account thật của người dùng; đường này nhận token **do client gửi lên**, nên bỏ qua cờ đó là để một account Google mang địa chỉ của người khác chiếm được account ISAS.
>
> 🔴 **Allowlist `aud` fail-closed:** `ValidationSettings.Audience` rỗng nghĩa là thư viện Google **BỎ QUA** kiểm tra `aud` ⇒ mọi Google ID token trên đời (kể cả token từ project Google của kẻ tấn công) đăng nhập được. Vì vậy trống cả `Authentication:Google:IdTokenAudiences` lẫn `ClientId` thì **ném**, không trả danh sách rỗng.

**Config:** `Authentication:Google:IdTokenAudiences` (mảng). **Để trống = rơi về `Authentication:Google:ClientId`** — đúng cho đa số, vì app xin ID token kèm `serverClientId` = **WEB** client ID nên `aud` chính là nó. ⚠ App đặt `serverClientId` bằng Android/iOS client ID là sai (401 câm — log in ra allowlist đang cấu hình để đối chiếu). Android/iOS client kèm SHA-1 trên Google Cloud Console vẫn cần, nhưng chỉ để Google xác thực **app**; SHA-1 khoá theo **chữ ký APK** (một fingerprint dùng cho mọi điện thoại), nên cần một client cho **mỗi keystore**: debug từng máy dev · release · Play App Signing.

**`POST /refresh`** — Làm mới token. Public.
- Req: `{ refreshToken: string }` → Res **`200`** **`AuthResponse`** (có `accessToken` — xem cảnh báo dưới). Lỗi: **401** (token hết hạn / thu hồi / quá **cửa sổ ân hạn** bên dưới).
- ⚠ **Trước đây action khai `RefreshTokenResponse`, tức OpenAPI mô tả một response KHÔNG có `accessToken`.** JSON lúc chạy vẫn đúng (`ObjectResult` serialize theo kiểu thật) nên không client nào gãy — cái gãy là **tài liệu**. Client sinh model từ Scalar (app mobile) sẽ đọc hụt access token rồi refresh vô tận mà không hiểu vì sao. Nay khai đúng `AuthResponse`; có test khoá kiểu khai của action, vì chính KIỂU KHAI sinh ra schema.
- **Cửa sổ ân hạn xoay vòng** (`Jwt:RefreshTokenGraceSeconds`, mặc định **60s**, `0` = tắt): token vừa bị xoay vòng vẫn refresh được thêm ngần đó giây — server đi theo `replaced_by` tới token **còn sống** ở cuối chuỗi và xoay tiếp, trả cặp token mới. Mốc đo là `created_at` của token thay thế (không cần cột `revoked_at`). Token bị thu hồi **thẳng tay** (đăng xuất / đổi quyền — `replaced_by` NULL) **KHÔNG** hưởng ân hạn, chết ngay.
- *Vì sao:* mỗi tab giữ refresh token riêng nhưng chung một phiên; thu-hồi-tức-thì làm tab đến muộn ăn 401 → đăng xuất oan (mở 2 tab là dính; quay về từ PayOS gần như luôn tạo tab thứ hai). *Đánh đổi:* thu-hồi-tức-thì chính là cơ chế **phát hiện token bị đánh cắp** (reuse detection) — ân hạn làm yếu nó trong đúng cửa sổ đó, nên giữ NGẮN.

**`POST /logout`** — Thu hồi **MỌI** refresh token của user đang đăng nhập. Auth (`Candidate·Employer·Admin`).
- Req: `{ refreshToken: string }` (giữ hợp đồng cũ; phạm vi thu hồi lấy theo claim `sub`, **không** theo token gửi kèm) → Res **`204`**. Lỗi: **401**.
- Thu hồi đúng 1 token thì tab khác vẫn gia hạn phiên tiếp → "đã đăng xuất" mà phiên vẫn sống. ⚠ **access token đang lưu hành KHÔNG thu hồi được** (validate offline — GEN-3) nên còn hợp lệ tới hết TTL (**15'**); **FE phải tự xoá token khỏi storage** khi đăng xuất (đã làm: `AuthStore.logout()` gọi `clearSession()` trước khi gọi API).

**`GET /me`** — Profile. Auth. → Res **`200`** `UserResponse` kèm `orgId`/`orgName`/`orgRole` nullable. Lỗi: **401**.
**`PUT /me`** — Cập nhật profile. Auth.
- Req: `{ fullName: string?, location: string?, title: string? }` → Res **`200`** `UserResponse`. Lỗi: **401**.

**`POST /forgot-password`** `{ email: string }` → gửi OTP · **`POST /verify-otp`** `{ email: string, otp: string }` · **`POST /reset-password`** `{ email: string, newPassword: string }`. Public. Lỗi: **400** (OTP sai/hết hạn).

**✅ Admin (PlatformAdmin) — đình chỉ account + đặt lại mật khẩu hộ (F20 / FR16, AUTH-7).** Cả cụm `auth/admin` gác `[Authorize(Roles="Admin")]` ở CLASS (A5). ⚠ **Đây là platform-role `Admin`, KHÁC `POST/PATCH/DELETE /auth/org/members`** vốn là `Employer` + `OrgAdmin` trong một org (AUTH-4/AUTH-8) — hai mô hình quyền khác nhau, đừng bê qua lại.

- **`POST /auth/admin/users/{id}/ban`** — đình chỉ account. Req `{ reason?: string }` (≤500 ký tự) → Res **`200`** `AdminUserResponse` (kèm `bannedAt`/`banReason`). Lỗi: **400** tự ban mình · **404** user lạ · **409** người bị ban là **Admin còn hoạt động cuối cùng** (cấm hết Admin thì không còn ai gỡ ban được cho ai — hệ thống tự khoá mình).
- **`POST /auth/admin/users/{id}/unban`** — gỡ đình chỉ → **`200`**. Lỗi: **404**. *(Không khôi phục refresh token cũ — đăng nhập lại là có phiên mới.)*
- **`POST /auth/admin/users/{id}/reset-password`** — đặt lại mật khẩu hộ. Req `{ newPassword: string }` → **`204`**. Lỗi: **400** mật khẩu không đạt policy Identity · **404**. Thu hồi **mọi refresh token** của user (không thì đổi mật khẩu KHÔNG đuổi được kẻ đang chiếm tài khoản).
- **`GET /auth/admin/users`** nay trả kèm `bannedAt`/`banReason` (additive — FE cũ không vỡ).
- **`GET /auth/admin/organizations`** và **`GET /auth/admin/users`**: `cursor` hỏng hoặc `limit <= 0` → **400** (không còn âm thầm quay về trang đầu); cursor vắng và cursor hợp lệ giữ keyset paging cũ.

> ⚠⚠ **RANH GIỚI HIỆU LỰC CỦA BAN (AUTH-5 / GEN-3 — đọc trước khi "siết cho chặt hơn").**
> Service khác validate JWT **offline** bằng chung khoá, **không hỏi AuthService lúc chạy** → **access token đang lưu hành KHÔNG thu hồi được**. Ban vì thế **không tức thì**: người vừa bị cấm vẫn gọi API được **tối đa 1 TTL access token (15')**.
> Ban làm ngay hai việc: **(1)** chặn **mọi** đường phát phiên mới — đăng nhập mật khẩu · đăng nhập Google · refresh · `provision-candidate` (magic-link B2B, cấp JWT chỉ theo email nên là cửa dễ bỏ sót nhất); **(2)** thu hồi **mọi refresh token** → không gia hạn được nữa. Sau ≤15' account chết hẳn.
> Cần chặt hơn → **rút ngắn TTL access**. ❌ **KHÔNG** thêm denylist / gọi mạng vào đường validate của service khác (vi phạm GEN-3, ràng buộc cứng). Cùng đánh đổi đã chốt ở AUTH-5/BK14 cho việc đổi `org_role`.

- **`POST /auth/admin/users/{id}/role`** — đổi **platform-role** (AUTH-3). Req `{ role: "Candidate" | "Employer" | "Admin" }` → Res **`200`** `AdminUserResponse`. Lỗi: **400** role ngoài 3 tên trên · **400** tự đổi vai trò của chính mình · **404** user lạ · **409** hạ cấp **Admin còn hoạt động cuối cùng** · **409** rời `Employer` khi **vẫn còn hàng `org_members`**.
  - Mô hình **1 role/user** (`ListAllUsersAsync` đọc `.FirstOrDefault()`) ⇒ **THAY THẾ** role cũ, không cộng dồn. Role mới == role cũ → **no-op**, cố ý **không** thu hồi token (đá người dùng khỏi phiên vì một thao tác chẳng đổi gì).
  - ⚠ **Allowlist tường minh, KHÔNG kiểm "role có trong bảng `roles` không"**: role là string tự do, `EnsureRoleExistsAsync` tạo **lazily** ⇒ một cái tên gõ sai vừa lọt kiểm tra tồn-tại vừa đẻ ra role rác mà không endpoint nào gác. Phân biệt hoa thường (`"admin"` → 400).
  - ⚠ **409 "còn thuộc org"** giữ bất biến *thành viên org ⇒ platform-role `Employer`* (`register-org` và A6 đều tạo `Employer`). Không chặn thì đây là **đường vòng qua guard "cấm hạ OrgAdmin cuối cùng" của A6b** — org mất sạch người lo billing/thành viên mà không cảnh báo gì; và JWT sẽ mang `org_id`+`org_role` trong khi platform-role không qua nổi endpoint `Employer` nào. Gỡ khỏi org trước (AUTH-8: việc của OrgAdmin), rồi mới đổi vai trò. Chiều **VÀO** `Employer` không bị chặn — nếu chặn thì không sửa nổi đúng trạng thái lệch mà guard sinh ra để bảo vệ.
  - Thu hồi **mọi refresh token** theo AUTH-5 ⇒ hiệu lực sau **≤1 TTL access (15')**, cùng ranh giới với ban (khối ⚠⚠ ngay trên).
  - *Follow-up:* **không có dấu vết ai đổi vai trò của ai** — Auth chưa có hạ tầng audit (khác `audit_logs` của Campaign) và không có cột kiểu `banned_by` cho việc này. Leo thang lên `Admin` vì thế hiện không truy được người ra quyết định.

**🔜 Admin — chưa build:**
- **`GET /auth/admin/users/{id}`** — xem chi tiết một user (nay chỉ có list).
- **`POST /auth/admin/users/{id}/revoke-sessions`** — đá phiên mà không cần ban/đổi mật khẩu (hiện revoke chỉ chạy kèm ban · reset-password · đổi role).
- **`GET/POST /auth/admin/orgs…`** — duyệt / khóa tổ chức (verify MST khi duyệt postpaid).
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
| 403 | role/`org_role` không đủ quyền (vd `HrMember` gọi admin/billing — A4) · **login của account bị đình chỉ** (F20 — 403 chứ không 401: thông tin đăng nhập ĐÚNG, cái bị từ chối là quyền dùng hệ thống; 401 sẽ khiến FE mời gõ lại mật khẩu vô ích) · `provision-candidate` cho account bị đình chỉ |
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
banned_at       timestamptz?  ✅ F20 — mốc PlatformAdmin ĐÌNH CHỈ account (null = đang hoạt động)
ban_reason      varchar(500)? ✅ F20 — lý do (chỉ hiển thị cho admin)
banned_by       uuid?         ✅ F20 — admin ra quyết định (ref lỏng → users.id, phục vụ đối chất)
```
+ cột Identity chuẩn: `security_stamp`, `lockout_*`, `phone_*`…

> ⚠ **`banned_at` ≠ `lockout_end`.** `lockout_*` là khoá **TỰ ĐỘNG** do nhập sai mật khẩu (`CheckPasswordSignInAsync(lockoutOnFailure: true)`), Identity tự đặt/xoá. Gộp ban của admin vào cột đó thì (a) không phân biệt được "bị cấm" với "gõ sai mật khẩu 5 lần", và (b) một lần đăng nhập thành công / reset mật khẩu sẽ **vô tình gỡ lệnh cấm**. Migration `AddUserBanF20` — thuần `AddColumn` nullable, **online-safe, không cần dọn dữ liệu trước**.

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
- **Tạo tài khoản là NGUYÊN TỬ**: 5 đường tạo account (`register` · `register-org` · đăng nhập Google lần đầu · `/internal/auth/provision-candidate` · `POST /auth/org/members`) chạy trong **một transaction** — hoặc tài khoản ra đời **đầy đủ** (`users` + `user_roles` + `organizations`/`org_members` + `user_logins` nếu có), hoặc **không có gì**. Trước đây mỗi đường ghi 3–5 lần `SaveChanges` rời rạc, lỗi giữa chừng để lại **user không role** (đăng nhập được nhưng mọi endpoint `[Authorize(Roles)]` trả 403) hoặc **Employer không thuộc org nào** (mất cả quyền lẫn billing — AUTH-8), mà **email đã UNIQUE nên không đăng ký/mời lại được để tự sửa** và không có job nào dọn. Kết quả `AddToRoleAsync` nay **được kiểm** — trước đây bị bỏ qua nên một thất bại không-ném vẫn commit user thiếu role trong im lặng.
  - ⚠ Transaction PHẢI mở qua **`Database.CreateExecutionStrategy()`**: `Program.cs` bật `EnableRetryOnFailure()`, và chiến lược retry của EF **từ chối transaction tự mở** ⇒ gọi thẳng `BeginTransactionAsync` sẽ ném ở **mọi** lần đăng ký trên Postgres trong khi test SQLite vẫn xanh (kiểu bug DB25b). Có test khoá hợp đồng này bằng một execution strategy giả có `RetriesOnFailure=true`.
  - ⚠ **`EnsureRoleExistsAsync` cố ý nằm NGOÀI transaction**: role là *dữ liệu tham chiếu* (Candidate/Employer), không phải dữ liệu của riêng tài khoản. Ở trong thì hai request đăng ký **đầu tiên** chạy song song sẽ đua tạo cùng role, bên thua đụng UNIQUE `roles.normalized_name` và **rollback theo cả việc tạo user** — một lần đăng ký hợp lệ bị 500 vì lý do không liên quan. Bên thua đua được xử lý là **thành công** (mục tiêu "role tồn tại" đã đạt).
  - ⚠ **`refresh_tokens` cố ý nằm NGOÀI transaction**: đó là trạng thái **phiên**, không phải trạng thái tài khoản. Hỏng ở bước đó để lại tài khoản đầy đủ và hợp lệ — người dùng chỉ cần đăng nhập lại. Với `register-org` thì việc này còn **bắt buộc**: token phải đọc `org_members` sau khi đã commit mới mang đúng `org_id`/`org_role` (A2).

## Xác thực (nguồn chân lý cho cả hệ)
- JWT phát bởi Auth, **các service khác validate bằng cùng** `Jwt:Key` / `Issuer` / `Audience` — **không** call Auth.
- Chỉ nên gọi Auth khi cần **dữ liệu tươi không có trong token** (vd email để xuất hóa đơn).
- `RoleClaimType = ClaimTypes.Role`, `MapInboundClaims = false` (giữ nguyên claim).
