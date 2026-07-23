using System;
using System.Collections.Generic;
using System.Net;
using System.Text;

namespace Isas.CampaignService.Tests
{
    /// <summary>
    /// R2 — test qua pipeline THẬT: burst/isolation/header/mutation. Đây là lớp chứng minh mạnh nhất vì
    /// nó lắp đúng thứ tự middleware của Program.cs, không mock lại logic.
    /// </summary>
    public class PublicApiRateLimitR2Tests : IAsyncLifetime
    {
        private PublicApiRateLimitFactory _factory = null!;
        private HttpClient _client = null!;
        private string _rawKeyA = null!;

        public async Task InitializeAsync()
        {
            _factory = new PublicApiRateLimitFactory { PerKeyLimit = 3, AnonymousLimit = 2 };
            _rawKeyA = await _factory.SeedApiKeyAsync();
            _client = _factory.CreateClient();
        }

        public Task DisposeAsync()
        {
            _client.Dispose();
            _factory.Dispose();
            return Task.CompletedTask;
        }

        private HttpRequestMessage PublicRequest(string? apiKey = null)
        {
            var req = new HttpRequestMessage(HttpMethod.Get, "/campaign/public/campaigns");
            if (apiKey is not null)
                req.Headers.Add("X-Api-Key", apiKey);
            return req;
        }

        [Fact]
        public async Task ValidKey_GetsFullPerKeyQuota_ThenRejected()
        {
            for (var i = 0; i < _factory.PerKeyLimit; i++)
            {
                var res = await _client.SendAsync(PublicRequest(_factory.SeededRawKey));
                Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            }

            var rejected = await _client.SendAsync(PublicRequest(_factory.SeededRawKey));
            Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        }

        [Fact]
        public async Task KeyABurst_DoesNotLockOutKeyB()
        {
            // MẤU CHỐT của R2 — mutation-check bắt buộc RED nếu xoá middleware pre-authenticate: trước
            // sửa, A và B đều rơi "anonymous" chung → burst A khoá luôn B.
            for (var i = 0; i < _factory.PerKeyLimit; i++)
                await _client.SendAsync(PublicRequest(_rawKeyA));

            var exhausted = await _client.SendAsync(PublicRequest(_rawKeyA));
            Assert.Equal(HttpStatusCode.TooManyRequests, exhausted.StatusCode);

            var rawKeyB = await _factory.SeedApiKeyAsync();
            var stillOk = await _client.SendAsync(PublicRequest(rawKeyB));
            Assert.Equal(HttpStatusCode.OK, stillOk.StatusCode);
        }

        [Fact]
        public async Task AnonymousBurst_HasSeparateStricterBucket_ThenRejected()
        {
            for (var i = 0; i < _factory.AnonymousLimit; i++)
            {
                var res = await _client.SendAsync(PublicRequest(apiKey: null));
                Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode); // thiếu header → 401 (đúng, không đổi)
            }

            var rejected = await _client.SendAsync(PublicRequest(apiKey: null));
            Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        }

        [Fact]
        public async Task AnonymousBurst_DoesNotLockOutValidKey()
        {
            // Đây là test trực tiếp chứng minh bug R2 gốc ĐÃ hết: trước khi sửa, việc này sẽ FAIL vì
            // anonymous burst dùng CHUNG bucket với key thật.
            for (var i = 0; i < _factory.AnonymousLimit + 3; i++)
                await _client.SendAsync(PublicRequest(apiKey: null));

            var stillOk = await _client.SendAsync(PublicRequest(_factory.SeededRawKey));
            Assert.Equal(HttpStatusCode.OK, stillOk.StatusCode);
        }

        [Fact]
        public async Task Rejected429_CarriesRetryAfterAndRemainingHeader()
        {
            for (var i = 0; i < _factory.PerKeyLimit; i++)
                await _client.SendAsync(PublicRequest(_factory.SeededRawKey));

            var rejected = await _client.SendAsync(PublicRequest(_factory.SeededRawKey));

            Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
            Assert.True(rejected.Headers.RetryAfter is not null, "Thiếu header Retry-After.");
            Assert.Equal("0", rejected.Headers.GetValues("X-RateLimit-Remaining").Single());
        }

        [Fact]
        public async Task RandomInvalidKeys_AllLandInSingleAnonymousBucket_NotUnboundedPartitions()
        {
            // Chống DoS-đổi-chiều: nếu ai đó lỡ sửa code partition theo RAW HEADER thay vì theo
            // claim/kết quả xác thực, test này sẽ FAIL vì mỗi key rác sẽ có bucket riêng → không giới
            // hạn được gì (không exhaust bucket nào trong AnonymousLimit request).
            for (var i = 0; i < _factory.AnonymousLimit; i++)
            {
                var randomKey = $"garbage-{Guid.NewGuid()}";
                var res = await _client.SendAsync(PublicRequest(randomKey));
                Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
            }

            var oneMoreRandomKey = $"garbage-{Guid.NewGuid()}";
            var rejected = await _client.SendAsync(PublicRequest(oneMoreRandomKey));
            Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        }
    }
}
