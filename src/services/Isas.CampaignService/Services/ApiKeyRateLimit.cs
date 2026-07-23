using Isas.CampaignService.Models;
using System.Security.Claims;

namespace Isas.CampaignService.Services
{
    /// <summary>
    /// R2 — quyết định partition + trần cho rate-limiter Public API, tách khỏi Program.cs để test
    /// được thuần (không cần dựng WebApplicationFactory chỉ để check logic chọn bucket).
    ///
    /// VÌ SAO tách "key hợp lệ" khỏi "anonymous" ở TẦNG NÀY (không chỉ ở middleware xác thực):
    /// double-check — nếu vì lý do gì đó middleware pre-authenticate không chạy / claim rỗng, hàm
    /// này vẫn fallback đúng về "anonymous" (fail-closed về phía CHẶT hơn, không phải phía lỏng hơn).
    /// </summary>
    public static class ApiKeyRateLimit
    {
        public const string AnonymousPartitionKey = "anonymous";

        public readonly record struct Decision(string PartitionKey, int PermitLimit);

        public static Decision Resolve(ClaimsPrincipal user, ApiKeySettings settings)
        {
            var keyId = user.FindFirst(ApiKeyDefaults.KeyIdClaim)?.Value;

            if (!string.IsNullOrEmpty(keyId))
                return new Decision($"key:{keyId}", settings.RateLimitPermitsPerWindow);

            // Kẹp tối thiểu 1 — 0/âm ở đây vô tình tắt limiter cho toàn bộ request lạ, khác ý nghĩa
            // "0 = tắt" của RateLimitPermitsPerWindow (đó là kill-switch CÓ CHỦ Ý, cái này thì không).
            var anonLimit = Math.Max(1, settings.AnonymousRateLimitPermitsPerWindow);
            return new Decision(AnonymousPartitionKey, anonLimit);
        }
    }
}
