namespace Isas.AuthService.DTOs
{
    public class RefreshTokenResponse
    {
        public string RefreshToken { get; set; }
        public DateTime ExpiresAt { get; set; }
    }
}
