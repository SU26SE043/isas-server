namespace Isas.AuthService.Services
{
    /// <summary>
    /// F20 (FR16) — account đã bị PlatformAdmin đình chỉ: KHÔNG phát phiên mới cho account này.
    /// Ném ở MỌI đường phát token (đăng nhập mật khẩu · đăng nhập Google · refresh · provision
    /// candidate D2), không phải chỉ ở controller đăng nhập — xem ghi chú
    /// <c>AuthService.EnsureNotBannedAsync</c>.
    /// </summary>
    public class UserBannedException : Exception
    {
        public UserBannedException(string message) : base(message) { }
    }

    /// <summary>
    /// F20 — thao tác quản trị vi phạm bất biến hệ thống (vd: đình chỉ Admin CUỐI CÙNG còn hoạt
    /// động → không còn ai gỡ ban được cho ai). Map ra 409, mẫu <c>OrgMemberConflictException</c>
    /// của A6b ("hạ cấp OrgAdmin cuối").
    /// </summary>
    public class AdminActionConflictException : Exception
    {
        public AdminActionConflictException(string message) : base(message) { }
    }
}
