namespace Isas.CampaignService.DTOs
{
    // Phân tích tuyển dụng theo TỔ CHỨC — `GET /campaign/analytics` (employer). Hợp đồng JSON camelCase
    // (JsonSerializerDefaults.Web của controller). FE khai model theo ĐÚNG tên property dưới đây — đổi tên
    // là field rụng im lặng ở phía kia (lớp bug focusCriteria / metricsVersion / adaptiveMaxQuestions /
    // ScoreFallback đã cắn repo 4 lần), nên có test khoá tên khoá bằng JSON serialize thật.
    //
    // STOCK vs FLOW: mọi khối `Campaigns/Screening/Invitations/Interviews/PerCampaign` là trạng thái HIỆN
    // TẠI của cả org (KHÔNG lọc theo kỳ); chỉ `Buckets` là dòng chảy trong [From, To).
    //
    // Mọi mảng là List<T> đã materialize — KHÔNG IEnumerable lazy (bài học AdminAnalyticsFr18Tests: nhánh
    // lazy chỉ chạy lúc serialize, SAU khi Ok() đã return, nên lỗi lọt qua Assert.IsType<OkObjectResult>).
    public class CampaignAnalyticsResponse
    {
        public DateTime From { get; set; }
        public DateTime To { get; set; }
        public string Granularity { get; set; } = "day";   // "day" | "month"
        public CampaignAnalyticsCampaigns Campaigns { get; set; } = new();
        public CampaignAnalyticsScreening Screening { get; set; } = new();
        public CampaignAnalyticsInvitations Invitations { get; set; } = new();
        public CampaignAnalyticsInterviews Interviews { get; set; } = new();
        public List<CampaignAnalyticsBucket> Buckets { get; set; } = new();
        public List<CampaignAnalyticsPerCampaign> PerCampaign { get; set; } = new();
    }

    public class CampaignAnalyticsCampaigns
    {
        public int Total { get; set; }
        public List<StatusCount> ByStatus { get; set; } = new();   // status = enum string CampaignStatus
    }

    public class CampaignAnalyticsScreening
    {
        public int Submissions { get; set; }                         // COUNT cv_submission (mọi status)
        public int Analyzed { get; set; }                            // status ∈ {Analyzed, Invited} — đã có điểm sàng
        public List<StatusCount> ByStatus { get; set; } = new();     // enum string CvSubmissionStatus
        public decimal? MedianFitScore { get; set; }                 // median overall_match_score (bỏ null); null nếu 0 dòng
        public List<BandCount> FitDistribution { get; set; } = new();   // LUÔN đủ 5 band, đúng thứ tự
        public List<RiskCount> RiskBySeverity { get; set; } = new();    // LUÔN đủ 3: Low · Medium · High
        public List<SkillCount> TopSkills { get; set; } = new();        // top 10, count giảm dần, hoà theo tên A→Z
    }

    public class CampaignAnalyticsInvitations
    {
        public int Total { get; set; }     // = Queued + Sent + Joined + Expired + Revoked
        public int Queued { get; set; }
        public int Sent { get; set; }
        public int Joined { get; set; }
        public int Expired { get; set; }
        public int Revoked { get; set; }
    }

    public class CampaignAnalyticsInterviews
    {
        public int Joined { get; set; }        // COUNT campaign_membership của org
        public int Started { get; set; }       // session_id != null
        public int InProgress { get; set; }    // interview_status == InProgress
        public int Completed { get; set; }     // interview_status == Completed
        public int Scored { get; set; }        // COUNT campaign_rankings của org
        public int PendingScore { get; set; }  // session_id != null ∧ Completed ∧ session_id ∉ campaign_rankings
        public int Passed { get; set; }
        public int Failed { get; set; }
        public int Undetermined { get; set; }
        public decimal? MedianScore { get; set; }                      // median (override_score ?? total_score); null nếu 0 dòng
        public List<BandCount> ScoreDistribution { get; set; } = new();   // đủ 5 band, trên điểm effective
        public List<SignalCount> FlagsBySignal { get; set; } = new();     // count giảm dần, hoà theo tên
    }

    public class CampaignAnalyticsBucket
    {
        public DateTime PeriodStart { get; set; }
        public int CampaignsCreated { get; set; }    // campaigns.created_at
        public int InvitationsSent { get; set; }     // campaign_invitations.email_sent_at (KHÔNG phải created_at)
        public int Joins { get; set; }               // campaign_membership.joined_at
        public int InterviewsStarted { get; set; }   // campaign_membership.interview_started_at
        public int Scored { get; set; }              // campaign_rankings.updated_at
    }

    public class CampaignAnalyticsPerCampaign
    {
        public Guid CampaignId { get; set; }
        public string Title { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
        public int Invited { get; set; }
        public int Joined { get; set; }
        public int Started { get; set; }
        public int Scored { get; set; }
        public int Passed { get; set; }
        public decimal? MedianScore { get; set; }
    }

    public class StatusCount
    {
        public string Status { get; set; } = string.Empty;
        public int Count { get; set; }
    }

    public class BandCount
    {
        public string Band { get; set; } = string.Empty;   // "0-19" · "20-39" · "40-59" · "60-79" · "80-100"
        public int Count { get; set; }
    }

    public class RiskCount
    {
        public string Risk { get; set; } = string.Empty;   // "Low" · "Medium" · "High"
        public int Count { get; set; }
    }

    public class SkillCount
    {
        public string Skill { get; set; } = string.Empty;
        public int Count { get; set; }
    }

    public class SignalCount
    {
        public string SignalType { get; set; } = string.Empty;
        public int Count { get; set; }
    }
}
