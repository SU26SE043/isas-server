using Isas.CampaignService.Models;
using Isas.CampaignService.Services;
using System.Security.Claims;

namespace Isas.CampaignService.Tests
{
    public class ApiKeyRateLimitResolveR2Tests
    {
        private static ApiKeySettings DefaultSettings() => new()
        {
            RateLimitPermitsPerWindow = 60,
            AnonymousRateLimitPermitsPerWindow = 10,
            RateLimitWindowSeconds = 60
        };

        private static ClaimsPrincipal PrincipalWithKeyId(string? keyId)
        {
            var identity = new ClaimsIdentity(keyId is null ? null : ApiKeyDefaults.Scheme);
            if (keyId is not null)
                identity.AddClaim(new Claim(ApiKeyDefaults.KeyIdClaim, keyId));
            return new ClaimsPrincipal(identity);
        }

        [Fact]
        public void Resolve_ValidKeyClaim_ReturnsPerKeyPartitionWithFullLimit()
        {
            var keyId = Guid.NewGuid().ToString();
            var decision = ApiKeyRateLimit.Resolve(PrincipalWithKeyId(keyId), DefaultSettings());

            Assert.Equal($"key:{keyId}", decision.PartitionKey);
            Assert.Equal(60, decision.PermitLimit);
        }

        [Fact]
        public void Resolve_NoClaim_ReturnsAnonymousPartitionWithStricterLimit()
        {
            var decision = ApiKeyRateLimit.Resolve(PrincipalWithKeyId(null), DefaultSettings());

            Assert.Equal(ApiKeyRateLimit.AnonymousPartitionKey, decision.PartitionKey);
            Assert.Equal(10, decision.PermitLimit);
            Assert.True(decision.PermitLimit < 60, "Anonymous phải chặt hơn per-key — đúng mục tiêu R2.");
        }

        [Fact]
        public void Resolve_TwoDistinctKeys_ProduceDistinctPartitions()
        {
            var settings = DefaultSettings();
            var a = ApiKeyRateLimit.Resolve(PrincipalWithKeyId("key-a"), settings);
            var b = ApiKeyRateLimit.Resolve(PrincipalWithKeyId("key-b"), settings);

            Assert.NotEqual(a.PartitionKey, b.PartitionKey);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-5)]
        public void Resolve_AnonymousLimitZeroOrNegative_ClampedToAtLeastOne(int configuredAnonLimit)
        {
            var settings = DefaultSettings();
            settings.AnonymousRateLimitPermitsPerWindow = configuredAnonLimit;

            var decision = ApiKeyRateLimit.Resolve(PrincipalWithKeyId(null), settings);

            // R2 — 0/âm ở đây KHÔNG được hiểu là "tắt" (khác RateLimitPermitsPerWindow, nơi ≤0 = tắt
            // có chủ đích). Anonymous là bucket bảo vệ chống đúng loại tấn công đang sửa, không cho tắt
            // ngoài ý muốn qua cấu hình sai.
            Assert.True(decision.PermitLimit >= 1);
        }

        [Fact]
        public void Resolve_EmptyStringKeyClaim_TreatedAsMissing_FallsBackToAnonymous()
        {
            // Phòng ca claim tồn tại nhưng rỗng (vd lỗi gán claim ở nơi khác) — không để lọt qua thành
            // partition "key:" (rỗng) trộn lẫn nhiều request khác nhau vào 1 bucket vô nghĩa.
            var identity = new ClaimsIdentity(ApiKeyDefaults.Scheme);
            identity.AddClaim(new Claim(ApiKeyDefaults.KeyIdClaim, ""));
            var principal = new ClaimsPrincipal(identity);

            var decision = ApiKeyRateLimit.Resolve(principal, DefaultSettings());

            Assert.Equal(ApiKeyRateLimit.AnonymousPartitionKey, decision.PartitionKey);
        }
    }
}
