using Isas.CampaignService.Models;
using Isas.CampaignService.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Isas.CampaignService.Tests
{
    /// <summary>
    /// R2 — dựng pipeline THẬT (Program.cs) trong-process để test rate-limit end-to-end, thay vì mock
    /// middleware. SQLite in-memory thay Postgres; KHÔNG cần RabbitMQ — các publisher trong Program.cs
    /// tạo ConnectionFactory LƯỜI (lazy), hosted-service consumer chỉ connect trong ExecuteAsync, không
    /// có AddSingleton&lt;IConnection&gt; nào eager-connect lúc khởi động.
    /// </summary>
    public sealed class PublicApiRateLimitFactory : WebApplicationFactory<Program>
    {
        private readonly SqliteConnection _connection = new("DataSource=:memory:");

        public int PerKeyLimit { get; init; } = 3;
        public int AnonymousLimit { get; init; } = 2;
        public int WindowSeconds { get; init; } = 60;

        /// <summary>Key thô để test gọi (client tự set header X-Api-Key). Set sau khi factory khởi tạo xong.</summary>
        public string SeededRawKey { get; private set; } = null!;
        public Guid SeededOrgId { get; } = Guid.NewGuid();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Jwt:Key"] = "test-signing-key-at-least-32-characters-long!!",
                    ["Jwt:Issuer"] = "isas-test",
                    ["Jwt:Audience"] = "isas-test",
                    ["ApiKeys:RateLimitPermitsPerWindow"] = PerKeyLimit.ToString(),
                    ["ApiKeys:AnonymousRateLimitPermitsPerWindow"] = AnonymousLimit.ToString(),
                    ["ApiKeys:RateLimitWindowSeconds"] = WindowSeconds.ToString(),
                    // Không dùng AiService/Auth/Interview thật — BaseUrl không cần đúng vì test không
                    // chạm endpoint nào gọi các HttpClient đó.
                    ["AiService:BaseUrl"] = "http://localhost:9",
                    ["Auth:BaseUrl"] = "http://localhost:9",
                    ["Interview:BaseUrl"] = "http://localhost:9",
                    ["SeaweedFS:ServiceURL"] = "http://localhost:9",
                    ["SeaweedFS:AccessKey"] = "x",
                    ["SeaweedFS:SecretKey"] = "x",
                });
            });

            builder.ConfigureServices(services =>
            {
                var dbContextRelatedDescriptors = services
                    .Where(d => d.ServiceType.IsGenericType
                                && d.ServiceType.GetGenericArguments().Contains(typeof(CampaignDbContext)))
                    .ToList();
                foreach (var d in dbContextRelatedDescriptors)
                    services.Remove(d);

                _connection.Open();
                services.AddDbContext<CampaignDbContext>(options =>
                    options.UseSqlite(_connection).UseSnakeCaseNamingConvention());

                var hostedServiceDescriptors = services
                    .Where(d => d.ServiceType == typeof(IHostedService)
                                && d.ImplementationType?.Namespace?.StartsWith("Isas.CampaignService") == true)
                    .ToList();
                foreach (var d in hostedServiceDescriptors)
                    services.Remove(d);
            });

            builder.UseEnvironment("Testing");
        }

        /// <summary>
        /// Gọi 1 lần sau khi factory tạo xong (Server đã build) để seed DB + sinh key thô.
        /// TODO XÁC NHẬN VỚI BẠN: cách hash key thô hiện tại (Services/ApiKeys.cs — tôi chưa có file
        /// này) và tên DbSet<ApiKey> trên CampaignDbContext. Tạm viết theo giả định hợp lý nhất, ĐỪNG
        /// chạy thật cho tới khi bạn xác nhận / sửa đúng.
        /// </summary>
        public async Task<string> SeedApiKeyAsync(Guid? orgId = null)
        {
            using var scope = Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<CampaignDbContext>();
            await db.Database.EnsureCreatedAsync();

            var rawKey = ApiKeys.NewRawKey();

            db.ApiKeys.Add(new ApiKey
            {
                Id = Guid.NewGuid(),
                OrgId = orgId ?? SeededOrgId,
                Name = "R2 test key",
                KeyHash = ApiKeys.Hash(rawKey),
                KeyPrefix = ApiKeys.DisplayPrefix(rawKey),
                IncludePii = false,
                CreatedByUserId = Guid.NewGuid(),
                CreatedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddDays(30),
                LastUsedAt = null,
                RevokedAt = null
            });

            await db.SaveChangesAsync();
            return rawKey;
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing) _connection.Dispose();
        }
    }
}
