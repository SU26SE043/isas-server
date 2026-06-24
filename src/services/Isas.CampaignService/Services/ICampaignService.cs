using Isas.CampaignService.DTOs;

namespace Isas.CampaignService.Services
{
    public interface ICampaignService
    {
        Task<CampaignResponse> GetCampaignAsync(Guid id, CancellationToken ct);
        Task<List<CampaignResponse>> GetCampaignsAsync(CancellationToken ct);
        Task<CampaignResponse> CreateCampaignAsync(Guid employerId, CreateCampaignRequest request, CancellationToken ct);
        Task<CampaignResponse> UpdateCampaignAsync(Guid id, UpdateCampaignRequest request, CancellationToken ct);
        Task<bool> DeleteCampaignAsync(Guid id, CancellationToken ct);
        Task<CampaignResponse> UploadCampaignFilesAsync(Guid employerId, Guid id, UploadCampaignFilesRequest request, CancellationToken ct);
        Task<CampaignResponse> UpdateCampaignFilesAsync(Guid id, UploadCampaignFilesRequest request, CancellationToken ct);
        Task<CampaignResponse> UpdateCampaignQuestionsAsync(Guid id, List<QuestionItem> questions, CancellationToken ct);
        Task<Stream> DownloadCampaignFilesAsync(Guid id, string fileType, CancellationToken ct);
    }
}
