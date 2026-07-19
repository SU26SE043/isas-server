using Isas.CampaignService.DTOs;

namespace Isas.CampaignService.Services
{
    /// <summary>F17 — vòng đời API key bên thứ ba + xác thực key khi Public API được gọi.</summary>
    public interface IApiKeyService
    {
        /// <summary>Tạo key cho org. Trả key THÔ đúng một lần (không lưu, không đọc lại được).</summary>
        Task<CreatedApiKeyResponse> CreateAsync(
            Guid orgId, Guid actorUserId, CreateApiKeyRequest req, CancellationToken ct);

        /// <summary>Liệt kê key của org — không bao giờ kèm key thô/hash.</summary>
        Task<List<ApiKeyResponse>> ListAsync(Guid orgId, CancellationToken ct);

        /// <summary>Thu hồi (soft) — key thuộc org khác/không tồn tại → KeyNotFoundException (404).</summary>
        Task RevokeAsync(Guid orgId, Guid actorUserId, Guid keyId, CancellationToken ct);

        /// <summary>
        /// Xác thực key thô. Trả null khi: sai định dạng / không khớp hash nào / đã revoke / quá hạn.
        /// KHÔNG phân biệt lý do ra ngoài — mọi ca đều 401 để không xác nhận hộ "key này từng tồn tại".
        /// </summary>
        Task<ApiKeyPrincipal?> AuthenticateAsync(string? rawKey, CancellationToken ct);
    }

    /// <summary>Danh tính đã xác thực của một API key — org scope + quyền đọc PII.</summary>
    public sealed record ApiKeyPrincipal(Guid KeyId, Guid OrgId, bool IncludePii);
}
