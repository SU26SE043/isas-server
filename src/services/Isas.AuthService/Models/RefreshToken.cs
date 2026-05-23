namespace Isas.AuthService.Models
{
    public class RefreshToken
    {
        public Guid Id { get; set; }

        public Guid UserId { get; set; }

        public string Token { get; set; } = null!;

        public bool IsRevoked { get; set; } = false;

        public Guid? ReplacedBy { get; set; }

        public DateTime ExpiresAt { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public User User { get; set; } = default!;
    }
}
