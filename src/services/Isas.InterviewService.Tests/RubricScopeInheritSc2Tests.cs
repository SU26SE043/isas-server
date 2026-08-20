using Isas.InterviewService.ApplicationDbContext;
using Isas.InterviewService.DTOs;
using Isas.InterviewService.Entities;
using Isas.InterviewService.Enums;
using Isas.InterviewService.Models;
using Isas.InterviewService.Services;
using Isas.InterviewService.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace Isas.InterviewService.Tests;

/// <summary>
/// SC2 (vế BC16) — rubric RIÊNG của ứng viên phải KẾ THỪA <see cref="ScoringScope"/> từ tiêu chí BỘ
/// CHUẨN cùng <c>(nghề, ngôn ngữ, tên)</c>.
///
/// <para><b>Lỗi được khoá lại ở đây</b> (đã truy trọn chuỗi trên production, không phải phỏng đoán):
/// <c>RubricLibraryService.ReplaceAsync</c> không set <c>ScoringScope</c> ⇒ mọi tiêu chí riêng nhận
/// default <c>Always</c> (rubric riêng BA/vi 7/7 <c>Always</c>, BE/vi 9/9 <c>Always</c>, 0 dòng
/// <c>WhenTargeted</c>) ⇒ <c>LoadTargetableCriteriaAsync</c> trả rỗng ⇒ nhánh
/// <c>if (targetable.Count &gt; 0)</c> trượt ⇒ gọi overload sinh câu hỏi KHÔNG kèm criteria ⇒
/// <c>target_criterion_ids = null</c> ⇒ mọi câu bị chấm trên TOÀN BỘ rubric. Đo: <b>400/593 câu
/// (67%)</b> trắng nhãn, <b>37/96 buổi (39%)</b> hỏng trọn vẹn, <b>0 buổi nửa nọ nửa kia</b> (đúng
/// chữ ký của một thuộc tính rubric cố định suốt buổi), và chỉ đúng hai (nghề, ngôn ngữ) CÓ rubric
/// riêng bị dính — FE/vi và BA/en 100%.</para>
///
/// <para><b>Vì sao có test (2) đi qua <c>PracticeService</c> chứ không chỉ đọc cột:</b> đọc thẳng
/// <c>rubric_criteria.scoring_scope</c> chỉ chứng minh cột được ghi. Thứ thật sự hỏng là mắt xích
/// KẾ TIẾP — truy vấn lọc <c>WhenTargeted</c> ở đường tạo session. Không chạy qua nó thì bộ test
/// vẫn xanh ngay cả khi hai đầu lệch nhau (vd lệch ngôn ngữ/nghề).</para>
/// </summary>
public class RubricScopeInheritSc2Tests
{
    // 4 CÁCH NÓI + 3 NỘI DUNG — đúng hình dạng bộ chuẩn thật (xem B2CRubricSeed / ScoringScopeTests).
    private static readonly (string Name, ScoringScope Scope)[] SeedShape =
    [
        ("Độ trôi chảy & tự tin",              ScoringScope.Always),
        ("Giao tiếp & trình bày",              ScoringScope.Always),
        ("Ngữ pháp & dùng từ",                 ScoringScope.Always),
        ("Thuật ngữ chuyên ngành",             ScoringScope.Always),
        ("Hiểu nghiệp vụ & các bên liên quan", ScoringScope.WhenTargeted),
        ("Phân tích yêu cầu",                  ScoringScope.WhenTargeted),
        ("Tư duy giải quyết vấn đề",           ScoringScope.WhenTargeted),
    ];

    private static RubricLibraryService Svc(InterviewDbContext db, WarningRecorder? log = null)
        => new(db, null, log);

    /// Bộ chuẩn (candidate_id NULL, campaign_id NULL) cho 1 (nghề, ngôn ngữ).
    private static async Task SeedStandardAsync(
        InterviewDbContext db, JobCategory cat, string language = "vi",
        (string Name, ScoringScope Scope)[]? shape = null, bool active = true)
    {
        foreach (var (name, scope) in shape ?? SeedShape)
        {
            var row = TestDb.Criterion(cat, name: name, language: language, active: active);
            row.Weight = Math.Round(1m / (shape ?? SeedShape).Length, 4);
            row.ScoringScope = scope;
            db.RubricCriteria.Add(row);
        }
        await db.SaveChangesAsync();
    }

    /// Payload "ứng viên chép bộ chuẩn rồi chỉnh trọng số/thang điểm" — đúng thứ prod đang có.
    private static UpsertRubricRequest CopyOfSeed(params string[] names)
    {
        var weight = Math.Round(1m / names.Length, 4);
        return new UpsertRubricRequest(
            names.Select(n => new RubricCriterionInput(n, "mô tả", weight, 5)).ToList());
    }

    // ── (1) Kế thừa đúng scope từ bộ chuẩn cùng tên ──────────────────────────────────────────

    [Fact]
    public async Task Replace_TenTrungBoChuan_KeThuaDungScope()
    {
        using var t = new TestDb();
        await SeedStandardAsync(t.Db, JobCategory.BA);
        var me = Guid.NewGuid();

        await Svc(t.Db).ReplaceAsync(me, JobCategory.BA, CopyOfSeed(SeedShape.Select(s => s.Name).ToArray()));

        var rows = await t.Db.RubricCriteria.AsNoTracking()
            .Where(c => c.CandidateId == me && c.IsActive).ToListAsync();

        Assert.Equal(7, rows.Count);
        // Không assert "4 và 3" bằng con số trần: so TỪNG tên với bộ chuẩn, để test còn đúng khi seed đổi.
        foreach (var (name, scope) in SeedShape)
            Assert.Equal(scope, rows.Single(r => r.Name == name).ScoringScope);
    }

    // ── (2) Mắt xích kế tiếp: đường tạo session nay THẤY tiêu chí nội dung ───────────────────

    /// Đây là test có ý nghĩa nghiệp vụ thật: <c>ContentCriteriaCount</c> đi thẳng từ
    /// <c>LoadTargetableCriteriaAsync</c> — chính truy vấn quyết định câu hỏi có được gắn nhãn hay
    /// không. Trước bản vá con số này là <b>0</b> cho mọi ứng viên có rubric riêng.
    [Fact]
    public async Task SauKhiLuuRubricRieng_DuongTaoSession_ThayDuTieuChiNoiDung()
    {
        using var t = new TestDb();
        await SeedStandardAsync(t.Db, JobCategory.BA);
        var me = Guid.NewGuid();

        var before = await Practice(t).GetSessionOptionsAsync(me, "BA");
        Assert.Equal(3, before.ContentCriteriaCount);       // đang dùng bộ chuẩn → vốn đã đúng

        await Svc(t.Db).ReplaceAsync(me, JobCategory.BA, CopyOfSeed(SeedShape.Select(s => s.Name).ToArray()));

        var after = await Practice(t).GetSessionOptionsAsync(me, "BA");
        Assert.Equal(3, after.ContentCriteriaCount);        // rubric riêng KHÔNG được làm mất chúng
    }

    // ── (3) Tên lạ → giữ Always, nhưng PHẢI thấy được trong log ──────────────────────────────

    [Fact]
    public async Task Replace_TenKhongKhopBoChuan_GiuAlways_VaCanhBao()
    {
        using var t = new TestDb();
        await SeedStandardAsync(t.Db, JobCategory.BA);
        var me = Guid.NewGuid();
        var log = new WarningRecorder();

        await Svc(t.Db, log).ReplaceAsync(
            me, JobCategory.BA, CopyOfSeed("Phân tích yêu cầu", "Kinh nghiệm domain fintech"));

        var rows = await t.Db.RubricCriteria.AsNoTracking()
            .Where(c => c.CandidateId == me && c.IsActive).ToListAsync();

        Assert.Equal(ScoringScope.WhenTargeted, rows.Single(r => r.Name == "Phân tích yêu cầu").ScoringScope);
        // Lùi an toàn: vẫn được chấm (chấm thừa), KHÔNG phải bỏ chấm.
        Assert.Equal(ScoringScope.Always, rows.Single(r => r.Name == "Kinh nghiệm domain fintech").ScoringScope);

        // Không assert log thì gỡ sạch cảnh báo đi test vẫn xanh — mà cảnh báo là thứ DUY NHẤT cho
        // thấy một tiêu chí NỘI DUNG tự đặt tên đang bị chấm cho mọi câu (sai im lặng).
        var warning = Assert.Single(log.Warnings);
        Assert.Contains("SC2", warning);
        Assert.Contains("Kinh nghiệm domain fintech", warning);
        Assert.DoesNotContain("Phân tích yêu cầu", warning);
    }

    [Fact]
    public async Task Replace_MoiTenDeuKhopBoChuan_KhongCanhBao()
    {
        using var t = new TestDb();
        await SeedStandardAsync(t.Db, JobCategory.BA);
        var log = new WarningRecorder();

        await Svc(t.Db, log).ReplaceAsync(
            Guid.NewGuid(), JobCategory.BA, CopyOfSeed(SeedShape.Select(s => s.Name).ToArray()));

        Assert.Empty(log.Warnings);
    }

    // ── (4) So tên: trim + không phân biệt hoa thường, nhưng KHÔNG fuzzy ─────────────────────

    [Theory]
    [InlineData("Phân tích yêu cầu")]          // y hệt
    [InlineData("  Phân tích yêu cầu  ")]      // khoảng trắng thừa hai đầu
    [InlineData("PHÂN TÍCH YÊU CẦU")]          // hoa hết
    [InlineData("phân tích yêu cầu")]          // thường hết
    [InlineData(" pHâN tÍcH yêU cẦu ")]        // trộn + khoảng trắng
    public async Task Replace_SoTen_BoQuaHoaThuongVaKhoangTrangThua(string typed)
    {
        using var t = new TestDb();
        await SeedStandardAsync(t.Db, JobCategory.BA);
        var me = Guid.NewGuid();
        var log = new WarningRecorder();

        await Svc(t.Db, log).ReplaceAsync(me, JobCategory.BA, CopyOfSeed(typed, "Giao tiếp & trình bày"));

        var row = await t.Db.RubricCriteria.AsNoTracking()
            .SingleAsync(c => c.CandidateId == me && c.IsActive && c.Name == typed.Trim());
        Assert.Equal(ScoringScope.WhenTargeted, row.ScoringScope);
        Assert.Empty(log.Warnings);
    }

    /// Chuẩn hoá phải áp cho CẢ HAI vế: tên trong DB cũng có thể mang khoảng trắng thừa (bộ chuẩn do
    /// admin gõ qua màn AdminB2CRubric, không phải hằng số trong code).
    [Fact]
    public async Task Replace_TenBoChuanCoKhoangTrangThua_VanKhop()
    {
        using var t = new TestDb();
        await SeedStandardAsync(t.Db, JobCategory.BA,
            shape: [("  Phân tích yêu cầu ", ScoringScope.WhenTargeted)]);
        var me = Guid.NewGuid();
        var log = new WarningRecorder();

        await Svc(t.Db, log).ReplaceAsync(me, JobCategory.BA, CopyOfSeed("Phân tích yêu cầu"));

        var row = await t.Db.RubricCriteria.AsNoTracking()
            .SingleAsync(c => c.CandidateId == me && c.IsActive);
        Assert.Equal(ScoringScope.WhenTargeted, row.ScoringScope);
        Assert.Empty(log.Warnings);
    }

    /// KHÔNG fuzzy: khớp SAI còn tệ hơn không khớp — gán nhầm <c>WhenTargeted</c> cho một tiêu chí
    /// cách nói thì nó chỉ còn được chấm ở vài câu, và không có triệu chứng nào ngoài điểm đổi nghĩa.
    [Theory]
    [InlineData("Phân tích yêu cầu nghiệp vụ")]   // tiền tố của tên seed
    [InlineData("Phân tích")]                     // tên seed chứa chuỗi này
    [InlineData("Phân  tích  yêu  cầu")]          // khoảng trắng GIỮA từ — không chuẩn hoá
    [InlineData("Phan tich yeu cau")]             // bỏ dấu
    public async Task Replace_TenGanGiong_KhongKhop_GiuAlways(string typed)
    {
        using var t = new TestDb();
        await SeedStandardAsync(t.Db, JobCategory.BA);
        var me = Guid.NewGuid();

        await Svc(t.Db).ReplaceAsync(me, JobCategory.BA, CopyOfSeed(typed, "Giao tiếp & trình bày"));

        var row = await t.Db.RubricCriteria.AsNoTracking()
            .SingleAsync(c => c.CandidateId == me && c.IsActive && c.Name == typed);
        Assert.Equal(ScoringScope.Always, row.ScoringScope);
    }

    // ── (5) Không có bộ chuẩn → giữ Always + cảnh báo NÓI RÕ nguyên nhân khác ────────────────

    [Fact]
    public async Task Replace_ChuaCoBoChuan_GiuAlways_VaCanhBaoThieuBoChuan()
    {
        using var t = new TestDb();   // cố ý KHÔNG seed
        var me = Guid.NewGuid();
        var log = new WarningRecorder();

        await Svc(t.Db, log).ReplaceAsync(
            me, JobCategory.BA, CopyOfSeed(SeedShape.Select(s => s.Name).ToArray()));

        var rows = await t.Db.RubricCriteria.AsNoTracking()
            .Where(c => c.CandidateId == me && c.IsActive).ToListAsync();
        Assert.All(rows, r => Assert.Equal(ScoringScope.Always, r.ScoringScope));

        // Một cảnh báo GỘP, không phải 7 dòng rời — và phải phân biệt được với ca "tên lạ", vì cách
        // xử lý khác hẳn nhau (apply seed BC11 vs. hỏi lại người chốt rubric).
        var warning = Assert.Single(log.Warnings);
        Assert.Contains("BỘ CHUẨN", warning);
    }

    /// Bộ chuẩn bị deactivate KHÔNG phải nguồn kế thừa: `is_active` là "bộ dùng cho buổi MỚI".
    [Fact]
    public async Task Replace_BoChuanDaDeactivate_KhongDungLamNguon()
    {
        using var t = new TestDb();
        await SeedStandardAsync(t.Db, JobCategory.BA, active: false);
        var me = Guid.NewGuid();

        await Svc(t.Db).ReplaceAsync(me, JobCategory.BA, CopyOfSeed("Phân tích yêu cầu"));

        var row = await t.Db.RubricCriteria.AsNoTracking()
            .SingleAsync(c => c.CandidateId == me && c.IsActive);
        Assert.Equal(ScoringScope.Always, row.ScoringScope);
    }

    // ── (6) Khớp trong ĐÚNG một (nghề, ngôn ngữ, chủ bộ) ─────────────────────────────────────

    /// Tên trùng nhưng khác NGHỀ ⇒ không kế thừa. Bộ chuẩn ba nghề dùng chung 4 tên cách nói và
    /// KHÁC nhau ở tiêu chí nội dung, nên khớp lỏng chiều này là gán scope của nghề khác.
    [Fact]
    public async Task Replace_KhongKeThua_TuNgheKhac()
    {
        using var t = new TestDb();
        await SeedStandardAsync(t.Db, JobCategory.BE);   // chỉ BE có "Phân tích yêu cầu"
        await SeedStandardAsync(t.Db, JobCategory.FE, shape: [("Giao tiếp & trình bày", ScoringScope.Always)]);
        var me = Guid.NewGuid();

        await Svc(t.Db).ReplaceAsync(
            me, JobCategory.FE, CopyOfSeed("Phân tích yêu cầu", "Giao tiếp & trình bày"));

        var row = await t.Db.RubricCriteria.AsNoTracking()
            .SingleAsync(c => c.CandidateId == me && c.IsActive && c.Name == "Phân tích yêu cầu");
        Assert.Equal(ScoringScope.Always, row.ScoringScope);
    }

    /// Tên trùng nhưng khác NGÔN NGỮ ⇒ không kế thừa (F12: rubric tồn tại ở cả vi lẫn en).
    [Fact]
    public async Task Replace_KhongKeThua_TuNgonNguKhac()
    {
        using var t = new TestDb();
        await SeedStandardAsync(t.Db, JobCategory.BA, language: "en",
            shape: [("Shared name", ScoringScope.WhenTargeted)]);
        await SeedStandardAsync(t.Db, JobCategory.BA, language: "vi",
            shape: [("Shared name", ScoringScope.Always)]);
        var me = Guid.NewGuid();

        await Svc(t.Db).ReplaceAsync(me, JobCategory.BA, CopyOfSeed("Shared name"));   // mặc định "vi"

        var row = await t.Db.RubricCriteria.AsNoTracking()
            .SingleAsync(c => c.CandidateId == me && c.IsActive);
        Assert.Equal(ScoringScope.Always, row.ScoringScope);
    }

    /// Rubric riêng của NGƯỜI KHÁC không phải bộ chuẩn ⇒ không được dùng làm nguồn kế thừa
    /// (vế `CandidateId == null`). Nếu thiếu, một ứng viên có thể quyết định thước đo của người khác.
    [Fact]
    public async Task Replace_KhongKeThua_TuRubricRiengCuaNguoiKhac()
    {
        using var t = new TestDb();
        var other = Guid.NewGuid();
        var stolen = TestDb.Criterion(JobCategory.BA, name: "Tiêu chí tự đặt", candidateId: other);
        stolen.ScoringScope = ScoringScope.WhenTargeted;
        t.Db.RubricCriteria.Add(stolen);
        await t.Db.SaveChangesAsync();

        var me = Guid.NewGuid();
        await Svc(t.Db).ReplaceAsync(me, JobCategory.BA, CopyOfSeed("Tiêu chí tự đặt"));

        var row = await t.Db.RubricCriteria.AsNoTracking()
            .SingleAsync(c => c.CandidateId == me && c.IsActive);
        Assert.Equal(ScoringScope.Always, row.ScoringScope);
    }

    // ── (7) Lưu lại lần nữa vẫn giữ phân loại (không "mất scope" ở version sau) ──────────────

    [Fact]
    public async Task Replace_LanThuHai_VanGiuScope_TrenVersionMoi()
    {
        using var t = new TestDb();
        await SeedStandardAsync(t.Db, JobCategory.BA);
        var me = Guid.NewGuid();
        var names = SeedShape.Select(s => s.Name).ToArray();

        await Svc(t.Db).ReplaceAsync(me, JobCategory.BA, CopyOfSeed(names));
        await Svc(t.Db).ReplaceAsync(me, JobCategory.BA, CopyOfSeed(names));

        var rows = await t.Db.RubricCriteria.AsNoTracking()
            .Where(c => c.CandidateId == me && c.IsActive).ToListAsync();
        Assert.All(rows, r => Assert.Equal(2, r.Version));
        Assert.Equal(3, rows.Count(r => r.ScoringScope == ScoringScope.WhenTargeted));
    }

    // ── Hạ tầng test ─────────────────────────────────────────────────────────────────────────

    // PracticeService thật với mọi cộng tác viên là mock — chỉ cần đường ĐỌC rubric
    // (GetSessionOptionsAsync không gọi AI, không reserve credit).
    private static PracticeService Practice(TestDb t)
        => new(
            t.Db,
            new Mock<IStorageService>().Object,
            new Mock<IAiServiceQuestionGenerator>().Object,
            new Mock<ISessionScoringNotifier>().Object,
            new Mock<ICreditReservationClient>().Object,
            NullLogger<PracticeService>.Instance,
            Options.Create(new AdaptiveOptions()));

    /// Mẫu <c>SeedCoverageSc1Tests.WarningRecorder</c> — cảnh báo SC2 chỉ tồn tại trong log, nên
    /// không bắt ở đây thì gỡ sạch nó đi bộ test vẫn xanh.
    private sealed class WarningRecorder : ILogger<RubricLibraryService>
    {
        public List<string> Warnings { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning) Warnings.Add(formatter(state, exception));
        }
    }
}
