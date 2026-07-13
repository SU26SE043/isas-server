namespace Isas.CampaignService.Services
{
    /// <summary>D2 — gọi AuthService /internal/auth/provision-candidate (máy-máy, X-Internal-Token, KHÔNG gateway).</summary>
    public interface IAuthProvisionClient
    {
        Task<ProvisionedCandidate> ProvisionCandidateAsync(string email, string? fullName, CancellationToken ct = default);
    }

    /// <summary>Kết quả provision: candidateId (ref lỏng → Auth) + JWT Candidate trả thẳng cho ứng viên.</summary>
    public record ProvisionedCandidate(Guid CandidateId, string AccessToken);
}
