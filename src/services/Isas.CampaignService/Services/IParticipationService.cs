using Isas.CampaignService.DTOs;
using Isas.Shared.Pagination;

namespace Isas.CampaignService.Services
{
    /// <summary>
    /// D2 — Distribution: ứng viên tham gia campaign qua magic-link rồi bắt đầu phỏng vấn
    /// (Discord/Classroom model). Link CHỈ để join; session tạo khi "Start Interview".
    /// </summary>
    public interface IParticipationService
    {
        // GET /invitations/{token} — metadata công khai (KHÔNG side-effect: không provision, không membership, không session).
        Task<InvitationMetadataResponse> GetInvitationMetadataAsync(string token, CancellationToken ct = default);

        // POST /invitations/{token}/join — provision Candidate (Auth) + tạo/cập nhật membership Joined.
        Task<JoinCampaignResponse> JoinCampaignAsync(string token, string? callerEmail, CancellationToken ct = default);

        // GET /my-campaigns — campaign đã join của candidate.
        // Keyset-paged (DB8) theo (CreatedAt DESC, Id DESC) của membership; soft-delete campaign lọc ở SQL.
        Task<KeysetPage<MyCampaignItem>> GetMyCampaignsAsync(
            Guid candidateId, string? cursor, int? limit, CancellationToken ct = default);

        // GET /my-campaigns/{id} — chi tiết campaign cho ứng viên đã join (không phải thành viên → 404).
        Task<CandidateCampaignDetailResponse> GetCandidateCampaignAsync(Guid candidateId, Guid campaignId, CancellationToken ct = default);

        // POST /campaign/{id}/start — create-or-get session (Interview) + gắn session_id + InProgress. Idempotent.
        Task<StartInterviewResponse> StartInterviewAsync(Guid candidateId, Guid campaignId, CancellationToken ct = default);
    }
}
