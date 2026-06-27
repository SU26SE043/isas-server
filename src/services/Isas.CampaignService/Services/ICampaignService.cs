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
    }
}
