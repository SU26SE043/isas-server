using Isas.CampaignService.DTOs;
using Isas.CampaignService.Models;

namespace Isas.CampaignService.Services
{
    public interface ICampaignService
    {
        Task<CampaignResponse> GetCampaignAsync(Guid employerId, Guid id, CancellationToken ct);
        Task<List<CampaignResponse>> GetCampaignsAsync(Guid employerId, CancellationToken ct);
        Task<CampaignResponse> CreateCampaignAsync(Guid employerId, CreateCampaignRequest request, CancellationToken ct);
        Task<CampaignResponse> UpdateCampaignAsync(Guid employerId, Guid id, UpdateCampaignRequest request, CancellationToken ct);
        Task<bool> DeleteCampaignAsync(Guid employerId, Guid id, CancellationToken ct);
        Task<CampaignResponse> UploadCampaignFilesAsync(Guid employerId, Guid id, UploadCampaignFilesRequest request, CancellationToken ct);
        Task<CampaignResponse> UpdateCampaignFilesAsync(Guid employerId, Guid id, UploadCampaignFilesRequest request, CancellationToken ct);
        Task<CampaignResponse> UpdateCampaignQuestionsAsync(Guid employerId, Guid id, List<QuestionItem> questions, CancellationToken ct);
        Task<Stream> DownloadCampaignFilesAsync(Guid employerId, Guid id, string fileType, CancellationToken ct);
        Task<CampaignResponse> PublishCampaignAsync(Guid employerId, Guid id, CancellationToken ct);
        Task<CampaignResponse> TransitionStatusAsync(Guid employerId, Guid id, CampaignStatus target, CancellationToken ct);

        // D1: Distribution đường 1 — mời thẳng qua danh sách email
        Task<CreateInvitationsResponse> CreateInvitationsAsync(Guid employerId, Guid id, List<string> emails, CancellationToken ct);

        // E5: bảng kết quả + xếp hạng + pass/fail (đọc read-model campaign_rankings — E4)
        Task<CampaignResultsResponse> GetCampaignResultsAsync(Guid employerId, Guid id, CancellationToken ct);

        // E6: xuất bảng kết quả (E5) ra file — format=csv (pdf 🔜). Ngoài org → KeyNotFoundException (404).
        Task<CampaignResultExport> ExportCampaignResultsAsync(Guid employerId, Guid id, string? format, CancellationToken ct);

        // C13: sàng CV hàng loạt — parse + archive PDF (S3 KEY) + hard-filter (rule cứng, 0 credit).
        Task<ScreenCandidatesResponse> ScreenCandidatesAsync(Guid employerId, Guid id, IFormFileCollection files, CancellationToken ct);

        // C13: serve CV gốc cho HR — cv_file_url null → FileNotFoundException (404); ngoài org → 404.
        Task<Stream> DownloadCandidateCvAsync(Guid employerId, Guid id, Guid candidateId, CancellationToken ct);
    }
}
