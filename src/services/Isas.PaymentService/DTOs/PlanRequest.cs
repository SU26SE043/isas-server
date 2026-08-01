using PaymentService.Models;

namespace Isas.PaymentService.DTOs;

public class PlanRequest
{
    public PlanAudience Audience { get; set; }
    public string Code { get; set; } = null!;
    public string Name { get; set; } = null!;
    public int Rank { get; set; }
    public InterviewFunding InterviewFunding { get; set; }
    public int? MonthlyQuota { get; set; }
    public bool AdaptiveEnabled { get; set; }
    public int? AdaptiveMaxQuestions { get; set; }
    public int? AdaptiveMaxFollowups { get; set; }
    public bool GroundingEnabled { get; set; }
    public int SelfConsistencyN { get; set; } = 1;
    public bool CvAnalysisIncluded { get; set; }
    public bool RepoAnalysisIncluded { get; set; }
    public bool RoadmapEnabled { get; set; }
    public int? MaxQuestionsCap { get; set; }
    public int? MaxActiveCampaigns { get; set; }
    public int? MaxCandidatesCap { get; set; }
    public bool PostpaidEligible { get; set; }
    public int? SeatCount { get; set; }
    public string EntitlementsJson { get; set; } = "[]";
    public int EntitlementsVersion { get; set; } = 1;
    public bool IsActive { get; set; } = true;

    internal void ApplyTo(Plan plan)
    {
        plan.Audience = Audience; plan.Code = Code; plan.Name = Name; plan.Rank = Rank;
        plan.InterviewFunding = InterviewFunding; plan.MonthlyQuota = MonthlyQuota;
        plan.AdaptiveEnabled = AdaptiveEnabled; plan.AdaptiveMaxQuestions = AdaptiveMaxQuestions;
        plan.AdaptiveMaxFollowups = AdaptiveMaxFollowups; plan.GroundingEnabled = GroundingEnabled;
        plan.SelfConsistencyN = SelfConsistencyN; plan.CvAnalysisIncluded = CvAnalysisIncluded;
        plan.RepoAnalysisIncluded = RepoAnalysisIncluded; plan.RoadmapEnabled = RoadmapEnabled;
        plan.MaxQuestionsCap = MaxQuestionsCap; plan.MaxActiveCampaigns = MaxActiveCampaigns;
        plan.MaxCandidatesCap = MaxCandidatesCap; plan.PostpaidEligible = PostpaidEligible;
        plan.SeatCount = SeatCount; plan.EntitlementsJson = EntitlementsJson;
        plan.EntitlementsVersion = EntitlementsVersion; plan.IsActive = IsActive;
    }
}
