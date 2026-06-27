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

        public Organization Organization { get; set; } = default!;

        public User User { get; set; } = default!;
    }
}
