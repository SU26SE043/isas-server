using Isas.CampaignService.DTOs;
using Isas.CampaignService.Models;
using Isas.Shared.Pagination;

namespace Isas.CampaignService.Services
{
    // BK4: ownership/filter theo `orgId` (ORG sở hữu campaign, AUTH-8). Method có ghi audit nhận thêm
    // `actorUserId` (cá nhân HR thao tác = user sub) — audit giữ danh tính người, KHÔNG phải org.
    public interface ICampaignService
    {
        Task<CampaignResponse> GetCampaignAsync(Guid orgId, Guid id, CancellationToken ct);
        Task<List<CampaignResponse>> GetCampaignsAsync(Guid orgId, CancellationToken ct);

        // AUTH-7: PlatformAdmin oversight — MỌI campaign xuyên org (KHÔNG lọc org), read-only. Tôn trọng
        // soft-delete (D11). Optional lọc status/orgId. Cap 500, mới nhất trước.
        Task<KeysetPage<AdminCampaignListItem>> ListAllCampaignsAsync(string? status, Guid? orgId, string? cursor, int? limit, CancellationToken ct);
        Task<CampaignResponse> CreateCampaignAsync(Guid orgId, Guid actorUserId, CreateCampaignRequest request, CancellationToken ct);
        Task<CampaignResponse> UpdateCampaignAsync(Guid orgId, Guid actorUserId, Guid id, UpdateCampaignRequest request, CancellationToken ct);
        Task<bool> DeleteCampaignAsync(Guid orgId, Guid actorUserId, Guid id, CancellationToken ct);
        Task<CampaignResponse> UploadCampaignFilesAsync(Guid orgId, Guid id, UploadCampaignFilesRequest request, CancellationToken ct);
        Task<CampaignResponse> UpdateCampaignFilesAsync(Guid orgId, Guid id, UploadCampaignFilesRequest request, CancellationToken ct);
        Task<CampaignResponse> UpdateCampaignQuestionsAsync(Guid orgId, Guid actorUserId, Guid id, List<QuestionItem> questions, CancellationToken ct);
        Task<Stream> DownloadCampaignFilesAsync(Guid orgId, Guid id, string fileType, CancellationToken ct);
        Task<CampaignResponse> PublishCampaignAsync(Guid orgId, Guid actorUserId, Guid id, CancellationToken ct);
        Task<CampaignResponse> TransitionStatusAsync(Guid orgId, Guid actorUserId, Guid id, CampaignStatus target, CancellationToken ct);

        // D1: Distribution đường 1 — mời thẳng qua danh sách email
        Task<CreateInvitationsResponse> CreateInvitationsAsync(Guid orgId, Guid actorUserId, Guid id, List<string> emails, CancellationToken ct);

        // C15: Distribution đường 2 — mời hàng loạt từ shortlist sàng CV (candidateIds → tách email từ CV).
        Task<InviteShortlistResponse> InviteShortlistedCandidatesAsync(Guid orgId, Guid actorUserId, Guid id, List<Guid> candidateIds, CancellationToken ct);

        // D4: phát lại lời mời — vô hiệu token cũ (revoke → 410) + tạo invitation mới cùng email/candidate + resend.
        Task<InvitationItem> ReissueInvitationAsync(Guid orgId, Guid actorUserId, Guid id, Guid invitationId, CancellationToken ct);

        // E5: bảng kết quả + xếp hạng + pass/fail (đọc read-model campaign_rankings — E4)
        Task<CampaignResultsResponse> GetCampaignResultsAsync(Guid orgId, Guid id, CancellationToken ct);

        // E11b: HR chốt/sửa điểm-kết-quả cuối 1 ứng viên (org-scoped, audit; clear = về AI).
        Task OverrideResultAsync(Guid orgId, Guid actorUserId, Guid campaignId, Guid sessionId, OverrideResultRequest req, CancellationToken ct);

        // E6: xuất bảng kết quả (E5) ra file — format=csv (pdf 🔜). Ngoài org → KeyNotFoundException (404).
        Task<CampaignResultExport> ExportCampaignResultsAsync(Guid orgId, Guid id, string? format, CancellationToken ct);

        // C13: sàng CV hàng loạt — parse + archive PDF (S3 KEY) + hard-filter (rule cứng, 0 credit).
        Task<ScreenCandidatesResponse> ScreenCandidatesAsync(Guid orgId, Guid actorUserId, Guid id, IFormFileCollection files, CancellationToken ct);

        // C13: serve CV gốc cho HR — cv_file_url null → FileNotFoundException (404); ngoài org → 404.
        Task<Stream> DownloadCandidateCvAsync(Guid orgId, Guid id, Guid candidateId, CancellationToken ct);
    }
}
