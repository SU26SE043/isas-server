namespace Isas.AuthService.Services
{
    /// <summary>
    /// A6 — email đã có account (đã là thành viên org này, hoặc đã đăng ký ở nơi khác — email UNIQUE).
    /// Controller map sang <c>409 Conflict</c>. Tách exception riêng để không lẫn với lỗi chung.
    /// </summary>
    public sealed class OrgMemberConflictException : Exception
    {
        public OrgMemberConflictException(string message) : base(message) { }
    }
}
