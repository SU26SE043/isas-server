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

        public DateTime? UpdatedAt { get; set; }
    }
}
