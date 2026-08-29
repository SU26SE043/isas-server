using System.Security.Claims;
using System.Text.Json;
using Isas.CampaignService.Controllers;
using Isas.CampaignService.DTOs;
using Isas.CampaignService.Models;
using Isas.CampaignService.Services;
using Isas.Shared.Scoring;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Isas.CampaignService.Tests;

/// <summary>
/// SCP1-B8 — XEM TRƯỚC + ÁP (HĐ-4/HĐ-6). Preview chạy biểu thức đề xuất trên bó biến của MỌI ứng
/// viên đã chấm (LOCAL, không xuyên service), KHÔNG ghi gì. Apply đòi fingerprint khớp (lệch → 409),
/// ghi đè điểm chính thức + audit điểm cũ + dời con trỏ version. Apply chỉ OrgAdmin.
/// </summary>
public class ScoringPolicyB8Tests
{
    private sealed record Env(
        CampaignController Controller, CampaignTestDb Db, Guid OrgId, Guid CampaignId, Guid ActorUserId);

    private static Env Setup(CampaignStatus status = CampaignStatus.Active, string orgRole = "OrgAdmin")
    {
        var tdb = new CampaignTestDb();
        var orgId = Guid.NewGuid();
        var actor = Guid.NewGuid();
        var campaign = CampaignTestDb.NewCampaign(orgId, status);
        campaign.Domain = "BE";
        campaign.JDText = "JD";
        tdb.Db.Campaigns.Add(campaign);
        tdb.Db.SaveChanges();

        var controller = new CampaignController(
            Mock.Of<ICampaignService>(),
            Mock.Of<ICvScreeningService>(),
            Mock.Of<ILogger<CampaignController>>(),
            policies: new ScoringPolicyService(tdb.NewContext()));

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, actor.ToString()),
            new("org_id", orgId.ToString()),
            new("org_role", orgRole),
        };
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) }
        };
        return new Env(controller, tdb, orgId, campaign.Id, actor);
    }

    // Bó biến RAW của 1 buổi: 2 tiêu chí đều weight 0.5, maxScore 5 ⇒ weighted_avg_pct = mean(pct).
    private static ScoringInputsSnapshot Bag(decimal pctA, decimal pctB, int answered = 8, int total = 10)
        => new(
            new[]
            {
                new CriterionInputSnapshot("Giao tiếp", pctA, 0.5m, 5),
                new CriterionInputSnapshot("Kỹ thuật", pctB, 0.5m, 5),
            },
            answered, total);

    private static void SeedRanking(CampaignTestDb tdb, Guid campaignId, Guid candidateId, decimal total, ScoringInputsSnapshot bag)
    {
        tdb.Db.CampaignRankings.Add(new CampaignRanking
        {
            Id = Guid.NewGuid(), CampaignId = campaignId, CandidateId = candidateId,
            SessionId = Guid.NewGuid(), TotalScore = total, ScoringInputs = bag,
            UpdatedAt = DateTime.UtcNow,
        });
        tdb.Db.SaveChanges();
    }

    private static ScoringPolicy SeedPolicy(
        CampaignTestDb tdb, Guid campaignId, int version, string expr,
        ScoringExpressionKind kind = ScoringExpressionKind.Interview, int? pass = 60)
    {
        var p = new ScoringPolicy
        {
            Id = Guid.NewGuid(), CampaignId = campaignId, Kind = kind, Version = version,
            EngineVersion = ScoringEngine.Version, Name = $"policy v{version}", Expression = expr,
            PassScorePct = pass, CreatedAt = DateTime.UtcNow, CreatedBy = Guid.NewGuid(),
        };
        tdb.Db.ScoringPolicies.Add(p);
        tdb.Db.SaveChanges();
        return p;
    }

    private static async Task<ScoringPolicyResponse> CreatePolicy(
        CampaignController c, Guid campaignId, string kind, string expr, int? pass = 60)
    {
        var action = await c.CreateScoringPolicy(campaignId,
            new CreateScoringPolicyRequest { Kind = kind, Name = "Bản B8", Expression = expr, PassScorePct = pass },
            default);
        return Assert.IsType<ScoringPolicyResponse>(Assert.IsType<OkObjectResult>(action.Result).Value);
    }

    private static async Task<ScoringPolicyPreviewResponse> Preview(
        CampaignController c, Guid campaignId, string kind, string expr, int? pass = 60)
    {
        var action = await c.PreviewScoringPolicy(campaignId,
            new ScoringPolicyPreviewRequest { Kind = kind, Expression = expr, PassScorePct = pass },
            cursor: null, limit: null, default);
        return Assert.IsType<ScoringPolicyPreviewResponse>(Assert.IsType<OkObjectResult>(action.Result).Value);
    }

    // ── (test brief) preview KHÔNG đổi một điểm nào trong DB ───────────────────────────────────
    [Fact]
    public async Task Preview_khong_ghi_gi_vao_DB()
    {
        var e = Setup();
        using var _d = e.Db;
        var c1 = Guid.NewGuid();
        var c2 = Guid.NewGuid();
        SeedRanking(e.Db, e.CampaignId, c1, total: 60m, bag: Bag(80m, 40m));   // weighted = 60
        SeedRanking(e.Db, e.CampaignId, c2, total: 30m, bag: Bag(30m, 30m));   // weighted = 30

        // Biểu thức KHÁC hẳn: min_pct. c1 → 40, c2 → 30.
        var res = await Preview(e.Controller, e.CampaignId, "Interview", "min_pct");

        Assert.Equal(2, res.Total);
        var byId = res.Rows.ToDictionary(r => r.CandidateId);
        Assert.Equal(40m, byId[c1].NewScore);
        Assert.Equal(30m, byId[c2].NewScore);
        Assert.Equal(60m, byId[c1].OldScore);
        Assert.NotNull(res.Fingerprint);

        // DB KHÔNG đổi: total_score giữ nguyên, chưa cột policy nào set, chưa con trỏ.
        using var db = e.Db.NewContext();
        var rows = await db.CampaignRankings.Where(r => r.CampaignId == e.CampaignId).ToListAsync();
        Assert.All(rows, r =>
        {
            Assert.True(r.TotalScore == 60m || r.TotalScore == 30m);
            Assert.Null(r.PolicyVersion);
            Assert.Null(r.PolicyName);
            Assert.False(r.ScoreFallback);
        });
        Assert.Null((await db.Campaigns.SingleAsync(x => x.Id == e.CampaignId)).InterviewPolicyVersion);
        Assert.Equal(0, await db.AuditLogs.CountAsync());
    }

    // ── (test brief) đổi biểu thức sau preview rồi apply → 409 POLICY_CHANGED_AFTER_PREVIEW ────
    [Fact]
    public async Task Apply_fingerprint_lech_thi_409()
    {
        var e = Setup();
        using var _d = e.Db;
        SeedRanking(e.Db, e.CampaignId, Guid.NewGuid(), 60m, Bag(80m, 40m));

        // HR xem trước biểu thức X, cầm fingerprint của X.
        var previewX = await Preview(e.Controller, e.CampaignId, "Interview", "weighted_avg_pct");

        // ... rồi TẠO policy với biểu thức KHÁC (X') — con trỏ không dời (đã có người chấm).
        var policy = await CreatePolicy(e.Controller, e.CampaignId, "Interview", "weighted_avg_pct * 0.9");

        // Apply policy đó với fingerprint CŨ của X ⇒ vân tay tính lại từ X' ≠ fingerprint X ⇒ 409.
        var action = await e.Controller.ApplyScoringPolicy(
            e.CampaignId, policy.Id, new ApplyScoringPolicyRequest { Fingerprint = previewX.Fingerprint }, default);

        var conflict = Assert.IsType<ConflictObjectResult>(action.Result);
        Assert.Contains("POLICY_CHANGED_AFTER_PREVIEW", JsonSerializer.Serialize(conflict.Value));

        // Không ghi đè gì khi 409.
        using var db = e.Db.NewContext();
        Assert.Equal(60m, (await db.CampaignRankings.SingleAsync(r => r.CampaignId == e.CampaignId)).TotalScore);
        Assert.Null((await db.Campaigns.SingleAsync(x => x.Id == e.CampaignId)).InterviewPolicyVersion);
    }

    // ── (test brief) apply → MỌI điểm được ghi đè, audit có điểm cũ, con trỏ dời ───────────────
    [Fact]
    public async Task Apply_ghi_de_moi_diem_audit_diem_cu_va_doi_con_tro()
    {
        var e = Setup();
        using var _d = e.Db;
        var c1 = Guid.NewGuid();
        var c2 = Guid.NewGuid();
        SeedRanking(e.Db, e.CampaignId, c1, total: 60m, bag: Bag(80m, 40m));   // min_pct = 40
        SeedRanking(e.Db, e.CampaignId, c2, total: 30m, bag: Bag(90m, 10m));   // min_pct = 10

        const string expr = "min_pct";
        var preview = await Preview(e.Controller, e.CampaignId, "Interview", expr);
        var policy = await CreatePolicy(e.Controller, e.CampaignId, "Interview", expr);

        var action = await e.Controller.ApplyScoringPolicy(
            e.CampaignId, policy.Id, new ApplyScoringPolicyRequest { Fingerprint = preview.Fingerprint }, default);
        var result = Assert.IsType<ApplyScoringPolicyResult>(Assert.IsType<OkObjectResult>(action.Result).Value);
        Assert.Equal(2, result.Applied);
        Assert.Equal(policy.Version, result.Version);

        using var db = e.Db.NewContext();
        var rows = (await db.CampaignRankings.Where(r => r.CampaignId == e.CampaignId).ToListAsync())
            .ToDictionary(r => r.CandidateId);
        Assert.Equal(40m, rows[c1].TotalScore);   // ghi đè bằng điểm chính sách mới
        Assert.Equal(10m, rows[c2].TotalScore);
        Assert.All(rows.Values, r =>
        {
            Assert.Equal(policy.Version, r.PolicyVersion);
            Assert.Equal(policy.Name, r.PolicyName);
            Assert.False(r.ScoreFallback);
        });

        // Con trỏ campaign nay trỏ vào version vừa áp.
        Assert.Equal(policy.Version,
            (await db.Campaigns.SingleAsync(x => x.Id == e.CampaignId)).InterviewPolicyVersion);

        // Audit: 1 dòng ApplyScoringPolicy, Summary CHỨA điểm cũ (60 và 30).
        var audit = await db.AuditLogs.SingleAsync(a => a.Action == AuditAction.ApplyScoringPolicy);
        Assert.Equal(e.ActorUserId, audit.ActorUserId);
        Assert.Equal(e.CampaignId, audit.EntityId);
        Assert.Contains("60", audit.Summary);
        Assert.Contains("30", audit.Summary);
        Assert.Contains(c1.ToString(), audit.Summary);
    }

    // ── (test brief) HrMember gọi apply → 403 ─────────────────────────────────────────────────
    [Fact]
    public async Task HrMember_goi_apply_thi_403()
    {
        var e = Setup(orgRole: "HrMember");
        using var _d = e.Db;
        SeedRanking(e.Db, e.CampaignId, Guid.NewGuid(), 60m, Bag(80m, 40m));
        var policy = SeedPolicy(e.Db, e.CampaignId, version: 2, expr: "min_pct");
        var fp = ScoringPolicyFingerprint.Compute(policy.Expression, policy.PassScorePct, policy.EngineVersion);

        var action = await e.Controller.ApplyScoringPolicy(
            e.CampaignId, policy.Id, new ApplyScoringPolicyRequest { Fingerprint = fp }, default);

        Assert.Equal(StatusCodes.Status403Forbidden,
            Assert.IsType<ObjectResult>(action.Result).StatusCode);

        // Không ghi đè gì.
        using var db = e.Db.NewContext();
        Assert.Equal(60m, (await db.CampaignRankings.SingleAsync(r => r.CampaignId == e.CampaignId)).TotalScore);
    }

    // ── preview: biểu thức hỏng → 400 kèm errors (HĐ-2), KHÔNG chạm dữ liệu ────────────────────
    [Fact]
    public async Task Preview_bieu_thuc_hong_tra_400_errors()
    {
        var e = Setup();
        using var _d = e.Db;
        SeedRanking(e.Db, e.CampaignId, Guid.NewGuid(), 60m, Bag(80m, 40m));

        var action = await e.Controller.PreviewScoringPolicy(e.CampaignId,
            new ScoringPolicyPreviewRequest { Kind = "Interview", Expression = "weighted_avg_pct * " },
            cursor: null, limit: null, default);

        var bad = Assert.IsType<BadRequestObjectResult>(action.Result);
        Assert.Contains("SYNTAX_ERROR", JsonSerializer.Serialize(bad.Value));
    }

    // ── preview: đổi hạng ⇒ rankChanged, fingerprint khớp giữa hai lần gọi cùng cấu hình ───────
    [Fact]
    public async Task Preview_bat_doi_hang_va_fingerprint_on_dinh()
    {
        var e = Setup();
        using var _d = e.Db;
        var top = Guid.NewGuid();       // weighted 60, nhưng min_pct chỉ 40
        var bottom = Guid.NewGuid();    // weighted 50, min_pct 50
        SeedRanking(e.Db, e.CampaignId, top, total: 60m, bag: Bag(80m, 40m));
        SeedRanking(e.Db, e.CampaignId, bottom, total: 50m, bag: Bag(50m, 50m));

        var res = await Preview(e.Controller, e.CampaignId, "Interview", "min_pct");
        var byId = res.Rows.ToDictionary(r => r.CandidateId);
        // Cũ: top 60 > bottom 50 ⇒ top hạng 1. Mới (min_pct): bottom 50 > top 40 ⇒ đảo.
        Assert.Equal(1, byId[top].OldRank);
        Assert.Equal(2, byId[bottom].OldRank);
        Assert.True(byId[top].RankChanged);
        Assert.True(byId[bottom].RankChanged);
        Assert.Equal(1, byId[bottom].NewRank);
        Assert.Equal(2, byId[top].NewRank);

        var again = await Preview(e.Controller, e.CampaignId, "Interview", "min_pct");
        Assert.Equal(res.Fingerprint, again.Fingerprint);
    }

    // ── apply đường CvScreening: đổi thước ⇒ điểm khớp CV ghi đè, con trỏ CV dời ──────────────
    [Fact]
    public async Task Apply_CvScreening_ghi_de_overall_match_score()
    {
        var e = Setup();
        using var _d = e.Db;
        // campaign cần job_needs (need_count) để chạy biểu thức CV.
        using (var w = e.Db.NewContext())
        {
            var camp = await w.Campaigns.SingleAsync(x => x.Id == e.CampaignId);
            camp.JobNeeds = Enumerable.Range(0, 2).Select(i => new JobNeed
            {
                NeedId = $"need-{i}", Category = JobNeedCategories.Technical, Text = $"Nhu cầu {i}",
                Source = JobNeedSources.HrEdited,
            }).ToList();
            await w.SaveChangesAsync();
        }

        var cid = Guid.NewGuid();
        using (var w = e.Db.NewContext())
        {
            w.CvSubmissions.Add(new CvSubmission
            {
                Id = cid, CampaignId = e.CampaignId, Email = "a@x.com", CvParsedText = "CV",
                ParseStatus = CvParseStatus.Done, Status = CvSubmissionStatus.Analyzed,
                OverallMatchScore = 50, ScreeningVersion = 2,
                Strengths = new()
                {
                    new NeedAssessment { NeedId = "need-0", Area = "a", Level = NeedLevels.Strong, Evidence = "x" },
                },
                Gaps = new()
                {
                    new NeedAssessment { NeedId = "need-1", Area = "b", Level = NeedLevels.Weak, Evidence = "x" },
                },
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            });
            await w.SaveChangesAsync();
        }

        // must-have gate: 1/2 met ⇒ 0.
        const string gate = "if(must_have_met < must_have_total, 0, 100)";
        var preview = await Preview(e.Controller, e.CampaignId, "CvScreening", gate, pass: null);
        Assert.Equal(0m, preview.Rows.Single().NewScore);

        var policy = await CreatePolicy(e.Controller, e.CampaignId, "CvScreening", gate, pass: null);
        var action = await e.Controller.ApplyScoringPolicy(
            e.CampaignId, policy.Id, new ApplyScoringPolicyRequest { Fingerprint = preview.Fingerprint }, default);
        var result = Assert.IsType<ApplyScoringPolicyResult>(Assert.IsType<OkObjectResult>(action.Result).Value);
        Assert.Equal(1, result.Applied);

        using var db = e.Db.NewContext();
        var cand = await db.CvSubmissions.SingleAsync(c => c.Id == cid);
        Assert.Equal(0, cand.OverallMatchScore);                 // ghi đè bằng điểm gate
        Assert.Equal(policy.Version, cand.ScoringPolicyVersion);  // re-pin
        Assert.Equal(policy.Version,
            (await db.Campaigns.SingleAsync(x => x.Id == e.CampaignId)).CvPolicyVersion);
    }

    // ── apply khi CHƯA có ai được chấm → 400 ──────────────────────────────────────────────────
    [Fact]
    public async Task Apply_chua_co_ai_duoc_cham_tra_400()
    {
        var e = Setup(status: CampaignStatus.Draft);
        using var _d = e.Db;
        var policy = await CreatePolicy(e.Controller, e.CampaignId, "Interview", "min_pct");

        var action = await e.Controller.ApplyScoringPolicy(
            e.CampaignId, policy.Id,
            new ApplyScoringPolicyRequest
            {
                Fingerprint = ScoringPolicyFingerprint.Compute(policy.Expression, policy.PassScorePct, policy.EngineVersion),
            },
            default);

        Assert.IsType<BadRequestObjectResult>(action.Result);
    }

    // ── apply với policyId của MẪU hệ thống → 404 (không phải bản của campaign) ────────────────
    [Fact]
    public async Task Apply_policy_la_mau_he_thong_tra_404()
    {
        var e = Setup();
        using var _d = e.Db;
        SeedRanking(e.Db, e.CampaignId, Guid.NewGuid(), 60m, Bag(80m, 40m));

        using var db = e.Db.NewContext();
        var templateId = await db.ScoringPolicies
            .Where(p => p.CampaignId == null && p.Kind == ScoringExpressionKind.Interview)
            .Select(p => p.Id).FirstAsync();

        var action = await e.Controller.ApplyScoringPolicy(
            e.CampaignId, templateId, new ApplyScoringPolicyRequest { Fingerprint = "x" }, default);

        Assert.IsType<NotFoundResult>(action.Result);
    }
}
