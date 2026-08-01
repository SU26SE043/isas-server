using Microsoft.Extensions.Options;
using Microsoft.EntityFrameworkCore;
using Isas.PaymentService.DTOs;
using PaymentService.Models;

namespace Isas.PaymentService.Services;

/// <summary>Catalog validation kept in Payment so no caller can manufacture an unsafe tier.</summary>
public sealed class PlanService
{
    private readonly TieringSettings _settings;
    private readonly PaymentDbContext? _db;

    public PlanService(IOptions<TieringSettings> settings) => _settings = settings.Value;
    public PlanService(PaymentDbContext db, IOptions<TieringSettings> settings) : this(settings) => _db = db;

    public Task<List<Plan>> GetAsync(PlanAudience? audience, CancellationToken ct) => Db.Plans.AsNoTracking()
        .Where(p => audience == null || p.Audience == audience).OrderBy(p => p.Audience).ThenBy(p => p.Rank).ToListAsync(ct);

    public Task<Plan?> GetAsync(Guid id, CancellationToken ct) => Db.Plans.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct);

    public async Task<Plan> CreateAsync(PlanRequest request, CancellationToken ct)
    {
        var plan = new Plan { Id = Guid.NewGuid(), CreatedAt = DateTime.UtcNow };
        request.ApplyTo(plan); Validate(plan);
        if (await Db.Plans.AnyAsync(p => p.Audience == plan.Audience && p.Code == plan.Code, ct))
            throw new ArgumentException("Plan code already exists for this audience.");
        Db.Plans.Add(plan); await Db.SaveChangesAsync(ct); return plan;
    }

    public async Task<Plan?> UpdateAsync(Guid id, PlanRequest request, CancellationToken ct)
    {
        var plan = await Db.Plans.FirstOrDefaultAsync(p => p.Id == id, ct); if (plan is null) return null;
        request.ApplyTo(plan); Validate(plan); await Db.SaveChangesAsync(ct); return plan;
    }

    public async Task<bool> DeactivateAsync(Guid id, CancellationToken ct)
    {
        var plan = await Db.Plans.FirstOrDefaultAsync(p => p.Id == id, ct); if (plan is null) return false;
        plan.IsActive = false; await Db.SaveChangesAsync(ct); return true;
    }

    private PaymentDbContext Db => _db ?? throw new InvalidOperationException("Plan catalog requires a database.");

    public void Validate(Plan plan)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(plan.Code);
        ArgumentException.ThrowIfNullOrWhiteSpace(plan.Name);
        if (plan.Rank < 0) throw new ArgumentException("Plan rank must be >= 0.");
        if (plan.SelfConsistencyN < 1) throw new ArgumentException("SelfConsistencyN must be >= 1.");
        if (plan.MaxQuestionsCap is < 0 or > 20)
            throw new ArgumentException("MaxQuestionsCap must be between 0 and 20.");
        if (!plan.AdaptiveEnabled &&
            (plan.AdaptiveMaxQuestions is not null || plan.AdaptiveMaxFollowups is not null))
            throw new ArgumentException("Adaptive caps require AdaptiveEnabled.");
        if (plan.InterviewFunding == InterviewFunding.Metered && plan.MonthlyQuota is not > 0)
            throw new ArgumentException("Metered plans require a positive MonthlyQuota.");
        if (plan.InterviewFunding == InterviewFunding.Unlimited && !_settings.AllowUnlimitedPlans)
            throw new ArgumentException("Unlimited plans are disabled by Tiering:AllowUnlimitedPlans.");
        if (plan.Audience == PlanAudience.B2C &&
            (plan.MaxActiveCampaigns is not null || plan.MaxCandidatesCap is not null ||
             plan.PostpaidEligible || plan.SeatCount is not null))
            throw new ArgumentException("B2C plans cannot carry B2B entitlements.");
    }
}
