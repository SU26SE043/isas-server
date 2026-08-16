namespace PaymentService.Models;

/// <summary>
/// Catalog entitlement thuộc Payment. Plan tách giá/SKU (ProductPackage): một tier có thể có nhiều
/// package tháng/năm, nhưng snapshot entitlement chỉ lấy từ plan tại thời điểm kích hoạt.
/// </summary>
public class Plan : IHasUpdatedAt
{
    public Guid Id { get; set; }
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

    // jsonb text deliberately defaults to [] (never an invalid empty string).
    public string EntitlementsJson { get; set; } = "[]";
    public int EntitlementsVersion { get; set; } = 1;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

}

public enum PlanAudience { B2C, B2B }

public enum InterviewFunding { Credit, Metered, Unlimited }

internal static class PlanSeed
{
    // Stable IDs make package seeding/admin references deterministic. No Unlimited tier is seeded.
    //
    // ⚠ ADAPTIVE BẬT Ở **MỌI** TIER — quyết định sản phẩm, không phải sơ suất seed. Một buổi phỏng vấn
    // tiêu đúng 1 credit (B2C ví cá nhân · B2B ví org) BẤT KỂ gói, nên gói không được lấy mất chính cái
    // engine mà người dùng vừa trả tiền để chạy. Gói vẫn phân biệt bằng những thứ có chi phí biên khác
    // nhau THẬT: nguồn tiền (credit vs quota tháng), grounding, self-consistency (×N lần gọi Gemini),
    // phân tích CV/repo, roadmap, trần campaign/candidate B2B, postpaid, seats.
    // Khoá bằng test `PlanSeedAdaptiveTests`; đường B2C còn có SÀN thứ hai ở `PracticeService`.
    internal static readonly Plan[] All =
    [
        New("10000000-0000-0000-0000-000000000001", PlanAudience.B2C, "free", "Free", 0, InterviewFunding.Credit, adaptive: true, maxQ: 20, followups: 3),
        New("10000000-0000-0000-0000-000000000002", PlanAudience.B2C, "plus", "Plus", 1, InterviewFunding.Metered, 30, adaptive: true, maxQ: 20, followups: 3, grounding: true, roadmap: true),
        New("10000000-0000-0000-0000-000000000003", PlanAudience.B2C, "pro", "Pro", 2, InterviewFunding.Metered, 100, adaptive: true, maxQ: 20, followups: 5, grounding: true, scn: 3, repo: true, roadmap: true),
        New("20000000-0000-0000-0000-000000000001", PlanAudience.B2B, "starter", "Starter", 0, InterviewFunding.Credit, adaptive: true, campaigns: 1, candidates: 25, seats: 1),
        New("20000000-0000-0000-0000-000000000002", PlanAudience.B2B, "business", "Business", 1, InterviewFunding.Credit, adaptive: true, grounding: true, campaigns: 10, candidates: 200, postpaid: true, seats: 10),
        New("20000000-0000-0000-0000-000000000003", PlanAudience.B2B, "enterprise", "Enterprise", 2, InterviewFunding.Credit, adaptive: true, grounding: true, postpaid: true)
    ];

    private static Plan New(string id, PlanAudience audience, string code, string name, int rank,
        InterviewFunding funding, int? quota = null, bool adaptive = false, int? maxQ = null,
        int? followups = null, bool grounding = false, int scn = 1, bool repo = false,
        bool roadmap = false, int? campaigns = null, int? candidates = null,
        bool postpaid = false, int? seats = null) => new()
    {
        Id = Guid.Parse(id), Audience = audience, Code = code, Name = name, Rank = rank,
        InterviewFunding = funding, MonthlyQuota = quota, AdaptiveEnabled = adaptive,
        AdaptiveMaxQuestions = maxQ, AdaptiveMaxFollowups = followups, GroundingEnabled = grounding,
        SelfConsistencyN = scn, CvAnalysisIncluded = rank > 0 && audience == PlanAudience.B2C,
        RepoAnalysisIncluded = repo, RoadmapEnabled = roadmap, MaxQuestionsCap = maxQ,
        MaxActiveCampaigns = campaigns, MaxCandidatesCap = candidates, PostpaidEligible = postpaid,
        SeatCount = seats, EntitlementsJson = "[]", EntitlementsVersion = 1, IsActive = true,
        CreatedAt = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc),
        UpdatedAt = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc)
    };
}
