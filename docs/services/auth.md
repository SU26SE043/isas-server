# AuthService

> Định danh & phát JWT cho toàn hệ. Code: `src/services/Isas.AuthService`. DB: `isas`. Gateway: `/api/v1/auth`.
> Quy ước chung (snake_case, JWT key, ...): [../architecture.md](../architecture.md) §5. Phân việc: [../work-division.md](../work-division.md).

## Vai trò
- Đăng ký / đăng nhập (email + mật khẩu, liên kết **Google OAuth**), phát **JWT access + refresh token**, profile.
- **3 platform role đã có: `Candidate` / `Employer` / `Admin`** (gắn vào claim JWT). `Admin` ở đây = **PlatformAdmin** (quản trị hệ thống: duyệt postpaid, quản gói), **khác** org-admin bên dưới.
- `register` hiện **auto gán `Candidate`**. **Gap:** luồng **cấp role Employer** (khi tạo/đăng ký tổ chức).
- Các service khác **không gọi Auth lúc chạy** — validate JWT **offline** bằng chung key.

## Organization & phân quyền nội bộ (multi-tenant) 🟡 thiết kế, RBAC đầy đủ = phase 2
B2B bán cho **doanh nghiệp**, không phải cá nhân → cần khái niệm **tổ chức**:
- Một **Organization** (`org_id`) = 1 doanh nghiệp; **billing/credit gắn org** (xem [payment.md](payment.md)), **campaign gắn `org_id`**.
- **Role nội bộ org** (claim kèm trong JWT): **`OrgAdmin`** (mua gói/trả tiền/xem billing, quản thành viên) vs **`HrMember`** (tạo & quản campaign, **không** xem billing).
- **Phase 1 (capstone):** data model có `org_id` + `org_role` từ đầu; có thể demo **1 org = 1 OrgAdmin**. **Sub-account HR đầy đủ (mời thành viên, phân quyền chi tiết) = phase 2.**

> **Admin KHÔNG phải service riêng.** Chức năng PlatformAdmin = endpoint **admin-gated** nằm trong service sở hữu dữ liệu — **Payment**: CRUD gói, đơn giá, duyệt/đình chỉ postpaid, xem giao dịch, cấp/hoàn credit; **Auth**: cấp role, quản tổ chức (verify MST khi duyệt postpaid) — cộng **1 FE admin dashboard**. **Giám sát/thống kê nền tảng** (#org · #campaign · #lượt phỏng vấn · doanh thu) = dashboard tổng hợp từ các service (*phase 2*). Không thêm AdminService (tránh coupling + phá Engine+Orchestrator).

## API — `/api/v1/auth`
| Method | Path | Auth | Mô tả |
|---|---|---|---|
| POST | `/register` | — | Đăng ký, trả token (role mặc định `Candidate`) |
| POST | `/login` | — | Đăng nhập, trả access + refresh token |
| POST | `/refresh` | — | Làm mới access token bằng refresh token |
| POST | `/logout` | — | Thu hồi refresh token |
| GET | `/me` | ✅ | Lấy profile |
| PUT | `/me` | ✅ | Cập nhật profile |
| POST | `/auth/admin/users/{id}/roles` | **Admin** | Gán/thu platform role (vd nâng user → Employer) |
| GET/POST | `/auth/admin/orgs…` | **Admin** | Xem / duyệt / khóa tổ chức |

## DB — `isas`
ASP.NET Identity. Bảng: `users` · `roles` · `user_roles` · `role_claims` · refresh tokens.

- **users** — email, hash mật khẩu, hồ sơ, liên kết Google OAuth.
- **roles / user_roles** — 3 platform role Candidate/Employer/Admin.
- **refresh tokens** — cơ chế JWT, thời hạn `Jwt:RefreshTokenDays`.
- **organizations** (B2B) — `id` · `name` · `tax_code?` (MST cho hóa đơn) · `created_at`.
- **org_members** — `org_id` · `user_id` · `org_role` (`OrgAdmin`/`HrMember`). JWT của Employer mang kèm `org_id` + `org_role`.

## Xác thực (nguồn chân lý cho cả hệ)
- JWT phát bởi Auth, **các service khác validate bằng cùng** `Jwt:Key` / `Issuer` / `Audience` — **không** call Auth.
- Chỉ nên gọi Auth khi cần **dữ liệu tươi không có trong token** (vd email để xuất hóa đơn).
- `RoleClaimType = ClaimTypes.Role`, `MapInboundClaims = false` (giữ nguyên claim).
