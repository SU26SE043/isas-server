using System.ComponentModel.DataAnnotations;

namespace Isas.AuthService.DTOs
{
    /// <summary>GET /auth/org — thông tin tổ chức của caller (mọi member org đọc được).</summary>
    public class OrganizationResponse
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = null!;
        public string? TaxCode { get; set; }
        public DateTime CreatedAt { get; set; }
        public int MemberCount { get; set; }
    }

    /// <summary>
    /// GET /internal/auth/organizations/{orgId} — resolve tên tổ chức máy-máy (X-Internal-Token, KHÔNG
    /// qua gateway). CampaignService gọi để điền <c>orgName</c> trên trang lời mời (CMP1-B1).
    /// Chỉ trả những gì caller cần: id + name (không lộ taxCode/memberCount cho luồng nội bộ này).
    /// </summary>
    public record InternalOrganizationResponse(Guid Id, string Name);

    /// <summary>PUT /auth/org — OrgAdmin sửa tên/mã số thuế (chỉ trường gửi lên).</summary>
    public class UpdateOrgRequest
    {
        [MinLength(1)]
        public string? Name { get; set; }

        public string? TaxCode { get; set; }
    }
}
