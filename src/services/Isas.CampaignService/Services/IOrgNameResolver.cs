namespace Isas.CampaignService.Services
{
    /// <summary>
    /// CMP1-B1 — resolve tên tổ chức theo org_id qua AuthService (máy-máy, X-Internal-Token, KHÔNG
    /// gateway). CampaignService chỉ giữ org_id (GEN-2: không FK xuyên service), nên trang lời mời
    /// phải hỏi Auth để hiển thị tên công ty mời.
    ///
    /// <para><b>FAIL-SOFT là hợp đồng, không phải tuỳ chọn:</b> Auth lỗi / timeout / 404 ⇒ trả
    /// <c>null</c>, KHÔNG ném. Trang lời mời phải mở được cho ứng viên ẩn danh kể cả khi Auth chết.</para>
    /// </summary>
    public interface IOrgNameResolver
    {
        Task<string?> ResolveOrgNameAsync(Guid orgId, CancellationToken ct = default);
    }
}
