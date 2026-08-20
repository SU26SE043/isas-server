using Isas.InterviewService.ApplicationDbContext;
using Isas.InterviewService.Entities;
using Isas.InterviewService.Enums;
using Isas.InterviewService.Models;
using Isas.InterviewService.Services;
using Microsoft.Extensions.Caching.Memory;
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
///
/// Nhóm sau (cửa sổ thời gian + cache) là PHÒNG XA cho hình dạng truy vấn, nhưng vẫn phải khoá
/// bằng test vì cả hai đều có thể làm SAI con số một cách âm thầm: cửa sổ cắt nhầm thì mẫu tụt
/// mà nhãn vẫn nói "n=…", còn cache thiếu một vế trong khoá thì người này nhìn mốc của người khác.
/// </summary>
public class CriterionBenchmarkF14Tests
{
    private const string Clarity = "Độ rõ ràng";
    private const string Tech = "Kỹ thuật";

    /// <param name="cache">
    /// Mặc định MỖI service một cache riêng ⇒ test không dính nhau. Test nào cần chứng minh ảnh chụp
    /// được DÙNG CHUNG (hoặc KHÔNG được lẫn giữa hai nghề/hai ngôn ngữ) thì truyền cùng một instance.
    /// </param>
    private static CriterionBenchmarkService Build(
        InterviewDbContext db, int minSample = 5, bool enabled = true, decimal passPct = 50m,
        int windowDays = 90, int ttlSeconds = 300, IMemoryCache? cache = null)
        => new(
            db,
            cache ?? new MemoryCache(new MemoryCacheOptions()),
            Options.Create(new BenchmarkOptions
            {
                Enabled = enabled,
                MinSampleSize = minSample,
                PeerWindowDays = windowDays,
                CacheTtlSeconds = ttlSeconds
            }),
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
        => AddScoredSession(db, candidateId, cat, null, "vi", criteria);

    /// <summary>Biến thể chốt thêm thời điểm tạo buổi (cửa sổ) + ngôn ngữ (khoá cache).</summary>
    private static PracticeSession AddScoredSession(
        InterviewDbContext db, Guid candidateId, JobCategory cat,
        DateTime? createdAt, string language,
        params (string Name, decimal Pct)[] criteria)
    {
        var s = TestDb.Session(candidateId, SessionStatus.Scored, cat, createdAt: createdAt, language: language);
        db.PracticeSessions.Add(s);
        foreach (var (name, pct) in criteria)
        {
            var crit = TestDb.Criterion(cat, name: name, candidateId: candidateId, language: language);
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

    // ── Cửa sổ thời gian ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CuaSoThoiGian_BuoiNgoaiCuaSoKhongVaoMau_KhongVaoTrungBinhLanVaoN()
    {
        // Cửa sổ là trần chi phí (mẫu nạp hết vào RAM), nhưng nó ĐỔI CẢ CON SỐ nên phải khoá: buổi
        // ngoài cửa sổ không được góp vào trung bình, và cũng không được góp vào `n` trên nhãn — `n`
        // nói "bao nhiêu người luyện", nói dư là nói dối về độ dày của mẫu.
        using var t = new TestDb();
        var me = Guid.NewGuid();
        var mine = AddScoredSession(t.Db, me, JobCategory.BE, (Clarity, 30m));
        var old = DateTime.UtcNow.AddDays(-200);
        foreach (var _ in Enumerable.Range(0, 5))
            AddScoredSession(t.Db, Guid.NewGuid(), JobCategory.BE, old, "vi", (Clarity, 90m));
        foreach (var _ in Enumerable.Range(0, 5))
            AddScoredSession(t.Db, Guid.NewGuid(), JobCategory.BE, DateTime.UtcNow, "vi", (Clarity, 60m));
        await t.Db.SaveChangesAsync();

        var result = await Build(t.Db, minSample: 5, windowDays: 90).BuildAsync(mine, BreakdownOf(t.Db, mine.Id));

        Assert.Equal("PeerAverage", result!.Source);
        Assert.Equal(60m, Assert.Single(result.Criteria).TargetPercentage);   // 90 KHÔNG được trộn vào
        Assert.Equal(5, result.SampleSize);                                   // 10 buổi tồn tại, chỉ 5 trong cửa sổ
    }

    [Fact]
    public async Task CuaSoThoiGian_ChiCoBuoiCu_TutXuongDuoiNguongMau_RoiVeNguongNoiBo()
    {
        using var t = new TestDb();
        var me = Guid.NewGuid();
        var mine = AddScoredSession(t.Db, me, JobCategory.BE, (Clarity, 30m));
        foreach (var _ in Enumerable.Range(0, 20))
            AddScoredSession(t.Db, Guid.NewGuid(), JobCategory.BE, DateTime.UtcNow.AddDays(-120), "vi", (Clarity, 90m));
        await t.Db.SaveChangesAsync();

        var result = await Build(t.Db, minSample: 5, windowDays: 90).BuildAsync(mine, BreakdownOf(t.Db, mine.Id));

        // 20 buổi nhưng đều quá cũ ⇒ n=0 ⇒ nói thẳng là ngưỡng nội bộ, KHÔNG dựng mốc từ dữ liệu cũ.
        Assert.Equal("PassThreshold", result!.Source);
        Assert.Equal(0, result.SampleSize);
    }

    [Fact]
    public async Task CuaSoThoiGian_KhongDuong_TatCuaSo_GiuHanhViCu_LayToanBoLichSu()
    {
        // Kill-switch: PeerWindowDays <= 0 phải trả lại đúng hành vi trước khi có cửa sổ (toàn bộ lịch
        // sử) — để nếu cửa sổ gây tranh cãi về sản phẩm thì tắt được bằng cấu hình, không cần deploy.
        using var t = new TestDb();
        var me = Guid.NewGuid();
        var mine = AddScoredSession(t.Db, me, JobCategory.BE, (Clarity, 30m));
        foreach (var _ in Enumerable.Range(0, 5))
            AddScoredSession(t.Db, Guid.NewGuid(), JobCategory.BE, DateTime.UtcNow.AddDays(-200), "vi", (Clarity, 90m));
        foreach (var _ in Enumerable.Range(0, 5))
            AddScoredSession(t.Db, Guid.NewGuid(), JobCategory.BE, DateTime.UtcNow, "vi", (Clarity, 60m));
        await t.Db.SaveChangesAsync();

        var result = await Build(t.Db, minSample: 5, windowDays: 0).BuildAsync(mine, BreakdownOf(t.Db, mine.Id));

        Assert.Equal("PeerAverage", result!.Source);
        Assert.Equal(75m, Assert.Single(result.Criteria).TargetPercentage);   // (90×5 + 60×5) / 10
        Assert.Equal(10, result.SampleSize);
    }

    [Fact]
    public async Task CuaSoThoiGian_VanLoaiChinhMinh_DuBuoiCuaMinhNamTrongCuaSo()
    {
        // Cửa sổ và "loại chính mình" là hai bộ lọc ĐỘC LẬP — thêm cửa sổ không được làm mất bộ lọc kia.
        using var t = new TestDb();
        var me = Guid.NewGuid();
        var mine = AddScoredSession(t.Db, me, JobCategory.BE, (Clarity, 30m));
        for (var i = 0; i < 9; i++)
            AddScoredSession(t.Db, me, JobCategory.BE, DateTime.UtcNow.AddDays(-1), "vi", (Clarity, 30m));
        await t.Db.SaveChangesAsync();

        var result = await Build(t.Db, minSample: 5, windowDays: 90).BuildAsync(mine, BreakdownOf(t.Db, mine.Id));

        Assert.Equal("PassThreshold", result!.Source);
        Assert.Equal(0, result.SampleSize);
    }

    // ── Cache ảnh chụp cộng đồng ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Cache_KhongTraNhamGiuaHaiNghe()
    {
        // Khoá cache thiếu job_category ⇒ ứng viên BE nhìn thấy mốc của FE. Sai kiểu này KHÔNG có
        // triệu chứng nào trên UI: vẫn đúng một đường đứt nét, vẫn đúng một nhãn "n=…".
        using var t = new TestDb();
        using var shared = new MemoryCache(new MemoryCacheOptions());
        var beMine = AddScoredSession(t.Db, Guid.NewGuid(), JobCategory.BE, (Clarity, 30m));
        var feMine = AddScoredSession(t.Db, Guid.NewGuid(), JobCategory.FE, (Clarity, 30m));
        foreach (var _ in Enumerable.Range(0, 5))
        {
            AddScoredSession(t.Db, Guid.NewGuid(), JobCategory.BE, (Clarity, 60m));
            AddScoredSession(t.Db, Guid.NewGuid(), JobCategory.FE, (Clarity, 90m));
        }
        await t.Db.SaveChangesAsync();

        var be = await Build(t.Db, minSample: 5, cache: shared).BuildAsync(beMine, BreakdownOf(t.Db, beMine.Id));
        var fe = await Build(t.Db, minSample: 5, cache: shared).BuildAsync(feMine, BreakdownOf(t.Db, feMine.Id));

        Assert.Equal(60m, Assert.Single(be!.Criteria).TargetPercentage);
        Assert.Equal(90m, Assert.Single(fe!.Criteria).TargetPercentage);
    }

    [Fact]
    public async Task Cache_KhongTraNhamGiuaHaiNgonNgu()
    {
        // Trên thực tế rubric vi/en đặt tên tiêu chí khác nhau nên hai ngôn ngữ đã tự tách. Test này
        // dựng ĐÚNG ca hiếm mà sự tách đó không còn: cùng một TÊN tiêu chí ở hai ngôn ngữ (rubric riêng
        // BC16 hoàn toàn có thể như vậy). Nếu ngôn ngữ chỉ tách nhờ cách đặt tên chứ không nằm trong
        // vị từ + khoá cache, đây là lúc mốc lẳng lặng trộn hai bộ tiêu chí khác nhau.
        using var t = new TestDb();
        using var shared = new MemoryCache(new MemoryCacheOptions());
        var viMine = AddScoredSession(t.Db, Guid.NewGuid(), JobCategory.BE, DateTime.UtcNow, "vi", (Clarity, 30m));
        var enMine = AddScoredSession(t.Db, Guid.NewGuid(), JobCategory.BE, DateTime.UtcNow, "en", (Clarity, 30m));
        foreach (var _ in Enumerable.Range(0, 5))
        {
            AddScoredSession(t.Db, Guid.NewGuid(), JobCategory.BE, DateTime.UtcNow, "vi", (Clarity, 60m));
            AddScoredSession(t.Db, Guid.NewGuid(), JobCategory.BE, DateTime.UtcNow, "en", (Clarity, 90m));
        }
        await t.Db.SaveChangesAsync();

        var vi = await Build(t.Db, minSample: 5, cache: shared).BuildAsync(viMine, BreakdownOf(t.Db, viMine.Id));
        var en = await Build(t.Db, minSample: 5, cache: shared).BuildAsync(enMine, BreakdownOf(t.Db, enMine.Id));

        Assert.Equal(60m, Assert.Single(vi!.Criteria).TargetPercentage);
        Assert.Equal(5, vi.SampleSize);
        Assert.Equal(90m, Assert.Single(en!.Criteria).TargetPercentage);
        Assert.Equal(5, en.SampleSize);
    }

    [Fact]
    public async Task Cache_DungChungAnhChup_MoiNguoiVanBiLoaiKhoiMauCuaChinhHo()
    {
        // Đây là bất biến mà cache dễ phá nhất: ảnh chụp được chia sẻ giữa MỌI người cùng nghề (khoá
        // KHÔNG có candidate_id — cố ý, xem docstring service), nên "loại chính mình" phải được làm
        // bằng phép TRỪ sau khi đọc cache. Hỏng vế trừ thì mốc của A vẫn có A trong đó — vòng tròn,
        // và nhìn trên UI thì không phân biệt được với mốc đúng.
        using var t = new TestDb();
        using var shared = new MemoryCache(new MemoryCacheOptions());
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var aSession = AddScoredSession(t.Db, a, JobCategory.BE, (Clarity, 0m));
        var bSession = AddScoredSession(t.Db, b, JobCategory.BE, (Clarity, 100m));
        foreach (var _ in Enumerable.Range(0, 5))
            AddScoredSession(t.Db, Guid.NewGuid(), JobCategory.BE, (Clarity, 50m));
        await t.Db.SaveChangesAsync();

        // Lượt đầu nạp + chụp; lượt sau đọc lại ĐÚNG ảnh chụp đó nhưng trừ phần của người xem khác.
        var forA = await Build(t.Db, minSample: 5, cache: shared).BuildAsync(aSession, BreakdownOf(t.Db, aSession.Id));
        var forB = await Build(t.Db, minSample: 5, cache: shared).BuildAsync(bSession, BreakdownOf(t.Db, bSession.Id));

        // A không thấy 0 của mình: (100 + 50×5) / 6 = 58.33 — B không thấy 100 của mình: (0 + 50×5) / 6 = 41.67
        Assert.Equal(58.33m, Assert.Single(forA!.Criteria).TargetPercentage);
        Assert.Equal(41.67m, Assert.Single(forB!.Criteria).TargetPercentage);
        Assert.Equal(6, forA.SampleSize);
        Assert.Equal(6, forB.SampleSize);
    }

    [Fact]
    public async Task Cache_TrongTTL_KhongQuetLaiDb_TTL0_ThiQuetLai()
    {
        // Không đo được "có gọi DB không" từ ngoài, nên đo bằng hệ quả: thêm buổi của NGƯỜI KHÁC rồi
        // hỏi lại. Còn TTL ⇒ vẫn là ảnh chụp cũ (bằng chứng cache thật sự chặn được lượt quét thứ hai);
        // TTL=0 ⇒ thấy ngay dữ liệu mới (kill-switch còn dùng được khi cần điều tra).
        using var t = new TestDb();
        using var shared = new MemoryCache(new MemoryCacheOptions());
        var me = Guid.NewGuid();
        var mine = AddScoredSession(t.Db, me, JobCategory.BE, (Clarity, 30m));
        foreach (var _ in Enumerable.Range(0, 5))
            AddScoredSession(t.Db, Guid.NewGuid(), JobCategory.BE, (Clarity, 60m));
        await t.Db.SaveChangesAsync();

        var first = await Build(t.Db, minSample: 5, cache: shared).BuildAsync(mine, BreakdownOf(t.Db, mine.Id));
        Assert.Equal(60m, Assert.Single(first!.Criteria).TargetPercentage);

        foreach (var _ in Enumerable.Range(0, 5))
            AddScoredSession(t.Db, Guid.NewGuid(), JobCategory.BE, (Clarity, 100m));
        await t.Db.SaveChangesAsync();

        var cached = await Build(t.Db, minSample: 5, cache: shared).BuildAsync(mine, BreakdownOf(t.Db, mine.Id));
        Assert.Equal(60m, Assert.Single(cached!.Criteria).TargetPercentage);
        Assert.Equal(5, cached.SampleSize);

        var fresh = await Build(t.Db, minSample: 5, ttlSeconds: 0, cache: shared)
            .BuildAsync(mine, BreakdownOf(t.Db, mine.Id));
        Assert.Equal(80m, Assert.Single(fresh!.Criteria).TargetPercentage);   // (60×5 + 100×5) / 10
        Assert.Equal(10, fresh.SampleSize);
    }

    [Fact]
    public async Task Cache_BoTenTieuChiKhacNhau_KhongDungChungAnhChup()
    {
        // BC16 cho mỗi ứng viên một bộ tiêu chí riêng ⇒ bộ TÊN là một phần vị từ của truy vấn, phải
        // nằm trong khoá. Thiếu nó thì người có rubric 1 trục đọc nhầm ảnh chụp của rubric 2 trục.
        using var t = new TestDb();
        using var shared = new MemoryCache(new MemoryCacheOptions());
        // Cùng một người xem cho cả hai buổi ⇒ khác biệt duy nhất giữa hai lượt là BỘ TÊN tiêu chí.
        var me = Guid.NewGuid();
        var oneAxis = AddScoredSession(t.Db, me, JobCategory.BE, (Clarity, 30m));
        var twoAxis = AddScoredSession(t.Db, me, JobCategory.BE, (Clarity, 30m), (Tech, 30m));
        foreach (var _ in Enumerable.Range(0, 5))
            AddScoredSession(t.Db, Guid.NewGuid(), JobCategory.BE, (Clarity, 60m), (Tech, 80m));
        await t.Db.SaveChangesAsync();

        var one = await Build(t.Db, minSample: 5, cache: shared).BuildAsync(oneAxis, BreakdownOf(t.Db, oneAxis.Id));
        var two = await Build(t.Db, minSample: 5, cache: shared).BuildAsync(twoAxis, BreakdownOf(t.Db, twoAxis.Id));

        Assert.Equal(60m, Assert.Single(one!.Criteria).TargetPercentage);
        Assert.Equal(60m, two!.Criteria.Single(c => c.Name == Clarity).TargetPercentage);
        Assert.Equal(80m, two.Criteria.Single(c => c.Name == Tech).TargetPercentage);
    }
}
