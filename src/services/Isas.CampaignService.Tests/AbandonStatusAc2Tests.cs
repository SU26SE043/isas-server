using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using CsvHelper;
using Isas.CampaignService.DTOs;
using Isas.CampaignService.Models;
using Isas.CampaignService.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using UglyToad.PdfPig;

using CampaignSvc = Isas.CampaignService.Services.CampaignService;

namespace Isas.CampaignService.Tests;

/// <summary>
/// AC2 · B2 — khu <c>unscoredFlagged</c> nói được trạng thái buổi. Trước đây mọi dòng chưa chấm in
/// "Chưa chấm" ⇒ HR tưởng lát nữa sẽ có điểm, kể cả khi buổi đã BỎ NGANG.
///  • <c>campaign_membership.abandon_reason</c>: ghi từ SessionAbandoned của buổi ĐANG giữ; sự kiện lượt
///    cũ đến muộn không ghi đè; Start lượt mới xoá cùng lúc gán SessionId mới;
///  • 4 field mới chỉ mang trạng thái khi dòng là buổi membership đang giữ (IsLatestAttempt);
///  • CSV và PDF ra CÙNG nhãn qua MỘT helper (F16).
/// </summary>
public class AbandonStatusAc2Tests
{
    // ── handler: abandon_reason ──────────────────────────────────────────────────────────────────

    private static RankingEventHandler NewHandler(CampaignDbContext db) =>
        new(db, NullLogger<RankingEventHandler>.Instance);

    private static CampaignMembership SeedMember(CampaignTestDb t, Guid campaignId, Guid? sessionId,
        InterviewProgressStatus? status, string? abandonReason = null)
    {
        var m = CampaignTestDb.NewMembership(campaignId, Guid.NewGuid(), sessionId: sessionId, interviewStatus: status);
        m.AbandonReason = abandonReason;
        t.Db.CampaignMemberships.Add(m);
        t.Db.SaveChanges();
        return m;
    }

    private static Campaign SeedCampaign(CampaignTestDb t, Guid? orgId = null)
    {
        var c = CampaignTestDb.NewCampaign(orgId ?? Guid.NewGuid(), CampaignStatus.Active);
        t.Db.Campaigns.Add(c);
        t.Db.SaveChanges();
        return c;
    }

    private static CampaignMembership Reload(CampaignTestDb t, Guid membershipId)
        => t.NewContext().CampaignMemberships.AsNoTracking().Single(x => x.Id == membershipId);

    [Theory]
    [InlineData("no_scored_answer", "no_scored_answer")]
    [InlineData("  generation_failed  ", "generation_failed")]
    [InlineData("   ", null)]
    [InlineData("", null)]
    public async Task Abandoned_BuoiDangGiu_GhiLyDo_Trim_RongThanhNull(string reason, string? expected)
    {
        using var t = new CampaignTestDb();
        var camp = SeedCampaign(t);
        var s = Guid.NewGuid();
        var m = SeedMember(t, camp.Id, s, InterviewProgressStatus.InProgress);

        await NewHandler(t.NewContext()).HandleSessionAbandonedAsync(new SessionAbandonedMessage
        {
            SessionId = s, CampaignId = camp.Id, CandidateId = m.CandidateId!.Value, Reason = reason
        });

        var after = Reload(t, m.Id);
        Assert.Equal(InterviewProgressStatus.Abandoned, after.InterviewStatus);
        Assert.Equal(expected, after.AbandonReason);
    }

    // Lượt 1 (S1) bỏ ngang → làm lại thành S2 cũng bỏ ngang; bản phát lại sự kiện của S1 đến muộn
    // (outbox at-least-once) KHÔNG được thay lý do của S2.
    [Fact]
    public async Task Abandoned_SuKienLuotCuDenMuon_KhongGhiDeLyDoLuotHienTai()
    {
        using var t = new CampaignTestDb();
        var camp = SeedCampaign(t);
        var s1 = Guid.NewGuid();
        var s2 = Guid.NewGuid();
        var m = SeedMember(t, camp.Id, s2, InterviewProgressStatus.Abandoned, abandonReason: "expired_no_answer");

        await NewHandler(t.NewContext()).HandleSessionAbandonedAsync(new SessionAbandonedMessage
        {
            SessionId = s1, CampaignId = camp.Id, CandidateId = m.CandidateId!.Value, Reason = "generation_failed"
        });

        Assert.Equal("expired_no_answer", Reload(t, m.Id).AbandonReason);
    }

    [Fact]
    public async Task Abandoned_SuKienLuotCuDenMuon_LuotMoiDangChay_KhongGanLyDo()
    {
        using var t = new CampaignTestDb();
        var camp = SeedCampaign(t);
        var m = SeedMember(t, camp.Id, Guid.NewGuid(), InterviewProgressStatus.InProgress);

        await NewHandler(t.NewContext()).HandleSessionAbandonedAsync(new SessionAbandonedMessage
        {
            SessionId = Guid.NewGuid(), CampaignId = camp.Id, CandidateId = m.CandidateId!.Value, Reason = "no_scored_answer"
        });

        var after = Reload(t, m.Id);
        Assert.Null(after.AbandonReason);
        Assert.Equal(InterviewProgressStatus.InProgress, after.InterviewStatus);
    }

    [Fact]
    public async Task Abandoned_DaCompleted_KhongGhiLyDo()
    {
        using var t = new CampaignTestDb();
        var camp = SeedCampaign(t);
        var s = Guid.NewGuid();
        var m = SeedMember(t, camp.Id, s, InterviewProgressStatus.Completed);

        await NewHandler(t.NewContext()).HandleSessionAbandonedAsync(new SessionAbandonedMessage
        {
            SessionId = s, CampaignId = camp.Id, CandidateId = m.CandidateId!.Value, Reason = "no_scored_answer"
        });

        Assert.Null(Reload(t, m.Id).AbandonReason);
    }

    // ── Start lượt mới (ATT1) xoá abandon_reason cùng lúc gán SessionId mới ──────────────────────

    private static Campaign SeedStartableCampaign(CampaignTestDb t, int maxAttempts)
    {
        var camp = CampaignTestDb.NewCampaign(Guid.NewGuid(), CampaignStatus.Active);
        camp.Domain = "BE";
        camp.MaxAttempts = maxAttempts;
        var epoch = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (var i = 1; i <= 3; i++)
            camp.Questions.Add(new CampaignQuestion
            {
                Id = Guid.NewGuid(), CampaignId = camp.Id, OrgId = camp.OrgId, QuestionText = $"Câu {i}",
                Source = QuestionSource.CustomHr, IsRequired = false, CreatedAt = epoch.AddSeconds(i),
            });
        camp.Criteria.Add(new CampaignCriterion
        {
            Id = Guid.NewGuid(), CampaignId = camp.Id, OrderNo = 0, Name = "Communication",
            Weight = 1.0m, MaxScore = 5, Source = CriterionSource.HrEdited,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        t.Db.Campaigns.Add(camp);
        t.Db.SaveChanges();
        return camp;
    }

    private static Mock<ICampaignSessionClient> SessionReturning(Guid sessionId)
    {
        var m = new Mock<ICampaignSessionClient>();
        m.Setup(x => x.CreateOrGetSessionAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<IReadOnlyList<SessionCriterionInput>>(),
                It.IsAny<DateTime?>(), It.IsAny<bool?>(), It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<int?>(),
                It.IsAny<string>(), It.IsAny<int>(), It.IsAny<IReadOnlyList<SessionQuestionInput>?>(),
                It.IsAny<CampaignScoringPolicyInput?>(), It.IsAny<bool>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CampaignSessionResult(sessionId, new List<SessionQuestion>
            {
                new(Guid.NewGuid(), 1, "Câu 1", 120),
            }));
        return m;
    }

    private static ParticipationService NewParticipation(CampaignDbContext db, ICampaignSessionClient session) =>
        new(db, Mock.Of<IAuthProvisionClient>(), session, NullLogger<ParticipationService>.Instance);

    [Fact]
    public async Task Start_LuotMoi_XoaAbandonReason_CungLucGanSessionMoi()
    {
        using var t = new CampaignTestDb();
        var camp = SeedStartableCampaign(t, maxAttempts: 2);
        var s1 = Guid.NewGuid();
        var s2 = Guid.NewGuid();
        var m = SeedMember(t, camp.Id, s1, InterviewProgressStatus.Abandoned, abandonReason: "expired_no_answer");
        using (var ctx = t.NewContext())
        {
            var row = ctx.CampaignMemberships.Single(x => x.Id == m.Id);
            row.AttemptCount = 1;
            ctx.SaveChanges();
        }

        await NewParticipation(t.NewContext(), SessionReturning(s2).Object)
            .StartInterviewAsync(m.CandidateId!.Value, camp.Id, default);

        var after = Reload(t, m.Id);
        Assert.Equal(s2, after.SessionId);
        Assert.Equal(2, after.AttemptCount);
        Assert.Null(after.AbandonReason);
    }

    // Interview trả về CÙNG buổi đang giữ ⇒ không có lượt mới ⇒ lý do của buổi đó giữ nguyên.
    [Fact]
    public async Task Start_CungSession_KhongPhaiLuotMoi_GiuLyDo()
    {
        using var t = new CampaignTestDb();
        var camp = SeedStartableCampaign(t, maxAttempts: 2);
        var s1 = Guid.NewGuid();
        var m = SeedMember(t, camp.Id, s1, InterviewProgressStatus.Abandoned, abandonReason: "no_scored_answer");
        using (var ctx = t.NewContext())
        {
            ctx.CampaignMemberships.Single(x => x.Id == m.Id).AttemptCount = 1;
            ctx.SaveChanges();
        }

        await NewParticipation(t.NewContext(), SessionReturning(s1).Object)
            .StartInterviewAsync(m.CandidateId!.Value, camp.Id, default);

        Assert.Equal("no_scored_answer", Reload(t, m.Id).AbandonReason);
    }

    // ── bảng kết quả: 4 field mới ────────────────────────────────────────────────────────────────

    private static CampaignSvc NewService(CampaignDbContext db) =>
        new(db, Mock.Of<IFileService>(), Mock.Of<ILogger<CampaignSvc>>(),
            Mock.Of<IParserService>(), Mock.Of<ICriteriaSuggester>(),
            Mock.Of<IInvitationEmailPublisher>());

    private static void Flag(CampaignTestDb t, Guid campaignId, Guid sessionId, Guid candidateId, string type = "tab_switch")
    {
        t.Db.SessionFlags.Add(new SessionFlag
        {
            Id = Guid.NewGuid(), CampaignId = campaignId, SessionId = sessionId, CandidateId = candidateId,
            SignalType = type, DetectedAt = DateTime.UtcNow
        });
        t.Db.SaveChanges();
    }

    private static CampaignMembership Member(CampaignTestDb t, Guid campaignId, Guid candidateId, Guid? sessionId,
        InterviewProgressStatus? status, string? reason = null, DateTime? startedAt = null,
        DateTime? firstStartedAt = null)
    {
        // startedAt = mốc lượt đang giữ (attempt_started_at); firstStartedAt = mốc lần đầu
        // (interview_started_at), bỏ trống ⇒ trùng startedAt (membership một lượt).
        var m = CampaignTestDb.NewMembership(campaignId, candidateId, sessionId: sessionId, interviewStatus: status);
        m.AbandonReason = reason;
        m.AttemptStartedAt = startedAt;
        m.InterviewStartedAt = firstStartedAt ?? startedAt;
        t.Db.CampaignMemberships.Add(m);
        t.Db.SaveChanges();
        return m;
    }

    [Fact]
    public async Task Results_LuotHienTai_VaLuotCu_BonFieldDung()
    {
        using var t = new CampaignTestDb();
        var orgId = Guid.NewGuid();
        var camp = SeedCampaign(t, orgId);
        var cand = Guid.NewGuid();
        var s1 = Guid.NewGuid();   // lượt 1 — bỏ ngang, đã có cờ
        var s2 = Guid.NewGuid();   // lượt 2 — đang làm, cũng có cờ
        // Hai mốc KHÁC nhau: interviewStartedAt của dòng là mốc của CHÍNH buổi đó (lượt 2), không phải mốc
        // lần đầu (lượt 1, 26 giây trước — đúng hình dạng đo được trên dev 03/10).
        var firstStarted = new DateTime(2026, 10, 3, 1, 1, 37, DateTimeKind.Utc);
        var started = new DateTime(2026, 10, 3, 1, 2, 3, DateTimeKind.Utc);
        Member(t, camp.Id, cand, s2, InterviewProgressStatus.Abandoned, "no_scored_answer", started, firstStarted);
        Flag(t, camp.Id, s1, cand);
        Flag(t, camp.Id, s2, cand);

        var res = await NewService(t.NewContext()).GetCampaignResultsAsync(orgId, camp.Id, default);

        var current = Assert.Single(res.UnscoredFlagged, u => u.SessionId == s2);
        Assert.True(current.IsLatestAttempt);
        Assert.Equal("Abandoned", current.InterviewStatus);
        Assert.Equal("no_scored_answer", current.AbandonReason);
        Assert.Equal(started, current.InterviewStartedAt);

        var old = Assert.Single(res.UnscoredFlagged, u => u.SessionId == s1);
        Assert.False(old.IsLatestAttempt);
        Assert.Null(old.InterviewStatus);       // KHÔNG mượn trạng thái lượt đang giữ
        Assert.Null(old.AbandonReason);
        Assert.Null(old.InterviewStartedAt);
    }

    // Dòng có trước cột, không chắc chỉ một lượt (attempt_started_at NULL): trả null = "không biết" —
    // KHÔNG lấy mốc lần đầu thế vào (sau một lượt làm lại, đó là giờ của buổi KHÁC).
    [Fact]
    public async Task Results_LuotHienTai_MocLuotKhongBiet_TraNull_KhongMuonMocLanDau()
    {
        using var t = new CampaignTestDb();
        var orgId = Guid.NewGuid();
        var camp = SeedCampaign(t, orgId);
        var cand = Guid.NewGuid();
        var s2 = Guid.NewGuid();
        Member(t, camp.Id, cand, s2, InterviewProgressStatus.InProgress, startedAt: null,
            firstStartedAt: new DateTime(2026, 9, 13, 12, 54, 22, DateTimeKind.Utc));
        Flag(t, camp.Id, s2, cand);

        var res = await NewService(t.NewContext()).GetCampaignResultsAsync(orgId, camp.Id, default);

        var row = Assert.Single(res.UnscoredFlagged);
        Assert.True(row.IsLatestAttempt);
        Assert.Null(row.InterviewStartedAt);
    }

    [Fact]
    public async Task Results_BonField_TenKhoaJson_CamelCase()
    {
        var json = JsonSerializer.SerializeToElement(
            new UnscoredFlaggedRow { IsLatestAttempt = true, InterviewStatus = "InProgress" },
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal("InProgress", json.GetProperty("interviewStatus").GetString());
        Assert.True(json.GetProperty("isLatestAttempt").GetBoolean());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("abandonReason").ValueKind);
        Assert.Equal(JsonValueKind.Null, json.GetProperty("interviewStartedAt").ValueKind);
        await Task.CompletedTask;
    }

    // KHÔNG thêm query: bảng kết quả đọc campaign_membership đúng MỘT lần (query danh tính F5, nay mở
    // rộng projection) — trạng thái buổi không được kéo bằng query riêng theo từng dòng.
    private sealed class MembershipQueryCounter : DbCommandInterceptor
    {
        public int Count;
        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            if (command.CommandText.Contains("campaign_membership", StringComparison.Ordinal)) Count++;
            return result;
        }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("campaign_membership", StringComparison.Ordinal)) Count++;
            return ValueTask.FromResult(result);
        }
    }

    [Fact]
    public async Task Results_DocMembershipDungMotQuery()
    {
        using var t = new CampaignTestDb();
        var orgId = Guid.NewGuid();
        var camp = SeedCampaign(t, orgId);
        for (var i = 0; i < 3; i++)
        {
            var cand = Guid.NewGuid();
            var s = Guid.NewGuid();
            Member(t, camp.Id, cand, s, InterviewProgressStatus.InProgress);
            Flag(t, camp.Id, s, cand);
        }
        var counter = new MembershipQueryCounter();
        using var ctx = t.NewContext(counter);

        await NewService(ctx).GetCampaignResultsAsync(orgId, camp.Id, default);

        Assert.Equal(1, counter.Count);
    }

    // ── nhãn xuất file: MỘT helper, CSV và PDF ra cùng nhãn ─────────────────────────────────────

    [Theory]
    [InlineData(true, "Abandoned", "generation_failed", "Lỗi hệ thống — không tạo được câu hỏi")]
    [InlineData(true, "Abandoned", "no_scored_answer", "Bỏ ngang")]
    [InlineData(true, "Abandoned", null, "Bỏ ngang")]
    [InlineData(true, "InProgress", null, "Đang làm bài")]
    [InlineData(false, null, null, "Lượt trước")]
    [InlineData(true, "NotStarted", null, "Chưa chấm")]
    [InlineData(true, null, null, "Chưa chấm")]
    public void ExportResultLabel_TheoTrangThai(bool latest, string? status, string? reason, string expected)
    {
        var label = UnscoredFlaggedRow.ExportResultLabel(new UnscoredFlaggedRow
        {
            IsLatestAttempt = latest, InterviewStatus = status, AbandonReason = reason
        });
        Assert.Equal(expected, label);
    }

    private static (Guid orgId, Guid campaignId, Dictionary<Guid, string> expected) SeedLabelMatrix(CampaignTestDb t)
    {
        var orgId = Guid.NewGuid();
        var camp = SeedCampaign(t, orgId);
        var expected = new Dictionary<Guid, string>();

        void Add(InterviewProgressStatus? status, string? reason, string label, bool oldAttempt = false)
        {
            var cand = Guid.NewGuid();
            var flagged = Guid.NewGuid();
            Member(t, camp.Id, cand, oldAttempt ? Guid.NewGuid() : flagged, status, reason);
            Flag(t, camp.Id, flagged, cand);
            expected[flagged] = label;
        }

        Add(InterviewProgressStatus.Abandoned, "generation_failed", UnscoredFlaggedRow.LabelGenerationFailed);
        Add(InterviewProgressStatus.Abandoned, "expired_no_answer", UnscoredFlaggedRow.LabelAbandoned);
        Add(InterviewProgressStatus.InProgress, null, UnscoredFlaggedRow.LabelInProgress);
        Add(InterviewProgressStatus.InProgress, null, UnscoredFlaggedRow.LabelPreviousAttempt, oldAttempt: true);
        Add(null, null, UnscoredFlaggedRow.LabelNotScored);
        return (orgId, camp.Id, expected);
    }

    [Fact]
    public async Task Export_Csv_NhanTheoTrangThai()
    {
        using var t = new CampaignTestDb();
        var (orgId, campaignId, expected) = SeedLabelMatrix(t);

        var export = await NewService(t.NewContext()).ExportCampaignResultsAsync(orgId, campaignId, "csv", default);

        using var reader = new StringReader(System.Text.Encoding.UTF8.GetString(export.Content));
        using var csv = new CsvReader(reader, CultureInfo.InvariantCulture);
        csv.Read();
        csv.ReadHeader();
        var got = new Dictionary<Guid, string>();
        while (csv.Read())
            got[Guid.Parse(csv.GetField("session_id")!)] = csv.GetField("result") ?? string.Empty;
        Assert.Equal(expected.OrderBy(x => x.Key), got.OrderBy(x => x.Key));
    }

    [Fact]
    public async Task Export_Pdf_CungNhanVoiCsv()
    {
        using var t = new CampaignTestDb();
        var (orgId, campaignId, expected) = SeedLabelMatrix(t);

        var export = await NewService(t.NewContext()).ExportCampaignResultsAsync(orgId, campaignId, "pdf", default);

        var column = ResultColumnText(export.Content);
        foreach (var label in expected.Values.Distinct())
            Assert.Contains(label, column);
        // Ô "Kết quả" của 5 dòng chưa chấm chỉ chứa 5 nhãn trên — không lẫn nhãn nào khác.
        var expectedColumn = string.Join(" ", expected.Values.OrderBy(x => x, StringComparer.Ordinal));
        Assert.Equal(expectedColumn.Split(' ').OrderBy(x => x, StringComparer.Ordinal),
            column.Split(' ', StringSplitOptions.RemoveEmptyEntries).OrderBy(x => x, StringComparer.Ordinal));
    }

    // Chữ của RIÊNG cột "Kết quả" (giữa tiêu đề "Kết" và tiêu đề "Chấm" cùng hàng với "Hạng"), đọc từ
    // trên xuống, trái sang phải. Nhãn dài xuống dòng trong ô hẹp ⇒ đọc cả trang theo thứ tự dòng sẽ
    // xen chữ của ô bên cạnh vào giữa nhãn; khoanh theo cột thì phần xuống dòng vẫn liền nhau.
    private static string ResultColumnText(byte[] pdf)
    {
        using var doc = PdfDocument.Open(pdf);
        var words = doc.GetPages().SelectMany(p => p.GetWords()).ToList();
        var header = words.First(w => w.Text == "Hạng");
        bool SameRow(UglyToad.PdfPig.Content.Word w) => Math.Abs(w.BoundingBox.Bottom - header.BoundingBox.Bottom) < 1.0;
        var left = words.First(w => w.Text == "Kết" && SameRow(w)).BoundingBox.Left - 1.0;
        var right = words.First(w => w.Text == "Chấm" && SameRow(w)).BoundingBox.Left - 1.0;
        return string.Join(" ", words
            .Where(w => w.BoundingBox.Bottom < header.BoundingBox.Bottom - 1.0
                && w.BoundingBox.Left >= left && w.BoundingBox.Left < right)
            .OrderByDescending(w => Math.Round(w.BoundingBox.Bottom, 1))
            .ThenBy(w => w.BoundingBox.Left)
            .Select(w => w.Text));
    }
}
