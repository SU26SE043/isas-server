namespace Isas.AuthService.DTOs
{
    public class UserResponse
    {
        public string Id { get; set; }

        public string FullName { get; set; }

        public string Email { get; set; }

        public string Location { get; set; }

        public string Title { get; set; }

        public DateTime CreatedAt { get; set; } 

        public string Role { get; set; }

        public Guid? OrgId { get; set; }
        public string? OrgName { get; set; }
        public string? OrgRole { get; set; }
    }
}
