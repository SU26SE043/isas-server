namespace Isas.AuthService.DTOs
{
    public class AuthResponse
    {
        public string AccessToken { get; set; }

        public string RefreshToken { get; set; }

        public DateTime ExpiresAt { get; set; }
    }
}
