using Isas.CampaignService.DTOs;

namespace Isas.CampaignService.Services
{
    /// <summary>Kết quả callback <c>cv-result</c> (dùng cho log — controller luôn trả 204).</summary>
    public enum CvResultOutcome { Analyzed, SkippedInvited }

    /// <summary>Kết quả callback <c>cv-failed</c> (dùng cho log — controller luôn trả 204).</summary>
    public enum CvFailedOutcome { Failed, SkippedInvited, SkippedAnalyzed }

    /// <summary>
    /// C14 — Sàng CV B2B async: publish job AI chấm khớp, nhận callback ghi điểm, shortlist + PATCH.
    /// Tách khỏi <see cref="CampaignService"/> để KHÔNG đổi constructor service cũ (C13 test giữ nguyên).
    /// TÁI DÙNG <c>campaign_criteria</c> làm rubric; 0 credit (D19).
    /// </summary>
    public interface ICvScreeningService
    {
        // Mỗi candidate Filtered → publish cv_screening_queue → Analyzing (+ last_screening_published_at).
        Task<int> PublishScreeningJobsAsync(Guid employerId, Guid campaignId, CancellationToken ct);

        // Callback cv-result: ghi candidate_criterion_scores + overall_match_score → Analyzed (idempotent).
        Task<CvResultOutcome> SaveCvResultAsync(Guid candidateId, CvResultCallbackRequest req, CancellationToken ct);

        // Callback cv-failed: → AnalysisFailed (+ reason). Đã Analyzed/Invited → no-op (không hạ cấp).
        Task<CvFailedOutcome> MarkCvFailedAsync(Guid candidateId, string? reason, CancellationToken ct);

        // Shortlist: mặc định sort=score DESC (overall_match_score). Lọc status/minScore/skill. Ngoài org → 404.
        Task<List<CandidateListItem>> GetCandidatesAsync(
            Guid employerId, Guid campaignId, string? status, int? minScore, string? skill, string? sort, CancellationToken ct);

        // Chi tiết 1 ứng viên + điểm từng tiêu chí. Ngoài org / không tồn tại → 404.
        Task<CandidateDetailResponse> GetCandidateAsync(Guid employerId, Guid campaignId, Guid candidateId, CancellationToken ct);

        // PATCH email/fullName (parse thiếu) → audit_logs. Đã Invited → 409. Trùng email → 400.
        Task PatchCandidateAsync(Guid employerId, Guid campaignId, Guid candidateId, PatchCandidateRequest req, CancellationToken ct);
    }
}
