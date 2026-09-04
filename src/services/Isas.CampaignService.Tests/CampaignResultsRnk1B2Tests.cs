using System.Security.Claims;
using System.Text;
using CsvHelper;
using Isas.CampaignService.Controllers;
using Isas.CampaignService.DTOs;
using Isas.CampaignService.Models;
using Isas.CampaignService.Services;
using Isas.Shared.Scoring;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

using CampaignSvc = Isas.CampaignService.Services.CampaignService;

namespace Isas.CampaignService.Tests;

/// <summary>
/// RNK1 · HĐ-3 — <c>CampaignResultRow</c> + CSV + PDF mang số câu (từ <c>scoring_inputs</c>) + điểm
/// sàng CV + cờ luật câu bỏ trống. Cột CSV thêm ở ĐUÔI (Index 13..21), thứ tự cột cũ không đổi.
/// <c>belowCutoff</c> RỖNG ở B2 (B4 điền). KHÔNG query phụ theo ứng viên.
/// </summary>
public class CampaignResultsRnk1B2Tests
{
    private static CampaignSvc NewService(CampaignDbContext db) =>
        new(db, Mock.Of<IFileService>(), Mock.Of<ILogger<CampaignSvc>>(),
            Mock.Of<IParserService>(), Mock.Of<ICriteriaSuggester>(),
            Mock.Of<IInvitationEmailPublisher>());

    private static Campaign SeedCampaign(
        CampaignDbContext db, Guid orgId, int? questionsPerSession = null, int questionBank = 0)
    {
        var c = CampaignTestDb.NewCampaign(orgId, CampaignStatus.Active);
        c.QuestionsPerSession = questionsPerSession;
        for (var i = 0; i < questionBank; i++)
            c.Questions.Add(new CampaignQuestion
            {
                Id = Guid.NewGuid(), CampaignId = c.Id, OrgId = orgId,
                QuestionText = $"Q{i}", Source = QuestionSource.CustomHr,
                IsRequired = true, CreatedAt = DateTime.UtcNow,
            });
        db.Campaigns.Add(c);
        db.SaveChanges();
        return c;
    }

    private static ScoringInputsSnapshot Bag(
        int answered, int total, int? seedAnswered, int? seedTotal, bool? skipPenalty)
        => new(
            new[] { new CriterionInputSnapshot("Giao tiếp", 80m, 1.0m, 5) },
            answered, total, seedAnswered, seedTotal, skipPenalty);

    private static CampaignRanking SeedRanking(
        CampaignDbContext db, Guid campaignId, Guid candidateId, decimal score = 80m,
        ScoringInputsSnapshot? bag = null)
    {
        var r = new CampaignRanking
        {
            Id = Guid.NewGuid(), CampaignId = campaignId, CandidateId = candidateId,
            SessionId = Guid.NewGuid(), TotalScore = score, ScoringInputs = bag,
            UpdatedAt = DateTime.UtcNow,
        };
        db.CampaignRankings.Add(r);
        db.SaveChanges();
        return r;
    }

    // Membership + (tuỳ chọn) CvSubmission gắn kèm. Không cvScore ⇒ mời bằng email (không CV).
    private static void SeedMembership(
        CampaignDbContext db, Guid campaignId, Guid candidateId,
        int? cvScore = null, string? cvRisk = null, int? cvVersion = null)
    {
        var email = $"c{candidateId:N}@x.co";   // email DISTINCT/ứng viên (dedup BK21)
        Guid? cvId = null;
        if (cvScore is not null || cvRisk is not null || cvVersion is not null)
        {
            cvId = Guid.NewGuid();
            db.CvSubmissions.Add(new CvSubmission
            {
                Id = cvId.Value, CampaignId = campaignId, Email = email, FullName = "Ứng viên",
                Status = CvSubmissionStatus.Analyzed, ParseStatus = CvParseStatus.Done,
                OverallMatchScore = cvScore, VerificationRisk = cvRisk, ScreeningVersion = cvVersion,
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            });
        }
        db.CampaignMemberships.Add(new CampaignMembership
        {
            Id = Guid.NewGuid(), CampaignId = campaignId, CandidateId = candidateId,
            CvSubmissionId = cvId, FullName = "Ứng viên", Email = email,
            Status = MembershipStatus.Joined, JoinedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        db.SaveChanges();
    }

    // ── 1. Ranking có snapshot đầy đủ + CV ⇒ row có đủ khoá HĐ-3 ──────────────────────────────────
    [Fact]
    public async Task Row_CoSnapshot_VaCV_MangDu9Khoa()
    {
        using var tdb = new CampaignTestDb();
        var orgId = Guid.NewGuid();
        var camp = SeedCampaign(tdb.Db, orgId, questionsPerSession: 5, questionBank: 12);
        var cand = Guid.NewGuid();
        SeedRanking(tdb.Db, camp.Id, cand, bag: Bag(answered: 5, total: 7, seedAnswered: 3, seedTotal: 5, skipPenalty: true));
        SeedMembership(tdb.Db, camp.Id, cand, cvScore: 72, cvRisk: "High", cvVersion: 2);

        var res = await NewService(tdb.NewContext()).GetCampaignResultsAsync(orgId, camp.Id, default);
        var row = Assert.Single(res.Results);

        Assert.Equal(5, row.Answered);
        Assert.Equal(7, row.TotalQuestions);
        Assert.Equal(3, row.SeedAnswered);
        Assert.Equal(5, row.SeedTotal);
        Assert.True(row.SkipPenalty);
        Assert.Equal(72, row.CvMatchScore);
        Assert.Equal("High", row.CvVerificationRisk);
        Assert.Equal(2, row.CvScreeningVersion);
        Assert.Empty(row.BelowCutoff);   // B2 — B4 điền

        Assert.Equal(5, res.QuestionsPerSession);
        Assert.Equal(12, res.QuestionBankTotal);
    }

    // ── 2. Mời bằng email (membership không CvSubmissionId) ⇒ 3 khoá CV = null ────────────────────
    [Fact]
    public async Task Row_MoiBangEmail_KhongCV_CacKhoaCvNull()
    {
        using var tdb = new CampaignTestDb();
        var orgId = Guid.NewGuid();
        var camp = SeedCampaign(tdb.Db, orgId);
        var cand = Guid.NewGuid();
        SeedRanking(tdb.Db, camp.Id, cand, bag: Bag(4, 4, 4, 4, false));
        SeedMembership(tdb.Db, camp.Id, cand);   // không CV

        var row = Assert.Single((await NewService(tdb.NewContext())
            .GetCampaignResultsAsync(orgId, camp.Id, default)).Results);

        Assert.Null(row.CvMatchScore);
        Assert.Null(row.CvVerificationRisk);
        Assert.Null(row.CvScreeningVersion);
        Assert.Equal(4, row.SeedAnswered);   // số câu vẫn có
    }

    // ── 3. Snapshot TRƯỚC RNK1 (không seed_*) ⇒ seed_* + skipPenalty null, answered/total vẫn có ──
    [Fact]
    public async Task Row_SnapshotCu_SeedNull_AnsweredCoGiaTri()
    {
        using var tdb = new CampaignTestDb();
        var orgId = Guid.NewGuid();
        var camp = SeedCampaign(tdb.Db, orgId);
        var cand = Guid.NewGuid();
        SeedRanking(tdb.Db, camp.Id, cand, bag: Bag(8, 10, seedAnswered: null, seedTotal: null, skipPenalty: null));

        var row = Assert.Single((await NewService(tdb.NewContext())
            .GetCampaignResultsAsync(orgId, camp.Id, default)).Results);

        Assert.Null(row.SeedAnswered);
        Assert.Null(row.SeedTotal);
        Assert.Null(row.SkipPenalty);
        Assert.Equal(8, row.Answered);
        Assert.Equal(10, row.TotalQuestions);
    }

    // ── 3b. Ranking KHÔNG có ScoringInputs (event trước SCP1) ⇒ MỌI khoá số câu null ──────────────
    [Fact]
    public async Task Row_KhongCoScoringInputs_MoiKhoaSoCauNull()
    {
        using var tdb = new CampaignTestDb();
        var orgId = Guid.NewGuid();
        var camp = SeedCampaign(tdb.Db, orgId, questionsPerSession: null, questionBank: 0);
        SeedRanking(tdb.Db, camp.Id, Guid.NewGuid(), bag: null);

        var res = await NewService(tdb.NewContext()).GetCampaignResultsAsync(orgId, camp.Id, default);
        var row = Assert.Single(res.Results);

        Assert.Null(row.Answered);
        Assert.Null(row.TotalQuestions);
        Assert.Null(row.SeedAnswered);
        Assert.Null(row.SkipPenalty);
        Assert.Null(res.QuestionsPerSession);
        Assert.Equal(0, res.QuestionBankTotal);
    }

    // ── 4. UnscoredFlagged mang CvMatchScore/CvVerificationRisk ──────────────────────────────────
    [Fact]
    public async Task Unscored_MangDiemSangCV()
    {
        using var tdb = new CampaignTestDb();
        var orgId = Guid.NewGuid();
        var camp = SeedCampaign(tdb.Db, orgId);
        var cand = Guid.NewGuid();
        var sid = Guid.NewGuid();
        SeedMembership(tdb.Db, camp.Id, cand, cvScore: 55, cvRisk: "Medium", cvVersion: 1);
        tdb.Db.SessionFlags.Add(new SessionFlag
        {
            Id = Guid.NewGuid(), CampaignId = camp.Id, SessionId = sid, CandidateId = cand,
            SignalType = "tab_switch", DetectedAt = DateTime.UtcNow,
        });
        tdb.Db.SaveChanges();

        var res = await NewService(tdb.NewContext()).GetCampaignResultsAsync(orgId, camp.Id, default);
        var u = Assert.Single(res.UnscoredFlagged);

        Assert.Equal(55, u.CvMatchScore);
        Assert.Equal("Medium", u.CvVerificationRisk);
    }

    // ── 5. CSV: cột cũ 0..12 KHÔNG đổi + đuôi 13..21 đúng tên/thứ tự; PDF không ném ───────────────
    [Fact]
    public async Task Csv_HeaderDuoi_DungThuTu_PdfKhongNem()
    {
        using var tdb = new CampaignTestDb();
        var orgId = Guid.NewGuid();
        var camp = SeedCampaign(tdb.Db, orgId, questionsPerSession: 3, questionBank: 6);
        var c1 = Guid.NewGuid();
        var c2 = Guid.NewGuid();
        SeedRanking(tdb.Db, camp.Id, c1, 90m, Bag(3, 5, 2, 3, true));
        SeedRanking(tdb.Db, camp.Id, c2, 80m, bag: null);
        SeedMembership(tdb.Db, camp.Id, c1, cvScore: 88, cvRisk: "Low", cvVersion: 2);
        SeedMembership(tdb.Db, camp.Id, c2);

        var svc = NewService(tdb.NewContext());
        var csv = Encoding.UTF8.GetString(
            (await svc.ExportCampaignResultsAsync(orgId, camp.Id, "csv", default)).Content);
        var header = csv.Split('\n')[0].TrimEnd('\r');

        // cột cũ 0..12 nguyên vẹn
        Assert.StartsWith(
            "rank,candidate_id,session_id,total_score,result,scored_at,flags,full_name,email,rubric_version,"
            + "policy_version,policy_name,score_fallback,",
            header);
        // đuôi RNK1 13..21
        Assert.EndsWith(
            ",answered,total_questions,seed_answered,seed_total,skip_penalty,"
            + "cv_match_score,cv_verification_risk,cv_screening_version,below_cutoff",
            header);

        // dòng có snapshot đầy đủ + CV: đọc theo TÊN cột (không phụ thuộc vị trí)
        using var reader = new StringReader(csv);
        using var parser = new CsvReader(reader, System.Globalization.CultureInfo.InvariantCulture);
        parser.Read(); parser.ReadHeader();
        var rows = new List<IDictionary<string, object>>();
        while (parser.Read()) rows.Add(parser.GetRecord<dynamic>());
        var top = rows.First(r => (string)r["candidate_id"] == c1.ToString());
        Assert.Equal("3", top["answered"]);        // Bag(answered:3, total:5, seedAnswered:2, seedTotal:3)
        Assert.Equal("5", top["total_questions"]);
        Assert.Equal("2", top["seed_answered"]);
        Assert.Equal("3", top["seed_total"]);
        Assert.Equal("True", top["skip_penalty"]);
        Assert.Equal("88", top["cv_match_score"]);
        Assert.Equal("Low", top["cv_verification_risk"]);
        Assert.Equal("2", top["cv_screening_version"]);
        Assert.Equal(string.Empty, top["below_cutoff"]);

        // PDF: có + không có CV/snapshot ⇒ không ném
        var pdf = (await svc.ExportCampaignResultsAsync(orgId, camp.Id, "pdf", default)).Content;
        Assert.True(pdf.Length > 0);
        Assert.Equal("%PDF", Encoding.ASCII.GetString(pdf, 0, 4));
    }

    // ── 6. Interceptor: số SELECT KHÔNG scale theo số ứng viên (không N+1) ───────────────────────
    [Fact]
    public async Task GetResults_KhongN1_SoQueryKhongDoiTheoSoUngVien()
    {
        int QueriesFor(int candidates)
        {
            using var tdb = new CampaignTestDb();
            var orgId = Guid.NewGuid();
            var camp = SeedCampaign(tdb.Db, orgId, questionsPerSession: 5, questionBank: 10);
            for (var i = 0; i < candidates; i++)
            {
                var cand = Guid.NewGuid();
                SeedRanking(tdb.Db, camp.Id, cand, 80m - i, Bag(5, 7, 3, 5, true));
                SeedMembership(tdb.Db, camp.Id, cand, cvScore: 70, cvRisk: "Low", cvVersion: 2);
            }

            var spy = new SqlSpy();
            var svc = NewService(tdb.NewContext(spy));
            svc.GetCampaignResultsAsync(orgId, camp.Id, default).GetAwaiter().GetResult();
            return spy.SelectCount;
        }

        Assert.Equal(QueriesFor(1), QueriesFor(4));
    }

    private sealed class SqlSpy : DbCommandInterceptor
    {
        private int _selects;
        public int SelectCount => _selects;

        public override InterceptionResult<System.Data.Common.DbDataReader> ReaderExecuting(
            System.Data.Common.DbCommand command, CommandEventData eventData,
            InterceptionResult<System.Data.Common.DbDataReader> result)
        {
            System.Threading.Interlocked.Increment(ref _selects);
            return result;
        }

        public override ValueTask<InterceptionResult<System.Data.Common.DbDataReader>> ReaderExecutingAsync(
            System.Data.Common.DbCommand command, CommandEventData eventData,
            InterceptionResult<System.Data.Common.DbDataReader> result, CancellationToken cancellationToken = default)
        {
            System.Threading.Interlocked.Increment(ref _selects);
            return ValueTask.FromResult(result);
        }
    }
}
