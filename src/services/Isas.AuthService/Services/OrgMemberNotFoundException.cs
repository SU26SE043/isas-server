namespace Isas.AuthService.Services
{
    /// <summary>
    /// A6b — thao tác trên thành viên không thuộc org của caller (đổi role / xoá).
    /// Controller map sang <c>404 Not Found</c>. Tách exception riêng để không lẫn với lỗi chung.
    /// </summary>
    public sealed class OrgMemberNotFoundException : Exception
    {
        public OrgMemberNotFoundException(string message) : base(message) { }
    }
}
