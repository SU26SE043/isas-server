using Isas.InterviewService.ApplicationDbContext;
using Isas.InterviewService.Entities;
using Isas.InterviewService.Enums;
using Isas.InterviewService.Models;
using Isas.InterviewService.Services;
using Microsoft.Extensions.Options;

namespace Isas.InterviewService.Tests;

/// <summary>
/// F14 (FR08) — mốc đối chiếu vẽ thành lớp thứ hai trên radar kết quả buổi luyện.
///
/// ⚠ Thứ đáng khoá bằng test ở đây KHÔNG phải phép tính trung bình (dễ), mà là các quyết định
/// về TÍNH TRUNG THỰC của con số — vì chúng vô hình khi nhìn UI và rất dễ bị "dọn cho gọn":
///
///   • Hệ thống KHÔNG có dữ liệu chuẩn ngành ⇒ nhãn KHÔNG được nói "chuẩn ngành".
///   • Loại chính mình khỏi mẫu ⇒ ca "hệ thống có đúng 1 người dùng" không đẻ ra một mốc trùng
///     khít điểm của họ (vô nghĩa nhưng nhìn rất thuyết phục).
///   • Gom theo TÊN tiêu chí ⇒ người dùng rubric riêng (BC16, id khác nhau) vẫn có mẫu.
///   • Thiếu mẫu ⇒ rơi về ngưỡng nội bộ và NÓI RÕ nó là ngưỡng nội bộ.
/// </summary>
public class CriterionBenchmarkF14Tests
{
    private const string Clarity = "Độ rõ ràng";
    private const string Tech = "Kỹ thuật";

    private static CriterionBenchmarkService Build(
        InterviewDbContext db, int minSample = 5, bool enabled = true, decimal passPct = 50m)
        => new(
            db,
            Options.Create(new BenchmarkOptions { Enabled = enabled, MinSampleSize = minSample }),
            Options.Create(new ScoringOptions { ImprovementThresholdPct = passPct }));

    /// <summary>
    /// Thêm 1 buổi B2C đã Scored kèm breakdown cho các tiêu chí (tên → %).
    ///
    /// Mỗi buổi mint hàng <c>rubric_criteria</c> RIÊNG (candidate_id = chủ buổi) — vừa thoả FK
    /// <c>session_criterion_scores.criterion_id</c> (SQLite CÓ enforce FK từ EF10), vừa tái hiện
    /// đúng thực tế BC16: cùng một tên tiêu chí nhưng id khác nhau giữa các candidate.
    /// </summary>
    private static PracticeSession AddScoredSession(
        InterviewDbContext db, Guid candidateId, JobCategory cat,
        params (string Name, decimal Pct)[] criteria)
    {
        var s = TestDb.Session(candidateId, SessionStatus.Scored, cat);
        db.PracticeSessions.Add(s);
        foreach (var (name, pct) in criteria)
        {
            var crit = TestDb.Criterion(cat, name: name, candidateId: candidateId);
            db.RubricCriteria.Add(crit);
            db.SessionCriterionScores.Add(new SessionCriterionScore
            {
                Id = Guid.NewGuid(),
                SessionId = s.Id,
                CriterionId = crit.Id,
                CriterionName = name,
                AverageScore = pct / 20m,
                MaxScore = 5,
                Percentage = pct,
                Weight = 1m,
                NeedsImprovement = pct < 50m,
                CreatedAt = DateTime.UtcNow
            });
        }
        return s;
    }

    private static List<SessionCriterionScore> BreakdownOf(InterviewDbContext db, Guid sessionId)
        => db.SessionCriterionScores.Where(x => x.SessionId == sessionId).ToList();

    [Fact]
    public async Task DuMau_DungTrungBinhNguoiKhac_NhanNoiRoLaTrungBinhNguoiLuyen()
    {
        using var t = new TestDb();
        var me = Guid.NewGuid();
        var mine = AddScoredSession(t.Db, me, JobCategory.BE, (Clarity, 30m));
        // 5 người KHÁC, mỗi người 1 buổi: 40/50/60/70/80 → trung bình 60.
        foreach (var pct in new[] { 40m, 50m, 60m, 70m, 80m })
            AddScoredSession(t.Db, Guid.NewGuid(), JobCategory.BE, (Clarity, pct));
        await t.Db.SaveChangesAsync();

        var result = await Build(t.Db, minSample: 5).BuildAsync(mine, BreakdownOf(t.Db, mine.Id));

        Assert.NotNull(result);
        Assert.Equal("PeerAverage", result!.Source);
        Assert.Equal(5, result.SampleSize);
        Assert.Equal(60m, Assert.Single(result.Criteria).TargetPercentage);
        Assert.Contains("Trung bình người luyện cùng vị trí", result.Label);
        Assert.Contains("n=5", result.Label);
    }

    [Fact]
    public async Task KhongDuMau_RoiVeNguongNoiBo_NhanNoiDungLaNguongNoiBo()
    {
        using var t = new TestDb();
        var me = Guid.NewGuid();
        var mine = AddScoredSession(t.Db, me, JobCategory.BE, (Clarity, 30m));
        AddScoredSession(t.Db, Guid.NewGuid(), JobCategory.BE, (Clarity, 90m));   // chỉ 1 người khác
        await t.Db.SaveChangesAsync();

        var result = await Build(t.Db, minSample: 5, passPct: 50m)
            .BuildAsync(mine, BreakdownOf(t.Db, mine.Id));

        Assert.NotNull(result);
        Assert.Equal("PassThreshold", result!.Source);
        Assert.Equal(0, result.SampleSize);
        Assert.Equal(50m, Assert.Single(result.Criteria).TargetPercentage);
        Assert.Contains("Ngưỡng đạt nội bộ", result.Label);
    }

    [Fact]
    public async Task NhanKhongBaoGioNoiLaChuanNganh()
    {
        // Hệ thống không có dữ liệu chuẩn ngành nào. Gắn nhãn đó lên trung bình nội bộ / ngưỡng
        // nội bộ là nói dối người dùng về độ tin cậy của đường kẻ họ đang nhìn.
        using var t = new TestDb();
        var me = Guid.NewGuid();
        var mine = AddScoredSession(t.Db, me, JobCategory.BE, (Clarity, 30m));
        foreach (var pct in new[] { 40m, 50m, 60m, 70m, 80m })
            AddScoredSession(t.Db, Guid.NewGuid(), JobCategory.BE, (Clarity, pct));
        await t.Db.SaveChangesAsync();
        var svc = Build(t.Db, minSample: 5);

        var peer = await svc.BuildAsync(mine, BreakdownOf(t.Db, mine.Id));
        var fallback = await Build(t.Db, minSample: 99).BuildAsync(mine, BreakdownOf(t.Db, mine.Id));

        foreach (var label in new[] { peer!.Label, fallback!.Label })
        {
            Assert.DoesNotContain("chuẩn ngành", label, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("industry", label, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task LoaiChinhMinhKhoiMau_MotNguoiDungDuyNhat_KhongTaoMocTrungKhitDiemMinh()
    {
        // Ca thoái hoá nguy hiểm nhất: chỉ có 1 người dùng, mà lại có nhiều buổi. Nếu tính cả
        // bản thân, mốc sẽ bám sát chính điểm của họ → biểu đồ nhìn "có đối chiếu" mà không đối
        // chiếu với ai cả. Loại bản thân ⇒ n=0 ⇒ rơi về ngưỡng nội bộ (trung thực).
        using var t = new TestDb();
        var me = Guid.NewGuid();
        var mine = AddScoredSession(t.Db, me, JobCategory.BE, (Clarity, 30m));
        for (var i = 0; i < 9; i++)
            AddScoredSession(t.Db, me, JobCategory.BE, (Clarity, 30m));   // vẫn CHÍNH mình
        await t.Db.SaveChangesAsync();

        var result = await Build(t.Db, minSample: 5).BuildAsync(mine, BreakdownOf(t.Db, mine.Id));

        Assert.Equal("PassThreshold", result!.Source);
        Assert.Equal(0, result.SampleSize);
    }

    [Fact]
    public async Task GomTheoTenTieuChi_RubricRiengBC16_VanCoMau()
    {
        // BC16: mỗi candidate có hàng rubric_criteria RIÊNG → cùng "Độ rõ ràng" nhưng khác id.
        // Gom theo id thì nhóm dùng rubric riêng vĩnh viễn n=0 — tính năng chết im lặng.
        using var t = new TestDb();
        var me = Guid.NewGuid();
        var mine = AddScoredSession(t.Db, me, JobCategory.BE, (Clarity, 30m));
        foreach (var pct in new[] { 60m, 60m, 60m, 60m, 60m })
            AddScoredSession(t.Db, Guid.NewGuid(), JobCategory.BE, (Clarity, pct));
        await t.Db.SaveChangesAsync();

        var result = await Build(t.Db, minSample: 5).BuildAsync(mine, BreakdownOf(t.Db, mine.Id));

        // Mọi criterionId trong DB đều khác nhau; chỉ TÊN là trùng.
        Assert.Equal("PeerAverage", result!.Source);
        Assert.Equal(60m, Assert.Single(result.Criteria).TargetPercentage);
    }

    [Fact]
    public async Task ChiLayNguoiCungViTri()
    {
        using var t = new TestDb();
        var me = Guid.NewGuid();
        var mine = AddScoredSession(t.Db, me, JobCategory.BE, (Clarity, 30m));
        foreach (var pct in new[] { 90m, 90m, 90m, 90m, 90m })
            AddScoredSession(t.Db, Guid.NewGuid(), JobCategory.FE, (Clarity, pct));   // vị trí KHÁC
        await t.Db.SaveChangesAsync();

        var result = await Build(t.Db, minSample: 5).BuildAsync(mine, BreakdownOf(t.Db, mine.Id));

        Assert.Equal("PassThreshold", result!.Source);   // mẫu FE không được tính cho buổi BE
    }

    [Fact]
    public async Task BuoiB2BKhongVaoMau()
    {
        // Điểm B2B chấm theo tiêu chí campaign (thang/ngữ cảnh khác hẳn) → trộn vào mốc B2C là
        // so hai thứ không cùng đơn vị.
        using var t = new TestDb();
        var me = Guid.NewGuid();
        var mine = AddScoredSession(t.Db, me, JobCategory.BE, (Clarity, 30m));
        for (var i = 0; i < 5; i++)
        {
            var campaignId = Guid.NewGuid();
            var s = TestDb.Session(Guid.NewGuid(), SessionStatus.Scored, JobCategory.BE,
                campaignId: campaignId);
            var crit = TestDb.Criterion(JobCategory.BE, name: Clarity, campaignId: campaignId);
            t.Db.PracticeSessions.Add(s);
            t.Db.RubricCriteria.Add(crit);
            t.Db.SessionCriterionScores.Add(new SessionCriterionScore
            {
                Id = Guid.NewGuid(), SessionId = s.Id, CriterionId = crit.Id,
                CriterionName = Clarity, AverageScore = 4.5m, MaxScore = 5, Percentage = 90m,
                Weight = 1m, CreatedAt = DateTime.UtcNow
            });
        }
        await t.Db.SaveChangesAsync();

        var result = await Build(t.Db, minSample: 5).BuildAsync(mine, BreakdownOf(t.Db, mine.Id));

        Assert.Equal("PassThreshold", result!.Source);
    }

    [Fact]
    public async Task MotTieuChiThieuMau_CaBieuDoRoiVeNguongNoiBo_KhongTronNguon()
    {
        // Trộn nguồn giữa các trục thì đường đứt nét không còn nghĩa thống nhất và không chú
        // thích trung thực được bằng MỘT nhãn.
        using var t = new TestDb();
        var me = Guid.NewGuid();
        var mine = AddScoredSession(t.Db, me, JobCategory.BE, (Clarity, 30m), (Tech, 40m));
        for (var i = 0; i < 5; i++)
            AddScoredSession(t.Db, Guid.NewGuid(), JobCategory.BE, (Clarity, 60m));   // thiếu Tech
        await t.Db.SaveChangesAsync();

        var result = await Build(t.Db, minSample: 5).BuildAsync(mine, BreakdownOf(t.Db, mine.Id));

        Assert.Equal("PassThreshold", result!.Source);
        Assert.Equal(2, result.Criteria.Count);
        Assert.All(result.Criteria, c => Assert.Equal(50m, c.TargetPercentage));
    }

    [Fact]
    public async Task Tat_TraNull()
    {
        using var t = new TestDb();
        var mine = AddScoredSession(t.Db, Guid.NewGuid(), JobCategory.BE, (Clarity, 30m));
        await t.Db.SaveChangesAsync();

        Assert.Null(await Build(t.Db, enabled: false).BuildAsync(mine, BreakdownOf(t.Db, mine.Id)));
    }

    [Fact]
    public async Task KhongCoBreakdown_TraNull()
    {
        using var t = new TestDb();
        var mine = TestDb.Session(Guid.NewGuid(), SessionStatus.Scored);
        t.Db.PracticeSessions.Add(mine);
        await t.Db.SaveChangesAsync();

        Assert.Null(await Build(t.Db).BuildAsync(mine, new List<SessionCriterionScore>()));
    }

    [Fact]
    public async Task MocLuonNamTrong0_100_DeVeChungTrucVoiPhanTramCuaUser()
    {
        using var t = new TestDb();
        var me = Guid.NewGuid();
        var mine = AddScoredSession(t.Db, me, JobCategory.BE, (Clarity, 30m));
        foreach (var pct in new[] { 100m, 100m, 100m, 100m, 100m })
            AddScoredSession(t.Db, Guid.NewGuid(), JobCategory.BE, (Clarity, pct));
        await t.Db.SaveChangesAsync();

        var result = await Build(t.Db, minSample: 5).BuildAsync(mine, BreakdownOf(t.Db, mine.Id));

        Assert.All(result!.Criteria, c => Assert.InRange(c.TargetPercentage, 0m, 100m));
    }
}
