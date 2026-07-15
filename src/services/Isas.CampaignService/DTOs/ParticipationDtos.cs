namespace Isas.CampaignService.DTOs
{
    // ── D2: Distribution — ứng viên tham gia campaign qua magic-link (Discord/Classroom model) ──
    // Link CHỈ để tham gia (join); session phỏng vấn tạo khi bấm "Start Interview", KHÔNG khi mở link.

    /// <summary>GET /invitations/{token} — metadata công khai để ứng viên xem trước khi tham gia (KHÔNG side-effect).</summary>
    public class InvitationMetadataResponse
    {
        public Guid CampaignId { get; set; }
        public string Title { get; set; } = null!;
        public string? OrgName { get; set; }        // tên công ty — Campaign chỉ có org_id (không call Auth) → null (chờ resolve)
        public string? JobTitle { get; set; }       // vị trí = campaign.Domain
        public string? Description { get; set; }     // JD text
        public DateTime? Deadline { get; set; }      // campaign.ExpiresAt
        public List<CampaignCriterionResponse> Criteria { get; set; } = new();
    }

    /// <summary>POST /invitations/{token}/join — kết quả tham gia (provision Candidate + membership Joined).</summary>
    public class JoinCampaignResponse
    {
        public string AccessToken { get; set; } = null!;   // JWT Candidate (từ Auth provision)
        public Guid CampaignId { get; set; }
        public Guid CandidateId { get; set; }
        public string MembershipStatus { get; set; } = null!;   // "Joined"
    }

    /// <summary>GET /my-campaigns — 1 dòng / 1 campaign ứng viên đã join.</summary>
    public class MyCampaignItem
    {
        public Guid CampaignId { get; set; }
        public string Title { get; set; } = null!;
        public string? Company { get; set; }         // org_id → tên: null (không call Auth)
        public string? JobTitle { get; set; }
        public DateTime? Deadline { get; set; }
        public string MembershipStatus { get; set; } = null!;      // "Joined"
        public string InterviewStatus { get; set; } = null!;       // NotStarted/InProgress/Completed
    }

    /// <summary>GET /my-campaigns/{id} — chi tiết campaign cho ứng viên đã join (JD/criteria/deadline + đã start chưa).</summary>
    public class CandidateCampaignDetailResponse
    {
        public Guid CampaignId { get; set; }
        public string Title { get; set; } = null!;
        public string? JobTitle { get; set; }
        public string? Description { get; set; }
        public DateTime? Deadline { get; set; }
        public List<CampaignCriterionResponse> Criteria { get; set; } = new();
        public string MembershipStatus { get; set; } = null!;
        public string InterviewStatus { get; set; } = null!;
        public Guid? SessionId { get; set; }
        public bool Started { get; set; }
    }

    /// <summary>POST /campaign/{id}/start — bắt đầu phỏng vấn (create-or-get session Interview).</summary>
    public class StartInterviewResponse
    {
        public Guid SessionId { get; set; }
        public Guid CampaignId { get; set; }
        public List<StartQuestionItem> Questions { get; set; } = new();
        // SEC-2: campaign bật face-verify NHƯNG ứng viên chưa có ảnh tham chiếu → FE nhắc enroll trước khi làm.
        // D13/SEC-5: chỉ là gợi ý (thiếu ảnh ≠ gian lận) — KHÔNG hard-block việc bắt đầu.
        public bool FaceEnrollRequired { get; set; }
    }

    public class StartQuestionItem
    {
        public Guid Id { get; set; }
        public int OrderNo { get; set; }
        public string Content { get; set; } = null!;
        public int TimeLimitSec { get; set; }
    }
}
