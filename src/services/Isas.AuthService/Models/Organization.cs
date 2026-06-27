namespace Isas.AuthService.Models
{
    public class Organization
    {
        public Guid Id { get; set; }

        public string Name { get; set; } = null!;

        public string? TaxCode { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        public ICollection<OrgMember> Members { get; set; } = new List<OrgMember>();
    }
}
