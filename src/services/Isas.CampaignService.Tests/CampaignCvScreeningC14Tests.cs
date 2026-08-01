using Isas.CampaignService.DTOs;
using Isas.CampaignService.Models;
using Isas.CampaignService.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;

namespace Isas.CampaignService.Tests;

/// <summary>
/// C14 — Sàng CV async (AI chấm khớp + callback + shortlist + PATCH). TÁI DÙNG campaign_criteria; 0 credit.
/// (a) Filtered → publish cv_screening_queue + Analyzing;
/// (b) callback cv-result → candidate_criterion_scores + overall_match_score + Analyzed;
/// (c) cv-failed → AnalysisFailed;
/// (d) callback 2 lần → không nhân đôi điểm;
/// (e) callback sau Invited → bỏ qua (không lật);
/// (f) ?sort=score → DESC (null xuống cuối);
/// (g) PATCH email → audit_logs; đã Invited → InvalidOperationException (409).
/// Publisher mock (không cần broker); SQLite in-mem (CampaignTestDb).
/// </summary>
public class CampaignCvScreeningC14Tests
{
    private static IConfiguration Config() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Internal:CallbackBase"] = "http://campaign:8080" })
            .Build();

    private static CvScreeningService NewService(CampaignDbContext db, ICvScreeningPublisher? publisher = null) =>
        new(db, publisher ?? Mock.Of<ICvScreeningPublisher>(), Config(),
            Mock.Of<ILogger<CvScreeningService>>());

    private static Campaign SeedActiveCampaign(CampaignTestDb tdb, Guid owner, string? domain = "BE")
    {
        var camp = CampaignTestDb.NewCampaign(owner, CampaignStatus.Active);
        camp.Domain = domain;
        camp.JDText = "JD: cần Backend .NET";
        tdb.Db.Campaigns.Add(camp);
        tdb.Db.SaveChanges();
        return camp;
    }

    private static List<CampaignCriterion> SeedCriteria(CampaignTestDb tdb, Guid campaignId, int count = 2)
    {
        var now = DateTime.UtcNow;
        var list = Enumerable.Range(0, count).Select(i => new CampaignCriterion
        {
            Id = Guid.NewGuid(),
            CampaignId = campaignId,
            OrderNo = i,
            Name = $"Tiêu chí {i}",
            Description = $"mô tả {i}",
            Weight = Math.Round(1m / count, 4),
            MaxScore = 5,
            Source = CriterionSource.HrEdited,
            CreatedAt = now,
            UpdatedAt = now
        }).ToList();
        tdb.Db.CampaignCriteria.AddRange(list);
        tdb.Db.SaveChanges();
        return list;
    }

    private static CvSubmission SeedCandidate(
        CampaignTestDb tdb, Guid campaignId, CvSubmissionStatus status,
        string? email = null, int? overall = null, string? parsedText = "CV text a@x.com")
    {
        var now = DateTime.UtcNow;
        var cand = new CvSubmission
        {
            Id = Guid.NewGuid(),
            CampaignId = campaignId,
            Email = email,
            CvParsedText = parsedText,
            CvFileUrl = $"campaigns/{campaignId}/candidates/x.pdf",
            ParseStatus = CvParseStatus.Done,
            Status = status,
            OverallMatchScore = overall,
            CreatedAt = now,
            UpdatedAt = now
        };
        tdb.Db.CvSubmissions.Add(cand);
        tdb.Db.SaveChanges();
        return cand;
    }

    // (a) Filtered → publish job + chuyển Analyzing + set last_screening_published_at; job mang criteria.
    [Fact]
    public async Task Filtered_publish_va_chuyen_Analyzing()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = SeedActiveCampaign(tdb, owner);
        var criteria = SeedCriteria(tdb, camp.Id, 2);
        var c1 = SeedCandidate(tdb, camp.Id, CvSubmissionStatus.Filtered, email: "a@x.com");
        var c2 = SeedCandidate(tdb, camp.Id, CvSubmissionStatus.Filtered, email: "b@x.com");

        var published = new List<CvScreeningJob>();
        var pub = new Mock<ICvScreeningPublisher>();
        pub.Setup(p => p.PublishAsync(It.IsAny<CvScreeningJob>(), It.IsAny<CancellationToken>()))
           .Callback<CvScreeningJob, CancellationToken>((j, _) => published.Add(j))
           .Returns(Task.CompletedTask);

        var svc = NewService(tdb.NewContext(), pub.Object);
        var n = await svc.PublishScreeningJobsAsync(owner, camp.Id, default);

        Assert.Equal(2, n);
        Assert.Equal(2, published.Count);
        Assert.All(published, j =>
        {
            Assert.Equal(2, j.Criteria.Count);                 // TÁI DÙNG campaign_criteria
            Assert.Equal("BE", j.JobCategory);
            Assert.Equal("http://campaign:8080", j.CallbackBase);
            Assert.False(string.IsNullOrEmpty(j.CvText));
        });
        Assert.Contains(published, j => j.CandidateId == c1.Id);
        Assert.Contains(published, j => j.CandidateId == c2.Id);

        using var check = tdb.NewContext();
        var rows = await check.CvSubmissions.Where(c => c.CampaignId == camp.Id).ToListAsync();
        Assert.All(rows, r =>
        {
            Assert.Equal(CvSubmissionStatus.Analyzing, r.Status);
            Assert.NotNull(r.LastScreeningPublishedAt);
        });
    }

    // (a-bis) chỉ publish ứng viên Filtered — Rejected/Analyzed KHÔNG bị đụng.
    [Fact]
    public async Task Publish_chi_Filtered_bo_qua_Rejected()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = SeedActiveCampaign(tdb, owner);
        SeedCriteria(tdb, camp.Id, 1);
        SeedCandidate(tdb, camp.Id, CvSubmissionStatus.Filtered, email: "a@x.com");
        var rejected = SeedCandidate(tdb, camp.Id, CvSubmissionStatus.Rejected, email: "b@x.com");

        var svc = NewService(tdb.NewContext());
        var n = await svc.PublishScreeningJobsAsync(owner, camp.Id, default);

        Assert.Equal(1, n);
        using var check = tdb.NewContext();
        Assert.Equal(CvSubmissionStatus.Rejected, (await check.CvSubmissions.FindAsync(rejected.Id))!.Status);
    }

    // (b) cv-result → ghi candidate_criterion_scores + overall_match_score + Analyzed; kẹp điểm + bỏ id bịa.
    [Fact]
    public async Task Callback_cv_result_ghi_diem_va_Analyzed()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = SeedActiveCampaign(tdb, owner);
        var criteria = SeedCriteria(tdb, camp.Id, 2);
        var cand = SeedCandidate(tdb, camp.Id, CvSubmissionStatus.Analyzing, email: "a@x.com");

        var req = new CvResultCallbackRequest
        {
            Skills = new() { "C#", "SQL" },
            YearsExperience = 3.5m,
            Summary = "Ứng viên tốt",
            OverallMatchScore = 150,   // vượt trần → kẹp về 100
            CriterionMatches = new()
            {
                new() { CriterionId = criteria[0].Id, MatchScore = 4.0m, Reasoning = "ok" },
                new() { CriterionId = criteria[1].Id, MatchScore = 99m, Reasoning = "quá max → kẹp 5" },
                new() { CriterionId = Guid.NewGuid(), MatchScore = 3m, Reasoning = "id AI bịa → bỏ" },
            }
        };

        var svc = NewService(tdb.NewContext());
        var outcome = await svc.SaveCvResultAsync(cand.Id, req, default);
        Assert.Equal(CvResultOutcome.Analyzed, outcome);

        using var check = tdb.NewContext();
        var row = await check.CvSubmissions.FindAsync(cand.Id);
        Assert.Equal(CvSubmissionStatus.Analyzed, row!.Status);
        Assert.Equal(100, row.OverallMatchScore);          // kẹp [0,100]
        Assert.Equal(3.5m, row.YearsExperience);
        Assert.Contains("C#", row.Skills!);

        var scores = await check.CandidateCriterionScores.Where(s => s.CvSubmissionId == cand.Id).ToListAsync();
        Assert.Equal(2, scores.Count);                     // id bịa bị bỏ (2 hợp lệ)
        Assert.Equal(5m, scores.Single(s => s.CriterionId == criteria[1].Id).MatchScore);   // kẹp về max_score=5
    }

    // (c) cv-failed → AnalysisFailed + reason.
    [Fact]
    public async Task Callback_cv_failed_set_AnalysisFailed()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = SeedActiveCampaign(tdb, owner);
        var cand = SeedCandidate(tdb, camp.Id, CvSubmissionStatus.Analyzing, email: "a@x.com");

        var svc = NewService(tdb.NewContext());
        var outcome = await svc.MarkCvFailedAsync(cand.Id, "Gemini timeout vĩnh viễn", default);
        Assert.Equal(CvFailedOutcome.Failed, outcome);

        using var check = tdb.NewContext();
        var row = await check.CvSubmissions.FindAsync(cand.Id);
        Assert.Equal(CvSubmissionStatus.AnalysisFailed, row!.Status);
        Assert.Contains("timeout", row.RejectReason);
    }

    // (c-bis) cv-failed muộn khi đã Analyzed → KHÔNG hạ cấp (no-op).
    [Fact]
    public async Task Callback_cv_failed_khi_da_Analyzed_khong_ha_cap()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = SeedActiveCampaign(tdb, owner);
        var cand = SeedCandidate(tdb, camp.Id, CvSubmissionStatus.Analyzed, email: "a@x.com", overall: 80);

        var svc = NewService(tdb.NewContext());
        var outcome = await svc.MarkCvFailedAsync(cand.Id, "late fail", default);
        Assert.Equal(CvFailedOutcome.SkippedAnalyzed, outcome);

        using var check = tdb.NewContext();
        Assert.Equal(CvSubmissionStatus.Analyzed, (await check.CvSubmissions.FindAsync(cand.Id))!.Status);
    }

    // (c-ter) cv-result về khi đang AnalysisFailed (worker callback muộn sau timeout) → recover Analyzed.
    [Fact]
    public async Task Callback_cv_result_recover_tu_AnalysisFailed()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = SeedActiveCampaign(tdb, owner);
        var criteria = SeedCriteria(tdb, camp.Id, 1);
        var cand = SeedCandidate(tdb, camp.Id, CvSubmissionStatus.AnalysisFailed, email: "a@x.com");
        cand.RejectReason = "timeout cũ";
        tdb.Db.SaveChanges();

        var req = new CvResultCallbackRequest
        {
            OverallMatchScore = 70,
            CriterionMatches = new() { new() { CriterionId = criteria[0].Id, MatchScore = 3m } }
        };

        var svc = NewService(tdb.NewContext());
        await svc.SaveCvResultAsync(cand.Id, req, default);

        using var check = tdb.NewContext();
        var row = await check.CvSubmissions.FindAsync(cand.Id);
        Assert.Equal(CvSubmissionStatus.Analyzed, row!.Status);
        Assert.Null(row.RejectReason);   // xoá lý do fail cũ khi recover
    }

    // (d) callback cv-result 2 lần → điểm KHÔNG nhân đôi (idempotent: xoá cũ rồi ghi lại).
    [Fact]
    public async Task Callback_cv_result_hai_lan_khong_nhan_doi()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = SeedActiveCampaign(tdb, owner);
        var criteria = SeedCriteria(tdb, camp.Id, 2);
        var cand = SeedCandidate(tdb, camp.Id, CvSubmissionStatus.Analyzing, email: "a@x.com");

        CvResultCallbackRequest Req() => new()
        {
            OverallMatchScore = 88,
            CriterionMatches = new()
            {
                new() { CriterionId = criteria[0].Id, MatchScore = 4m },
                new() { CriterionId = criteria[1].Id, MatchScore = 3m },
            }
        };

        await NewService(tdb.NewContext()).SaveCvResultAsync(cand.Id, Req(), default);
        await NewService(tdb.NewContext()).SaveCvResultAsync(cand.Id, Req(), default);   // callback lần 2

        using var check = tdb.NewContext();
        Assert.Equal(2, await check.CandidateCriterionScores.CountAsync(s => s.CvSubmissionId == cand.Id));  // vẫn 2, không 4
        Assert.Equal(88, (await check.CvSubmissions.FindAsync(cand.Id))!.OverallMatchScore);
    }

    // (e) callback cv-result về SAU khi đã Invited → bỏ qua (không ghi điểm, giữ Invited).
    [Fact]
    public async Task Callback_cv_result_sau_Invited_bo_qua()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = SeedActiveCampaign(tdb, owner);
        var criteria = SeedCriteria(tdb, camp.Id, 1);
        var cand = SeedCandidate(tdb, camp.Id, CvSubmissionStatus.Invited, email: "a@x.com", overall: 90);

        var req = new CvResultCallbackRequest
        {
            OverallMatchScore = 10,
            CriterionMatches = new() { new() { CriterionId = criteria[0].Id, MatchScore = 1m } }
        };

        var svc = NewService(tdb.NewContext());
        var outcome = await svc.SaveCvResultAsync(cand.Id, req, default);
        Assert.Equal(CvResultOutcome.SkippedInvited, outcome);

        using var check = tdb.NewContext();
        var row = await check.CvSubmissions.FindAsync(cand.Id);
        Assert.Equal(CvSubmissionStatus.Invited, row!.Status);      // giữ nguyên
        Assert.Equal(90, row.OverallMatchScore);                 // KHÔNG bị ghi đè
        Assert.Equal(0, await check.CandidateCriterionScores.CountAsync(s => s.CvSubmissionId == cand.Id));
    }

    // (e-bis) candidate không tồn tại → KeyNotFoundException (→404).
    [Fact]
    public async Task Callback_candidate_khong_ton_tai_nem_KeyNotFound()
    {
        using var tdb = new CampaignTestDb();
        var svc = NewService(tdb.NewContext());
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            svc.SaveCvResultAsync(Guid.NewGuid(), new CvResultCallbackRequest(), default));
    }

    // (f) ?sort=score → DESC theo overall_match_score; chưa Analyzed (null) xuống cuối.
    [Fact]
    public async Task Shortlist_sort_score_DESC_null_cuoi()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = SeedActiveCampaign(tdb, owner);
        SeedCandidate(tdb, camp.Id, CvSubmissionStatus.Analyzed, email: "mid@x.com", overall: 70);
        SeedCandidate(tdb, camp.Id, CvSubmissionStatus.Analyzed, email: "top@x.com", overall: 90);
        SeedCandidate(tdb, camp.Id, CvSubmissionStatus.Analyzing, email: "none@x.com", overall: null);

        var svc = NewService(tdb.NewContext());
        var list = (await svc.GetCandidatesAsync(owner, camp.Id, null, null, null, "score", null, null, null, default)).Items;

        Assert.Equal(3, list.Count);
        Assert.Equal(90, list[0].OverallMatchScore);
        Assert.Equal(70, list[1].OverallMatchScore);
        Assert.Null(list[2].OverallMatchScore);     // null xuống cuối
    }

    // (f-bis) filter minScore + status; ngoài org → 404.
    [Fact]
    public async Task Shortlist_filter_minScore_va_ngoai_org_404()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = SeedActiveCampaign(tdb, owner);
        SeedCandidate(tdb, camp.Id, CvSubmissionStatus.Analyzed, email: "a@x.com", overall: 50);
        SeedCandidate(tdb, camp.Id, CvSubmissionStatus.Analyzed, email: "b@x.com", overall: 85);

        var svc = NewService(tdb.NewContext());
        var filtered = (await svc.GetCandidatesAsync(owner, camp.Id, null, 70, null, "score", null, null, null, default)).Items;
        Assert.Single(filtered);
        Assert.Equal(85, filtered[0].OverallMatchScore);

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            svc.GetCandidatesAsync(Guid.NewGuid() /* org khác */, camp.Id, null, null, null, "score", null, null, null, default));
    }

    // (g) PATCH email → cập nhật + audit_logs có row EditCandidate.
    [Fact]
    public async Task Patch_email_cap_nhat_va_ghi_audit()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = SeedActiveCampaign(tdb, owner);
        var cand = SeedCandidate(tdb, camp.Id, CvSubmissionStatus.Analyzed, email: null);

        var svc = NewService(tdb.NewContext());
        await svc.PatchCandidateAsync(owner, owner, camp.Id, cand.Id,
            new PatchCandidateRequest { Email = "New@X.com", FullName = "  Nguyễn Văn A  " }, default);

        using var check = tdb.NewContext();
        var row = await check.CvSubmissions.FindAsync(cand.Id);
        Assert.Equal("new@x.com", row!.Email);        // chuẩn hoá lowercase
        Assert.Equal("Nguyễn Văn A", row.FullName);   // trim
        Assert.True(await check.AuditLogs.AnyAsync(a =>
            a.Action == AuditAction.EditCandidate && a.EntityId == cand.Id));
    }

    // (g-bis) PATCH sau khi đã Invited → InvalidOperationException (→409).
    [Fact]
    public async Task Patch_sau_Invited_nem_InvalidOperationException()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = SeedActiveCampaign(tdb, owner);
        var cand = SeedCandidate(tdb, camp.Id, CvSubmissionStatus.Invited, email: "a@x.com");

        var svc = NewService(tdb.NewContext());
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.PatchCandidateAsync(owner, owner, camp.Id, cand.Id,
                new PatchCandidateRequest { FullName = "X" }, default));
    }

    // (g-ter) PATCH email trùng ứng viên khác trong campaign → ArgumentException (→400).
    [Fact]
    public async Task Patch_email_trung_nem_ArgumentException()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = SeedActiveCampaign(tdb, owner);
        SeedCandidate(tdb, camp.Id, CvSubmissionStatus.Analyzed, email: "taken@x.com");
        var cand = SeedCandidate(tdb, camp.Id, CvSubmissionStatus.Filtered, email: null);

        var svc = NewService(tdb.NewContext());
        await Assert.ThrowsAsync<ArgumentException>(() =>
            svc.PatchCandidateAsync(owner, owner, camp.Id, cand.Id,
                new PatchCandidateRequest { Email = "taken@x.com" }, default));
    }
}
