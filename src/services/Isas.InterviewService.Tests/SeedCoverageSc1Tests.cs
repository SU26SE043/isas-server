using Isas.InterviewService.Data;
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
/// SC1 — số câu GỐC phải PHỦ được các tiêu chí NỘI DUNG của rubric buổi đó.
///
/// <para><b>Bằng chứng từ prod</b> (buổi <c>95ee0cc3</c>, BE/vi, 12 câu, đào sâu 3): 3 câu gốc nhưng
/// nhãn ra "Chiều sâu kỹ thuật" HAI lần ⇒ "Giải quyết vấn đề &amp; thuật toán" không câu nào hỏi ⇒
/// bị loại khỏi điểm (đúng thiết kế chấm-theo-phạm-vi) ⇒ điểm thành "may mắn trúng tủ".</para>
///
/// <para>Hai vế: (a) số câu gốc bám con số tiêu chí ĐỘNG chứ không hằng số config — BC16 cho
/// candidate tự CRUD rubric nên hằng số sẽ lệch âm thầm; (b) prompt ép phân bổ + guard .NET nói ra
/// khi vẫn thiếu phủ.</para>
/// </summary>
public class SeedCoverageSc1Tests
{
    private static RubricCriterion C(string name, ScoringScope scope)
        => new()
        {
            Id = Guid.NewGuid(), Name = name, Weight = 0.1m, MaxScore = 5,
            IsActive = true, JobCategory = JobCategory.BE, Language = "vi",
            ScoringScope = scope
        };

    /// Rubric seed thật của B2C: 4 tiêu chí CÁCH NÓI + <paramref name="contentCount"/> tiêu chí NỘI DUNG.
    private static List<RubricCriterion> SeedRubric(TestDb t, int contentCount = 3)
    {
        var all = new List<RubricCriterion>
        {
            C("Giao tiếp & trình bày", ScoringScope.Always),
            C(B2CRubricSeed.FluencyName, ScoringScope.Always),
            C(B2CRubricSeed.LanguageName, ScoringScope.Always),
            C(B2CRubricSeed.TerminologyName, ScoringScope.Always),
        };
        var content = Enumerable.Range(1, contentCount)
            .Select(i => C($"Tiêu chí nội dung {i}", ScoringScope.WhenTargeted))
            .ToList();
        all.AddRange(content);
        t.Db.RubricCriteria.AddRange(all);
        t.Db.SaveChanges();
        return content;
    }

    /// Mock overload GIÀU NHẤT (có `criteria`) — đường đi khi rubric CÓ tiêu chí nội dung.
    /// Ghi lại `count` đã xin để test đọc ra số câu gốc thật sự được yêu cầu.
    private static Mock<IAiServiceQuestionGenerator> LabelingGenerator(
        Action<int?> captureCount, Func<int, List<GeneratedQuestion>> questions)
    {
        var gen = new Mock<IAiServiceQuestionGenerator>();
        gen.Setup(g => g.GenerateQuestionsAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<IReadOnlyList<string>?>(), It.IsAny<int?>(),
                It.IsAny<IReadOnlyList<GroundingChunk>?>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<QuestionTargetCriterionDto>?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback((string _, string? _, string? _, IReadOnlyList<string>? _, int? count,
                       IReadOnlyList<GroundingChunk>? _, string _,
                       IReadOnlyList<QuestionTargetCriterionDto>? _, string _, CancellationToken _) => captureCount(count))
            .ReturnsAsync((string _, string? _, string? _, IReadOnlyList<string>? _, int? count,
                           IReadOnlyList<GroundingChunk>? _, string _,
                           IReadOnlyList<QuestionTargetCriterionDto>? _, string _, CancellationToken _)
                => new GeneratedQuestionsResult(questions(count ?? 0), Array.Empty<QuestionCitationDto>()));
        return gen;
    }

    private static PracticeService Build(
        TestDb t, Mock<IAiServiceQuestionGenerator> gen, AdaptiveOptions adaptive,
        ILogger<PracticeService>? logger = null)
    {
        var reservation = new Mock<ICreditReservationClient>();
        reservation.Setup(r => r.ReserveAsync(
                It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CreditReservationResult(Guid.NewGuid(), 1));

        return new PracticeService(
            t.Db, new Mock<IStorageService>().Object, gen.Object,
            new Mock<ISessionScoringNotifier>().Object, reservation.Object,
            logger ?? NullLogger<PracticeService>.Instance, Options.Create(adaptive));
    }

    /// Bắt cảnh báo SC1. Guard phủ CHỈ có tác dụng qua log (cố ý không sửa nhãn — xem
    /// <c>PracticeService</c>), nên nếu không assert log thì gỡ sạch guard đi test vẫn xanh:
    /// bộ test sẽ hẹp hơn nó trông có vẻ.
    private sealed class WarningRecorder : ILogger<PracticeService>
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

    // ── (a) Số câu gốc bám số tiêu chí nội dung ────────────────────────────────────────

    // Ca prod: `SeedCount = 1` trong config nhưng rubric có 3 tiêu chí nội dung ⇒ phải sinh 3 câu gốc.
    // Đây là vế QUAN TRỌNG NHẤT: sàn theo tiêu chí phải THẮNG trần config, nếu không thì cấu hình
    // (một hằng số ai đó đặt hồi rollout) quyết định thước đo điểm của ứng viên.
    [Fact]
    public async Task Create_SoCauGoc_KhongDuoiSoTieuChiNoiDung_DuConfigSeedCountThap()
    {
        using var t = new TestDb();
        var content = SeedRubric(t, contentCount: 3);

        int? requested = null;
        var gen = LabelingGenerator(
            c => requested = c,
            n => Enumerable.Range(0, n)
                .Select(i => new GeneratedQuestion
                {
                    Content = $"Q{i}",
                    TargetCriterionIds = [content[i % content.Count].Id]
                }).ToList());

        var svc = Build(t, gen, new AdaptiveOptions
        {
            Enabled = true, SeedCount = 1, MaxQuestions = 20, MaxDeepPerQuestion = 3
        });

        var res = await svc.CreateSessionAsync(
            Guid.NewGuid(),
            new CreatePracticeSessionRequest(null, null, JobCategory.BE, QuestionCount: 12));

        Assert.Equal(3, requested);              // xin AI đủ câu để phủ 3 tiêu chí, không phải 1
        Assert.Equal(3, res.Questions.Count);

        var s = await t.Db.PracticeSessions.AsNoTracking().FirstAsync(x => x.Id == res.Id);
        Assert.True(s.MaxQuestions > 3);         // vẫn còn khe cho câu đào sâu
    }

    // Rubric riêng BC16 có nhiều tiêu chí nội dung hơn ⇒ số câu gốc tự tăng theo. Đây là lý do phải
    // đọc con số ĐỘNG từ rubric: chốt cứng "3" trong config sẽ lệch ngay khi candidate thêm tiêu chí.
    [Fact]
    public async Task Create_RubricNhieuTieuChiHon_SoCauGocTangTheo()
    {
        using var t = new TestDb();
        var content = SeedRubric(t, contentCount: 5);

        int? requested = null;
        var gen = LabelingGenerator(
            c => requested = c,
            n => Enumerable.Range(0, n)
                .Select(i => new GeneratedQuestion
                {
                    Content = $"Q{i}",
                    TargetCriterionIds = [content[i % content.Count].Id]
                }).ToList());

        var svc = Build(t, gen, new AdaptiveOptions
        {
            Enabled = true, SeedCount = 5, MaxQuestions = 20, MaxDeepPerQuestion = 3
        });

        var res = await svc.CreateSessionAsync(
            Guid.NewGuid(),
            new CreatePracticeSessionRequest(null, null, JobCategory.BE, QuestionCount: 12));

        // Ngân sách 12 / (1+3) = 3, nhưng có 5 tiêu chí nội dung ⇒ sàn phủ thắng.
        Assert.Equal(5, requested);
        Assert.Equal(5, res.Questions.Count);
    }

    // Ngân sách hẹp: ưu tiên PHỦ hơn ĐÀO SÂU. 5 câu / 3 tiêu chí → 3 gốc + 2 khe, chứ không phải
    // 2 gốc (ceil(5/4)) rồi bỏ rơi một tiêu chí. Thiếu phủ = tiêu chí biến mất khỏi điểm; thiếu đào
    // sâu = chỉ mất chiều sâu.
    [Fact]
    public async Task Create_NganSachHep_UuTienPhuHonDaoSau()
    {
        using var t = new TestDb();
        var content = SeedRubric(t, contentCount: 3);

        int? requested = null;
        var gen = LabelingGenerator(
            c => requested = c,
            n => Enumerable.Range(0, n)
                .Select(i => new GeneratedQuestion
                {
                    Content = $"Q{i}",
                    TargetCriterionIds = [content[i % content.Count].Id]
                }).ToList());

        var svc = Build(t, gen, new AdaptiveOptions
        {
            Enabled = true, SeedCount = 5, MaxQuestions = 20, MaxDeepPerQuestion = 3
        });

        var res = await svc.CreateSessionAsync(
            Guid.NewGuid(),
            new CreatePracticeSessionRequest(null, null, JobCategory.BE, QuestionCount: 5));

        Assert.Equal(3, requested);

        var s = await t.Db.PracticeSessions.AsNoTracking().FirstAsync(x => x.Id == res.Id);
        Assert.True(s.MaxQuestions > res.Questions.Count);   // 5 > 3 ⇒ vẫn còn khe đào sâu
    }

    // Ngân sách BẤT KHẢ THI (3 câu / 3 tiêu chí + còn phải chừa khe đào sâu). Bất biến thắng sau cùng
    // là "luôn còn ≥ 1 khe": để seeds == trần thì buổi đóng dấu adaptive nhưng chạy y như batch tĩnh —
    // hỏng KHÔNG triệu chứng, trong khi thiếu phủ thì có log cảnh báo. Và tuyệt đối không được ném:
    // credit đã reserve rồi (PAY-5).
    [Fact]
    public async Task Create_NganSachBatKhaThi_VanChuaKheDaoSau_VaKhongNem()
    {
        using var t = new TestDb();
        var content = SeedRubric(t, contentCount: 3);

        int? requested = null;
        var gen = LabelingGenerator(
            c => requested = c,
            n => Enumerable.Range(0, n)
                .Select(i => new GeneratedQuestion
                {
                    Content = $"Q{i}",
                    TargetCriterionIds = [content[i % content.Count].Id]
                }).ToList());

        var log = new WarningRecorder();
        var svc = Build(t, gen, new AdaptiveOptions
        {
            Enabled = true, SeedCount = 5, MaxQuestions = 20, MaxDeepPerQuestion = 3
        }, log);

        var res = await svc.CreateSessionAsync(
            Guid.NewGuid(),
            new CreatePracticeSessionRequest(null, null, JobCategory.BE, QuestionCount: 3));

        Assert.Equal(2, requested);              // 3 - 1 khe = 2 câu gốc
        Assert.Equal(2, res.Questions.Count);

        var s = await t.Db.PracticeSessions.AsNoTracking().FirstAsync(x => x.Id == res.Id);
        Assert.True(s.MaxQuestions > res.Questions.Count);

        // Cắt bớt phủ là quyết định có hậu quả lên ĐIỂM — phải nói ra, không cắt im lặng (tiền lệ F9).
        Assert.Contains(log.Warnings, w => w.Contains("chỉ có 2 câu gốc cho 3 tiêu chí nội dung"));
    }

    // Rubric KHÔNG có tiêu chí nội dung nào (rubric riêng BC16 chưa phân loại / seed chưa apply) ⇒
    // công thức cũ giữ NGUYÊN: ceil(20/4) = 5. Sàn phủ không được phép đẩy số câu gốc lên khi không
    // có gì để phủ.
    [Fact]
    public async Task Create_KhongCoTieuChiNoiDung_GiuNguyenCongThucNganSach()
    {
        using var t = new TestDb();
        t.Db.RubricCriteria.Add(C("Giao tiếp & trình bày", ScoringScope.Always));
        await t.Db.SaveChangesAsync();

        int? requested = null;
        var gen = new Mock<IAiServiceQuestionGenerator>();
        gen.Setup(g => g.GenerateQuestionsAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<IReadOnlyList<string>?>(), It.IsAny<int?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string?, string?, IReadOnlyList<string>?, int?, string, CancellationToken>(
                (_, _, _, _, c, _, _) => requested = c)
            .ReturnsAsync(Enumerable.Range(1, 9)
                .Select(i => new GeneratedQuestion { Content = $"Q{i}" }).ToList());

        var svc = Build(t, gen, new AdaptiveOptions
        {
            Enabled = true, SeedCount = 5, MaxQuestions = 20, MaxDeepPerQuestion = 3
        });

        var res = await svc.CreateSessionAsync(
            Guid.NewGuid(),
            new CreatePracticeSessionRequest(null, null, JobCategory.BE, QuestionCount: 20));

        Assert.Equal(5, requested);
        Assert.Equal(5, res.Questions.Count);
    }

    // KILL-SWITCH `MaxDeepPerQuestion = 0` phải quay lại ĐÚNG hành vi trước INT-17b — kể cả khi
    // rubric CÓ tiêu chí nội dung. Nếu sàn phủ len được vào nhánh này thì "tắt" lại ra một hành vi
    // thứ ba, đúng lúc người trực cần nó nhất.
    [Fact]
    public async Task Create_KillSwitch_TranDoSauBang0_SanPhuKhongDuocCanThiep()
    {
        using var t = new TestDb();
        var content = SeedRubric(t, contentCount: 3);

        int? requested = null;
        var gen = LabelingGenerator(
            c => requested = c,
            _ => Enumerable.Range(1, 9)
                .Select(i => new GeneratedQuestion
                {
                    Content = $"Q{i}",
                    TargetCriterionIds = [content[0].Id]
                }).ToList());

        var svc = Build(t, gen, new AdaptiveOptions
        {
            Enabled = true, SeedCount = 2, MaxQuestions = 20, MaxDeepPerQuestion = 0
        });

        var res = await svc.CreateSessionAsync(
            Guid.NewGuid(),
            new CreatePracticeSessionRequest(null, null, JobCategory.BE, QuestionCount: 12));

        // Trần độ sâu 0 ⇒ `seedCount == null` ⇒ xin AI đúng `questionCount` (12), rồi cắt còn
        // `SeedCount` (2) như luồng adaptive trước INT-17b. Sàn phủ (3) KHÔNG được chạm vào con số nào.
        Assert.Equal(12, requested);
        Assert.Equal(2, res.Questions.Count);
    }

    // KHÔNG có trần buổi (`MaxQuestions = 0` = không trần, xem AdaptiveOptions) ⇒ bỏ vế ngân sách
    // lẫn vế chừa khe, nhưng SÀN PHỦ vẫn phải có hiệu lực. Nhánh này dễ bị bỏ quên vì nó không đi
    // qua phép chia nào cả.
    [Fact]
    public async Task Create_KhongCoTranBuoi_VanPhuDuTieuChi()
    {
        using var t = new TestDb();
        var content = SeedRubric(t, contentCount: 3);

        int? requested = null;
        var gen = LabelingGenerator(
            c => requested = c,
            n => Enumerable.Range(0, n)
                .Select(i => new GeneratedQuestion
                {
                    Content = $"Q{i}",
                    TargetCriterionIds = [content[i % content.Count].Id]
                }).ToList());

        var svc = Build(t, gen, new AdaptiveOptions
        {
            Enabled = true, SeedCount = 2, MaxQuestions = 0, MaxDeepPerQuestion = 3
        });

        // Không chọn số câu ⇒ MaxQuestions của buổi = `_adaptive.MaxQuestions` = 0 = không trần.
        var res = await svc.CreateSessionAsync(
            Guid.NewGuid(), new CreatePracticeSessionRequest(null, null, JobCategory.BE));

        var s = await t.Db.PracticeSessions.AsNoTracking().FirstAsync(x => x.Id == res.Id);
        Assert.Equal(0, s.MaxQuestions);
        Assert.Equal(3, requested);          // sàn phủ (3) thắng config SeedCount (2)
        Assert.Equal(3, res.Questions.Count);
    }

    // ── (b) Guard phủ — thiếu phủ KHÔNG được sửa nhãn, cũng không được làm hỏng buổi ────

    // AI dồn cả 3 câu vào 1 tiêu chí (đúng hình dạng bug prod). Buổi vẫn tạo được, và nhãn giữ
    // NGUYÊN VẸN: không bịa nhãn bù (⇒ chấm thứ không được hỏi), không xoá sạch nhãn (⇒ lùi về chấm
    // cả rubric cho MỌI câu). Cả hai "cách chữa" đó đều tệ hơn việc mất một tiêu chí.
    [Fact]
    public async Task Create_AiDonNhanVaoMotTieuChi_GiuNguyenNhan_KhongBiaKhongXoa()
    {
        using var t = new TestDb();
        var content = SeedRubric(t, contentCount: 3);

        var gen = LabelingGenerator(
            _ => { },
            n => Enumerable.Range(0, n)
                .Select(i => new GeneratedQuestion
                {
                    Content = $"Q{i}",
                    TargetCriterionIds = [content[0].Id]   // TRÙNG — 2 tiêu chí còn lại không ai hỏi
                }).ToList());

        var svc = Build(t, gen, new AdaptiveOptions
        {
            Enabled = true, SeedCount = 5, MaxQuestions = 20, MaxDeepPerQuestion = 3
        });

        var res = await svc.CreateSessionAsync(
            Guid.NewGuid(),
            new CreatePracticeSessionRequest(null, null, JobCategory.BE, QuestionCount: 12));

        var stored = await t.Db.PracticeQuestions.AsNoTracking()
            .Where(q => q.SessionId == res.Id).ToListAsync();

        Assert.Equal(3, stored.Count);
        Assert.All(stored, q => Assert.Equal(new[] { content[0].Id }, q.TargetCriterionIds));

        // Con dấu phạm vi vẫn là 2 (buổi CÓ nhãn) — guard không được đổi ý nghĩa thước đo.
        var s = await t.Db.PracticeSessions.AsNoTracking().FirstAsync(x => x.Id == res.Id);
        Assert.Equal(2, s.ScoringScopeVersion);
    }

    // Thiếu phủ PHẢI được nói ra. Guard cố ý không sửa nhãn, nên log LÀ toàn bộ tác dụng của nó —
    // không assert log thì gỡ sạch guard đi bộ test vẫn xanh (đúng lớp "bộ test hẹp hơn ta tưởng").
    [Fact]
    public async Task Create_ThieuPhu_CanhBaoNeuDichDanhTieuChiKhongDuocHoi()
    {
        using var t = new TestDb();
        var content = SeedRubric(t, contentCount: 3);
        var log = new WarningRecorder();

        var gen = LabelingGenerator(
            _ => { },
            n => Enumerable.Range(0, n)
                .Select(i => new GeneratedQuestion
                {
                    Content = $"Q{i}",
                    TargetCriterionIds = [content[0].Id]   // dồn cục — 2 tiêu chí không ai hỏi
                }).ToList());

        var svc = Build(t, gen, new AdaptiveOptions
        {
            Enabled = true, SeedCount = 5, MaxQuestions = 20, MaxDeepPerQuestion = 3
        }, log);

        await svc.CreateSessionAsync(
            Guid.NewGuid(),
            new CreatePracticeSessionRequest(null, null, JobCategory.BE, QuestionCount: 12));

        var warn = Assert.Single(log.Warnings, w => w.Contains("KHÔNG được câu hỏi"));
        // Phải nêu ĐÍCH DANH tiêu chí nào — "thiếu 2 tiêu chí" thì người trực vẫn phải tự đi tra.
        Assert.Contains(content[1].Name, warn);
        Assert.Contains(content[2].Name, warn);
        Assert.DoesNotContain(content[0].Name, warn);   // tiêu chí ĐÃ được hỏi không được kể vào
    }

    // Phủ đủ ⇒ IM LẶNG. Cảnh báo bắn cả khi mọi thứ ổn thì nó thành nhiễu và sẽ bị bỏ qua đúng lúc
    // cần đọc nhất.
    [Fact]
    public async Task Create_PhuDu_KhongCanhBao()
    {
        using var t = new TestDb();
        var content = SeedRubric(t, contentCount: 3);
        var log = new WarningRecorder();

        var gen = LabelingGenerator(
            _ => { },
            n => Enumerable.Range(0, n)
                .Select(i => new GeneratedQuestion
                {
                    Content = $"Q{i}",
                    TargetCriterionIds = [content[i % content.Count].Id]
                }).ToList());

        var svc = Build(t, gen, new AdaptiveOptions
        {
            Enabled = true, SeedCount = 5, MaxQuestions = 20, MaxDeepPerQuestion = 3
        }, log);

        await svc.CreateSessionAsync(
            Guid.NewGuid(),
            new CreatePracticeSessionRequest(null, null, JobCategory.BE, QuestionCount: 12));

        Assert.DoesNotContain(log.Warnings, w => w.Contains("SC1"));
    }

    // Phủ phải đếm trên bộ câu ĐÃ CẮT (`seedQuestions`), không phải bộ AI trả về. AI hay trả THỪA;
    // đếm trên bộ thô sẽ báo "phủ đủ" trong khi buổi thật chỉ giữ mấy câu đầu cùng một tiêu chí —
    // cảnh báo nói dối còn tệ hơn không có cảnh báo.
    [Fact]
    public async Task Create_AiTraThua_PhuDemTrenBoDaCat_KhongPhaiBoTho()
    {
        using var t = new TestDb();
        var content = SeedRubric(t, contentCount: 3);
        var log = new WarningRecorder();

        // 9 câu: 3 câu ĐẦU dồn vào content[0] (phần được giữ), 6 câu sau mới phủ nốt 2 tiêu chí kia.
        var gen = LabelingGenerator(
            _ => { },
            _ => Enumerable.Range(0, 9)
                .Select(i => new GeneratedQuestion
                {
                    Content = $"Q{i}",
                    TargetCriterionIds = [content[i < 3 ? 0 : i % content.Count].Id]
                }).ToList());

        var svc = Build(t, gen, new AdaptiveOptions
        {
            Enabled = true, SeedCount = 5, MaxQuestions = 20, MaxDeepPerQuestion = 3
        }, log);

        var res = await svc.CreateSessionAsync(
            Guid.NewGuid(),
            new CreatePracticeSessionRequest(null, null, JobCategory.BE, QuestionCount: 12));

        Assert.Equal(3, res.Questions.Count);   // chỉ giữ 3 câu đầu
        Assert.Contains(log.Warnings, w => w.Contains("KHÔNG được câu hỏi"));
    }

    // Câu xã giao ([] = "đã xét, không nhắm tiêu chí nội dung nào") KHÔNG được guard quy về "đã phủ"
    // hay bị sửa thành null. Đây là ca mà cả hai chiều đều sai: coi [] là phủ ⇒ nuốt mất cảnh báo;
    // biến [] thành null ⇒ câu xã giao bị chấm tiêu chí chuyên môn.
    [Fact]
    public async Task Create_CauXaGiaoNhanRong_GiuNguyenRong()
    {
        using var t = new TestDb();
        var content = SeedRubric(t, contentCount: 3);

        var gen = LabelingGenerator(
            _ => { },
            n => Enumerable.Range(0, n)
                .Select(i => new GeneratedQuestion
                {
                    Content = i == 0 ? "Giới thiệu bản thân" : $"Q{i}",
                    TargetCriterionIds = i == 0 ? Array.Empty<Guid>() : [content[i % content.Count].Id]
                }).ToList());

        var svc = Build(t, gen, new AdaptiveOptions
        {
            Enabled = true, SeedCount = 5, MaxQuestions = 20, MaxDeepPerQuestion = 3
        });

        var res = await svc.CreateSessionAsync(
            Guid.NewGuid(),
            new CreatePracticeSessionRequest(null, null, JobCategory.BE, QuestionCount: 12));

        var stored = await t.Db.PracticeQuestions.AsNoTracking()
            .Where(q => q.SessionId == res.Id).OrderBy(q => q.OrderNo).ToListAsync();

        Assert.NotNull(stored[0].TargetCriterionIds);
        Assert.Empty(stored[0].TargetCriterionIds!);
    }
}
