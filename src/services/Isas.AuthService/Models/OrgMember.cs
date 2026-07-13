namespace Isas.AuthService.Models
{
    public enum OrgRole
    {
        OrgAdmin,
        HrMember
    }

    public class OrgMember
    {
        public Guid OrgId { get; set; }

        public Guid UserId { get; set; }

        public OrgRole OrgRole { get; set; }

        // A6b: thời điểm thật user gia nhập org (trước đây list dùng proxy User.CreatedAt).
        public DateTime JoinedAt { get; set; }

        public Organization Organization { get; set; } = default!;

        public User User { get; set; } = default!;
    }
}
