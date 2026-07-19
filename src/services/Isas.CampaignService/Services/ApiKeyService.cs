using Isas.CampaignService.DTOs;
using Isas.CampaignService.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Isas.CampaignService.Services
{
    /// <summary>
    /// F17 — vòng đời API key (tạo/liệt kê/thu hồi) + xác thực key cho Public API.
    /// </summary>
    public class ApiKeyService : IApiKeyService
    {
        private readonly CampaignDbContext _db;
        private readonly ApiKeySettings _settings;
        private readonly ILogger<ApiKeyService> _logger;

        public ApiKeyService(
            CampaignDbContext db, IOptions<ApiKeySettings> settings, ILogger<ApiKeyService> logger)
        {
            _db = db;
            _settings = settings.Value;
            _logger = logger;
        }

        public async Task<CreatedApiKeyResponse> CreateAsync(
            Guid orgId, Guid actorUserId, CreateApiKeyRequest req, CancellationToken ct)
        {
            var name = req.Name?.Trim();
            if (string.IsNullOrWhiteSpace(name))
                throw new InvalidOperationException("Name là bắt buộc.");

            // Hạn LUÔN có (bài học DB23: hạn nullable ⇒ credential vĩnh viễn). Trần chặn key sống quá lâu.
            var days = req.ExpiresInDays ?? _settings.DefaultExpiryDays;
            if (days <= 0 || days > _settings.MaxExpiryDays)
                throw new InvalidOperationException(
                    $"expiresInDays phải trong khoảng 1..{_settings.MaxExpiryDays} (đang gửi: {days}).");

            var now = DateTime.UtcNow;

            // Trần số key ACTIVE/org — key càng nhiều càng khó biết cái nào còn dùng ⇒ càng ngại revoke.
            var activeCount = await _db.ApiKeys.CountAsync(
                k => k.OrgId == orgId && k.RevokedAt == null && k.ExpiresAt > now, ct);
            if (activeCount >= _settings.MaxActiveKeysPerOrg)
                throw new InvalidOperationException(
                    $"Org đã đạt trần {_settings.MaxActiveKeysPerOrg} API key đang hoạt động — thu hồi bớt trước khi tạo mới.");

            var rawKey = ApiKeys.NewRawKey();
            var entity = new ApiKey
            {
                Id = Guid.NewGuid(),
                OrgId = orgId,
                Name = name,
                KeyHash = ApiKeys.Hash(rawKey),
                KeyPrefix = ApiKeys.DisplayPrefix(rawKey),
                IncludePii = req.IncludePii,
                CreatedByUserId = actorUserId,
                CreatedAt = now,
                ExpiresAt = now.AddDays(days)
            };

            _db.ApiKeys.Add(entity);
            _db.AuditLogs.Add(new AuditLog
            {
                Id = Guid.NewGuid(),
                OrgId = orgId,
                ActorUserId = actorUserId,
                Action = AuditAction.CreateApiKey,
                Entity = "ApiKey",
                EntityId = entity.Id,
                // KHÔNG log key thô/hash vào audit — audit đọc được rộng hơn bảng key.
                Summary = $"Tạo API key '{name}' (prefix {entity.KeyPrefix}, pii={entity.IncludePii}, hạn {entity.ExpiresAt:u})",
                At = now
            });
            await _db.SaveChangesAsync(ct);

            return new CreatedApiKeyResponse
            {
                Id = entity.Id,
                Name = entity.Name,
                Key = rawKey,                 // lần DUY NHẤT key thô rời khỏi server
                KeyPrefix = entity.KeyPrefix,
                IncludePii = entity.IncludePii,
                ExpiresAt = entity.ExpiresAt,
                CreatedAt = entity.CreatedAt
            };
        }

        public async Task<List<ApiKeyResponse>> ListAsync(Guid orgId, CancellationToken ct)
        {
            var now = DateTime.UtcNow;
            // .Select trong SQL: cột key_hash KHÔNG được nạp lên (mẫu GET /files — slim tại SQL,
            // map sau khi nạp entity thì cột vẫn bị đọc lên, không đạt mục đích).
            return await _db.ApiKeys
                .Where(k => k.OrgId == orgId)
                .OrderByDescending(k => k.CreatedAt)
                .Select(k => new ApiKeyResponse
                {
                    Id = k.Id,
                    Name = k.Name,
                    KeyPrefix = k.KeyPrefix,
                    IncludePii = k.IncludePii,
                    CreatedAt = k.CreatedAt,
                    ExpiresAt = k.ExpiresAt,
                    LastUsedAt = k.LastUsedAt,
                    RevokedAt = k.RevokedAt,
                    IsActive = k.RevokedAt == null && k.ExpiresAt > now
                })
                .ToListAsync(ct);
        }

        public async Task RevokeAsync(Guid orgId, Guid actorUserId, Guid keyId, CancellationToken ct)
        {
            // Kẹp org NGAY trong vị ngữ: org A không revoke được key org B (và không phân biệt được
            // "key org khác" với "không tồn tại" — cả hai đều 404).
            var key = await _db.ApiKeys
                .FirstOrDefaultAsync(k => k.Id == keyId && k.OrgId == orgId, ct)
                ?? throw new KeyNotFoundException($"API key {keyId} not found.");

            if (key.RevokedAt != null) return;   // idempotent — revoke lại không lỗi

            var now = DateTime.UtcNow;
            key.RevokedAt = now;
            _db.AuditLogs.Add(new AuditLog
            {
                Id = Guid.NewGuid(),
                OrgId = orgId,
                ActorUserId = actorUserId,
                Action = AuditAction.RevokeApiKey,
                Entity = "ApiKey",
                EntityId = key.Id,
                Summary = $"Thu hồi API key '{key.Name}' (prefix {key.KeyPrefix})",
                At = now
            });
            await _db.SaveChangesAsync(ct);
        }

        public async Task<ApiKeyPrincipal?> AuthenticateAsync(string? rawKey, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(rawKey)) return null;
            if (!rawKey.StartsWith(ApiKeys.Prefix, StringComparison.Ordinal)) return null;

            // Tra bằng HASH, không phải key thô. Lưu ý về so-khớp hằng-thời-gian: KHÔNG cần ở đây,
            // khác với X-Internal-Token (Payment/Interview, commit 0a55343) — chỗ đó so trực tiếp
            // secret-với-secret bằng `==` nên timing rò rỉ từng byte. Ở đây đầu vào bị SHA-256 TRƯỚC
            // khi chạm DB, nên thời gian probe B-tree phụ thuộc hash của key kẻ tấn công tự chọn,
            // không phụ thuộc key thật; hash của key đoán sai không "gần" hash key đúng theo bất kỳ
            // nghĩa nào khai thác được. Thêm FixedTimeEquals sau khi DB đã so xong = trang trí.
            var hash = ApiKeys.Hash(rawKey);
            var now = DateTime.UtcNow;

            var key = await _db.ApiKeys
                .Where(k => k.KeyHash == hash)
                .Select(k => new { k.Id, k.OrgId, k.IncludePii, k.RevokedAt, k.ExpiresAt, k.LastUsedAt })
                .FirstOrDefaultAsync(ct);

            // Mọi lý do hỏng → null (= 401 trống). Không nói "key đã hết hạn" vs "key không tồn tại":
            // phân biệt là xác nhận hộ kẻ tấn công rằng chuỗi họ cầm từng là key thật.
            if (key is null) return null;
            if (key.RevokedAt != null)
            {
                _logger.LogWarning("API key {KeyId} đã thu hồi nhưng vẫn được dùng.", key.Id);
                return null;
            }
            if (key.ExpiresAt <= now)
            {
                _logger.LogInformation("API key {KeyId} đã hết hạn.", key.Id);
                return null;
            }

            await TouchAsync(key.Id, key.LastUsedAt, now, ct);
            return new ApiKeyPrincipal(key.Id, key.OrgId, key.IncludePii);
        }

        /// <summary>
        /// Ghi <c>last_used_at</c> CÓ TIẾT CHẾ: chỉ khi lần ghi trước cũ hơn TouchThrottleMinutes.
        /// Vị ngữ lặp lại điều kiện thời gian trong SQL để 2 request song song không cùng ghi.
        /// Lỗi ở đây KHÔNG được làm hỏng request — đây là telemetry, không phải quyết định auth.
        /// </summary>
        private async Task TouchAsync(Guid keyId, DateTime? lastUsedAt, DateTime now, CancellationToken ct)
        {
            var cutoff = now.AddMinutes(-_settings.TouchThrottleMinutes);
            if (lastUsedAt is not null && lastUsedAt > cutoff) return;

            try
            {
                await _db.ApiKeys
                    .Where(k => k.Id == keyId && (k.LastUsedAt == null || k.LastUsedAt <= cutoff))
                    .ExecuteUpdateAsync(s => s.SetProperty(k => k.LastUsedAt, now), ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Không ghi được last_used_at cho API key {KeyId}.", keyId);
            }
        }
    }
}
