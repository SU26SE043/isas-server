namespace Isas.CampaignService.DTOs
{
    /// <summary>
    /// Tiêu chí chấm như ỨNG VIÊN được thấy. CỐ Ý là type riêng chứ không dùng lại
    /// <see cref="CampaignCriterionResponse"/>: bản của Employer mang <c>Levels</c> (mốc điểm), mà mốc
    /// điểm là thước đo nội bộ — lộ ra thì ứng viên viết bài bám đúng câu chữ của mốc và thang đo mất
    /// hết giá trị phân biệt.
    ///
    /// <para>Chống rò bằng CẤU TRÚC, không bằng lời dặn: ở đây KHÔNG khai trường <c>Levels</c>, nên
    /// gán nhầm là lỗi BIÊN DỊCH. Trước đó hai đường ứng viên an toàn chỉ vì query quên
    /// <c>ThenInclude(Levels)</c> — tức an toàn do tình cờ, và một dòng "thêm cho đồng bộ" là rò ngay
    /// mà không test nào kêu. Cùng mẫu với <c>ApiKeyListItem</c> (F17) không khai trường <c>key</c>.</para>
    ///
    /// <para>Shape JSON trùng khít bản Employer TRƯỚC khi có mốc điểm ⇒ FE ứng viên không phải sửa gì.</para>
    /// </summary>
    public class CandidateCriterionResponse
    {
        public Guid Id { get; set; }
        public int OrderNo { get; set; }
        public string Name { get; set; } = null!;
        public string? Description { get; set; }
        public decimal Weight { get; set; }
        public int MaxScore { get; set; }
        public string Source { get; set; } = null!;
    }

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
        // CMP1-B1 — giờ MỞ phỏng vấn của campaign (campaign.StartsAt). KHÁC nghĩa với Deadline:
        // Deadline là hạn của LỜI MỜI (campaign.ExpiresAt), StartsAt là lúc ứng viên được phép bấm Start.
        public DateTime? StartsAt { get; set; }
        public DateTime? Deadline { get; set; }      // campaign.ExpiresAt — hạn lời mời (KHÔNG đổi nghĩa)
        public List<CandidateCriterionResponse> Criteria { get; set; } = new();
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
        public List<CandidateCriterionResponse> Criteria { get; set; } = new();
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
        public DateTime? DeadlineAt { get; set; }
        public List<StartQuestionItem> Questions { get; set; } = new();
        // SEC-1: campaign bật giám sát chống gian lận → FE kích hoạt proctoring (tab-switch/paste/focus + webcam).
        // Độc lập với face-verify: có thể bật anti-cheat mà không bật face-verify.
        public bool AntiCheatEnabled { get; set; }
        // SEC-2: campaign bật face-verify NHƯNG ứng viên chưa có ảnh tham chiếu → FE nhắc enroll trước khi làm.
        // D13/SEC-5: chỉ là gợi ý (thiếu ảnh ≠ gian lận) — KHÔNG hard-block việc bắt đầu.
        public bool FaceEnrollRequired { get; set; }
        // INT-17: campaign bật phỏng vấn THÍCH ỨNG → FE biết sẽ có câu hỏi sinh động ở đuôi (sau khi
        // trả lời hết seed) và append `nextQuestion` từ response upload thay vì nhảy màn tổng kết.
        public bool AdaptiveEnabled { get; set; }
    }

    public class StartQuestionItem
    {
        public Guid Id { get; set; }
        public int OrderNo { get; set; }
        public string Content { get; set; } = null!;
        public int TimeLimitSec { get; set; }
    }
}
