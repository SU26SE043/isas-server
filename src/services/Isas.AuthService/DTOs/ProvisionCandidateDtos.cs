namespace Isas.AuthService.DTOs
{
    /// <summary>
    /// D2 — provision Candidate nhẹ (internal, máy-máy). CampaignService gọi khi ứng viên bấm
    /// "Join Campaign" từ magic-link: tạo-hoặc-lấy account Candidate theo email (danh tính B2B =
    /// magic-link → account Candidate nhẹ, INT-13/D8 — KHÔNG token-as-identity, PHẢI có account + JWT thật).
    /// </summary>
    public class ProvisionCandidateRequest
    {
        public string Email { get; set; } = null!;
        public string? FullName { get; set; }
    }

    /// <summary>Kết quả provision: candidateId (dùng làm ref lỏng xuyên service) + JWT Candidate.</summary>
    public class ProvisionCandidateResponse
    {
        public Guid CandidateId { get; set; }
        public string AccessToken { get; set; } = null!;
    }
}
