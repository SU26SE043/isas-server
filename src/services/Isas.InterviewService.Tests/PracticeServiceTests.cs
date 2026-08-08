using Isas.InterviewService.DTOs;
using Isas.InterviewService.Enums;
using Isas.InterviewService.Models;
using Isas.InterviewService.Services;
using Isas.InterviewService.Services.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace Isas.InterviewService.Tests;

public class PracticeServiceTests
{
    private static PracticeService Build(TestDb t, Mock<IAiServiceQuestionGenerator> gen)
        => Build(t, gen, out _, out _);

    private static PracticeService Build(
        TestDb t, Mock<IAiServiceQuestionGenerator> gen, out Mock<ISessionScoringNotifier> scoringNotifier)
        => Build(t, gen, out scoringNotifier, out _);

    // BC2: mặc định reserve (owner=User) THÀNH CÔNG → luồng tạo session chạy như cũ.
    // Test 402/verify lấy `reservation` ra để setup/verify riêng.
    // DB2/BK12: `scoringNotifier` để verify ghi outbox SessionAbandoned(generation_failed) khi session Failed.
    private static PracticeService Build(
        TestDb t, Mock<IAiServiceQuestionGenerator> gen,
        out Mock<ISessionScoringNotifier> scoringNotifier,
        out Mock<ICreditReservationClient> reservation,
        IConfiguration? config = null)
    {
        scoringNotifier = new Mock<ISessionScoringNotifier>();
        scoringNotifier
            .Setup(n => n.NotifySessionScoredAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        reservation = new Mock<ICreditReservationClient>();
        reservation
            // BK14: cả "User" (B2C) và "Org" (B2B) đều trả reservation hợp lệ.
            .Setup(r => r.ReserveAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CreditReservationResult(Guid.NewGuid(), 1));

        return new PracticeService(
            t.Db, new Mock<IStorageService>().Object, gen.Object, scoringNotifier.Object,
            reservation.Object, NullLogger<PracticeService>.Instance, config: config);
    }

    [Fact]
    public async Task Create_HappyPath_SessionReady_WithQuestions()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();

        var gen = new Mock<IAiServiceQuestionGenerator>();
        gen.Setup(g => g.GenerateQuestionsAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<GeneratedQuestion>
            {
                new() { Content = "Q1" }, new() { Content = "Q2" }, new() { Content = "Q3" }
            });

        var svc = Build(t, gen, out _, out var reservation);
        var req = new CreatePracticeSessionRequest(null, null, JobCategory.BE);

        var res = await svc.CreateSessionAsync(candidate, req);

        Assert.Equal(nameof(SessionStatus.Ready), res.Status);
        Assert.Equal(3, res.Questions.Count);
        Assert.Equal(1, res.Questions[0].OrderNo);

        var saved = await t.Db.PracticeQuestions.AsNoTracking()
            .CountAsync(q => q.SessionId == res.Id);
        Assert.Equal(3, saved);

        // Adaptive TẮT (mặc định) → session không bật adaptive, mọi câu Kind=Seed.
        var s = await t.Db.PracticeSessions.AsNoTracking().FirstAsync(x => x.Id == res.Id);
        Assert.False(s.AdaptiveEnabled);
        Assert.All(res.Questions, q => Assert.Equal("Seed", q.Kind));
        // PONR1 rollout: thiếu config phải giữ luật cũ cho đến khi PONR3 đã thông báo trên UI.
        // Đây là guard chống vô tình bật thu tại Ready khi deploy image mới.
        reservation.Verify(r => r.ConsumeAsync(res.Id, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Create_ConsumeAtGenerationExplicitlyEnabled_ConsumesOnlyAfterReady()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var gen = new Mock<IAiServiceQuestionGenerator>();
        gen.Setup(g => g.GenerateQuestionsAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<GeneratedQuestion> { new() { Content = "Q1" } });

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Billing:ConsumeAtQuestionGeneration"] = "true"
        }).Build();
        var svc = Build(t, gen, out _, out var reservation, config);

        var result = await svc.CreateSessionAsync(candidate,
            new CreatePracticeSessionRequest(null, null, JobCategory.BE));

        var session = await t.Db.PracticeSessions.AsNoTracking().SingleAsync(s => s.Id == result.Id);
        Assert.Equal(SessionStatus.Ready, session.Status);
        reservation.Verify(r => r.ConsumeAsync(result.Id, It.IsAny<CancellationToken>()), Times.Once);
        reservation.Verify(r => r.ReleaseAsync(result.Id, It.IsAny<CancellationToken>()), Times.Never);
    }

    // Phỏng vấn THÍCH ỨNG (B2C): Adaptive:Enabled → chỉ giữ SeedCount câu SEED (dù AI trả nhiều hơn) +
    // đóng dấu toggle/trần lên session. Phần còn lại do AnswerService sinh động theo câu trả lời.
    //
    // ⚠ INT-17b — ĐỔI TIỀN ĐỀ CÓ CHỦ ĐÍCH: trước đây bản test này mock overload 4 tham số (không có
    // `count`), vì luồng cũ xin AI cả bộ rồi VỨT bớt chỉ giữ SeedCount — tức là trả tiền token cho những
    // câu không bao giờ dùng. Nay adaptive bật thì xin ĐÚNG số câu gốc, nên phải mock overload có `count`
    // và assert luôn SỐ CÂU GỬI ĐI (chứ không chỉ số câu giữ lại) — nếu chỉ assert số giữ lại thì lần
    // hồi quy làm nó quay về xin thừa sẽ không test nào kêu.
    [Fact]
    public async Task Create_AdaptiveEnabled_RequestsSeedCount_KeepsOnlySeedCount_AndStampsSession()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();

        int? requestedCount = null;
        var gen = new Mock<IAiServiceQuestionGenerator>();
        gen.Setup(g => g.GenerateQuestionsAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<IReadOnlyList<string>?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .Callback<string, string?, string?, IReadOnlyList<string>?, int?, CancellationToken>(
                (_, _, _, _, count, _) => requestedCount = count)
            // AI vẫn có thể trả THỪA so với yêu cầu → `Take` phải cắt.
            .ReturnsAsync(new List<GeneratedQuestion>
            {
                new() { Content = "Q1" }, new() { Content = "Q2" }, new() { Content = "Q3" }
            });

        var reservation = new Mock<ICreditReservationClient>();
        reservation
            .Setup(r => r.ReserveAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CreditReservationResult(Guid.NewGuid(), 1));

        var adaptive = Options.Create(new AdaptiveOptions
        {
            Enabled = true, SeedCount = 1, MaxQuestions = 8, MaxFollowUps = 2, MaxDeepPerQuestion = 3
        });
        var svc = new PracticeService(
            t.Db, new Mock<IStorageService>().Object, gen.Object,
            new Mock<ISessionScoringNotifier>().Object, reservation.Object,
            NullLogger<PracticeService>.Instance, adaptive);

        var res = await svc.CreateSessionAsync(candidate, new CreatePracticeSessionRequest(null, null, JobCategory.BE));

        // Xin AI ĐÚNG số câu gốc — không xin thừa rồi vứt.
        Assert.Equal(1, requestedCount);

        // Chỉ 1 câu SEED (dù AI trả 3), Kind=Seed.
        Assert.Single(res.Questions);
        Assert.Equal("Seed", res.Questions[0].Kind);
        Assert.Equal(1, await t.Db.PracticeQuestions.CountAsync(q => q.SessionId == res.Id));

        // Session đóng dấu cấu hình adaptive.
        var s = await t.Db.PracticeSessions.AsNoTracking().FirstAsync(x => x.Id == res.Id);
        Assert.True(s.AdaptiveEnabled);
        Assert.Equal(8, s.MaxQuestions);
        Assert.Equal(3, s.MaxDeepPerQuestion);
        // ⚠ ĐỔI TIỀN ĐỀ CÓ CHỦ ĐÍCH (INT-17b): trước đây assert `MaxFollowUps == 2` (đúng theo config).
        // Nay bật chế độ chuỗi thì trần theo BUỔI bị ép về 0 — để nguyên 2 thì nó bó chặt hơn trần theo
        // CÂU (1 gốc × 3) và hội thoại chết ở câu đào sâu thứ 2, tức là cấu hình tự mâu thuẫn.
        // `MaxQuestions` mới là trần buổi.
        Assert.Equal(0, s.MaxFollowUps);
    }

    // BUS-01 — số câu GỐC phải CHIA ngân sách buổi cho chiều sâu, không lấy thẳng `SeedCount`.
    //
    // FE mặc định `questionCount = 5`, mà `questionCount` là TỔNG số câu của buổi (F2b). Sinh đúng 5 câu
    // gốc thì `askedCount = 5` chạm ngay trần ⇒ `AnswerService` bước (3) thấy hết ngân sách và trả về
    // TRƯỚC cả lời gọi AI đầu tiên ⇒ buổi không có câu đào sâu nào, không lỗi, không thông báo: tính năng
    // tắt câm trên đúng đường mặc định của B2C.
    [Theory]
    [InlineData(20, 5)]   // trần 20 = thiết kế "5 gốc × (1+3)"
    [InlineData(10, 3)]   // gói plus: ceil(10/4) = 3 gốc → 3 + 7 khe sâu
    [InlineData(5, 2)]    // MẶC ĐỊNH FE: 2 gốc + 3 khe sâu = đúng 5 câu
    [InlineData(1, 1)]    // chọn 1 câu = 1 câu, không âm/không 0
    public async Task Create_ChuoiDaoSau_SoCauGoc_ChiaTheoNganSachBuoi(int questionCount, int expectedSeeds)
    {
        using var t = new TestDb();

        int? requestedCount = null;
        var gen = new Mock<IAiServiceQuestionGenerator>();
        gen.Setup(g => g.GenerateQuestionsAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<IReadOnlyList<string>?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .Callback<string, string?, string?, IReadOnlyList<string>?, int?, CancellationToken>(
                (_, _, _, _, count, _) => requestedCount = count)
            // AI trả dư — `Take` phải cắt về đúng số câu gốc đã tính.
            .ReturnsAsync(Enumerable.Range(1, 9).Select(i => new GeneratedQuestion { Content = $"Q{i}" }).ToList());

        var reservation = new Mock<ICreditReservationClient>();
        reservation
            .Setup(r => r.ReserveAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CreditReservationResult(Guid.NewGuid(), 1));

        var adaptive = Options.Create(new AdaptiveOptions
        {
            Enabled = true, SeedCount = 5, MaxQuestions = 20, MaxFollowUps = 3, MaxDeepPerQuestion = 3
        });
        var svc = new PracticeService(
            t.Db, new Mock<IStorageService>().Object, gen.Object,
            new Mock<ISessionScoringNotifier>().Object, reservation.Object,
            NullLogger<PracticeService>.Instance, adaptive);

        var res = await svc.CreateSessionAsync(
            Guid.NewGuid(),
            new CreatePracticeSessionRequest(null, null, JobCategory.BE, QuestionCount: questionCount));

        Assert.Equal(expectedSeeds, requestedCount);          // xin AI đúng ngần đó, không xin thừa
        Assert.Equal(expectedSeeds, res.Questions.Count);     // và giữ đúng ngần đó

        var s = await t.Db.PracticeSessions.AsNoTracking().FirstAsync(x => x.Id == res.Id);
        Assert.Equal(questionCount, s.MaxQuestions);
        // Bất biến thật sự cần giữ: sau khi rải seed vẫn PHẢI còn khe cho câu đào sâu (trừ khi ứng viên
        // chỉ chọn 1 câu). Đây mới là thứ BUS-01 nói tới — số câu gốc chỉ là phương tiện.
        if (questionCount > 1) Assert.True(s.MaxQuestions > expectedSeeds);
    }

    // CFG-01 — KILL-SWITCH: `MaxDeepPerQuestion = 0` phải quay lại ĐÚNG hành vi trước INT-17b, không phải
    // một hình dạng thứ ba. Trước bản vá, nhánh tính `seedCount` rẽ theo `adaptiveOn` chứ không theo trần
    // độ sâu ⇒ đặt trần 0 vẫn (a) xin AI `SeedCount` thay vì `questionCount`, (b) đẩy lời gọi sang overload
    // 6 tham số. Tức là "tắt" mà hành vi không quay lại như cũ — đúng lúc người trực cần nó nhất.
    [Fact]
    public async Task Create_KillSwitch_TranDoSauBang0_QuayLaiDungLuongTruocInt17b()
    {
        using var t = new TestDb();

        var gen = new Mock<IAiServiceQuestionGenerator>(MockBehavior.Strict);
        // Luồng CŨ: không focusCriteria + không chọn số câu ⇒ overload 4 THAM SỐ.
        gen.Setup(g => g.GenerateQuestionsAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<GeneratedQuestion>
            {
                new() { Content = "Q1" }, new() { Content = "Q2" }, new() { Content = "Q3" }
            });

        var reservation = new Mock<ICreditReservationClient>();
        reservation
            .Setup(r => r.ReserveAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CreditReservationResult(Guid.NewGuid(), 1));

        var adaptive = Options.Create(new AdaptiveOptions
        {
            Enabled = true, SeedCount = 1, MaxQuestions = 10, MaxFollowUps = 3, MaxDeepPerQuestion = 0
        });
        var svc = new PracticeService(
            t.Db, new Mock<IStorageService>().Object, gen.Object,
            new Mock<ISessionScoringNotifier>().Object, reservation.Object,
            NullLogger<PracticeService>.Instance, adaptive);

        var res = await svc.CreateSessionAsync(
            Guid.NewGuid(), new CreatePracticeSessionRequest(null, null, JobCategory.BE));

        // `MockBehavior.Strict` + chỉ setup overload 4 tham số ⇒ nếu code gọi overload có `count` thì test
        // ném ngay. Đây là phần khoá "không đổi overload".
        gen.Verify(g => g.GenerateQuestionsAsync(
            It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);

        // Vẫn cắt còn `SeedCount` câu seed như luồng adaptive cũ (AI trả 3, giữ 1).
        Assert.Single(res.Questions);

        var s = await t.Db.PracticeSessions.AsNoTracking().FirstAsync(x => x.Id == res.Id);
        Assert.True(s.AdaptiveEnabled);
        Assert.Equal(0, s.MaxDeepPerQuestion);
        // Trần theo BUỔI trở lại có hiệu lực (chế độ chuỗi mới ép nó về 0). Để 0 ở đây nghĩa là "không
        // trần" ⇒ kill-switch sẽ ra hành vi thứ ba: thích ứng chạy tới tận trần buổi.
        Assert.Equal(3, s.MaxFollowUps);
    }

    // CFG-01 (tiếp) — khoá GIÁ TRỊ MẶC ĐỊNH, không chỉ hành vi.
    //
    // Test trên tự dựng `AdaptiveOptions { MaxFollowUps = 3 }` nên nó KHÔNG phủ mặc định — mutation đổi
    // mặc định về 0 chạy qua xanh 514/514. Mà mặc định mới là thứ có hiệu lực khi env lẫn appsettings đều
    // vắng khoá này, tức đúng ca "ai đó dựng service ở môi trường mới rồi bật kill-switch".
    // `MaxFollowUps = 0` ở chế độ frontier nghĩa là KHÔNG trần (xem `AnswerService` bước (3)), nên mặc
    // định 0 biến kill-switch thành "thích ứng chạy tới tận trần buổi" thay vì hành vi trước INT-17b.
    [Fact]
    public void AdaptiveOptions_MacDinh_GiuTranBuoiChoCheDoFrontier()
    {
        var d = new AdaptiveOptions();

        Assert.Equal(3, d.MaxFollowUps);          // >0 = có trần thật khi kill-switch bật
        Assert.Equal(3, d.MaxDeepPerQuestion);    // >0 = chế độ chuỗi là mặc định
        Assert.Equal(5, d.SeedCount);
        Assert.Equal(20, d.MaxQuestions);
        Assert.Equal(3, d.MaxFailuresPerSession); // >0 = cầu dao bật (0 = tắt cầu dao)
        Assert.False(d.Enabled);                  // toggle vẫn TẮT mặc định
    }

    // SC3 — endpoint preview phải dùng đúng luật production, không để FE tự nhân bản ceil/kẹp.
    [Fact]
    public async Task SessionOptions_PreviewKhớpSoCauGocDuocTaoThucTe()
    {
        using var t = new TestDb();
        int? requestedCount = null;
        var generator = new Mock<IAiServiceQuestionGenerator>();
        generator.Setup(g => g.GenerateQuestionsAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<IReadOnlyList<string>?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .Callback<string, string?, string?, IReadOnlyList<string>?, int?, CancellationToken>(
                (_, _, _, _, count, _) => requestedCount = count)
            .ReturnsAsync(Enumerable.Range(1, 5).Select(i => new GeneratedQuestion { Content = $"Q{i}" }).ToList());
        var reservation = new Mock<ICreditReservationClient>();
        reservation.Setup(r => r.ReserveAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CreditReservationResult(Guid.NewGuid(), 1));
        var svc = new PracticeService(
            t.Db, new Mock<IStorageService>().Object, generator.Object,
            new Mock<ISessionScoringNotifier>().Object, reservation.Object,
            NullLogger<PracticeService>.Instance,
            Options.Create(new AdaptiveOptions { Enabled = true, SeedCount = 5, MaxQuestions = 20, MaxDeepPerQuestion = 3 }));
        var candidate = Guid.NewGuid();

        var options = await svc.GetSessionOptionsAsync(candidate, "BE");
        var preview = options.Preview.Single(p => p.QuestionCount == 5);
        await svc.CreateSessionAsync(candidate, new CreatePracticeSessionRequest(null, null, JobCategory.BE, QuestionCount: 5));

        Assert.True(options.AdaptiveEnabled);
        Assert.Equal(2, preview.SeedCount);
        Assert.Equal(preview.SeedCount, requestedCount);
        Assert.Equal(12, options.DefaultQuestionCount);
    }

    // INT-17b — câu GỐC đánh số CÓ KHOẢNG TRỐNG (stride = 1 + trần đào sâu) để chuỗi của câu trước có
    // chỗ nằm xen vào mà không đụng câu sau. Đây là thứ thay cho việc thêm field `displayOrder` + sửa FE:
    // `MapToResponse` và FE B2B đều sắp theo `OrderNo`, nên đánh số đúng là ra đúng thứ tự hội thoại.
    [Fact]
    public async Task Create_ChoDoChuoi_CauGocDanhSoCoKhoangTrong()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();

        var gen = new Mock<IAiServiceQuestionGenerator>();
        gen.Setup(g => g.GenerateQuestionsAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<IReadOnlyList<string>?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<GeneratedQuestion>
            {
                new() { Content = "Q1" }, new() { Content = "Q2" }, new() { Content = "Q3" },
                new() { Content = "Q4" }, new() { Content = "Q5" }
            });

        var reservation = new Mock<ICreditReservationClient>();
        reservation
            .Setup(r => r.ReserveAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CreditReservationResult(Guid.NewGuid(), 1));

        var adaptive = Options.Create(new AdaptiveOptions
        {
            Enabled = true, SeedCount = 5, MaxQuestions = 20, MaxFollowUps = 0, MaxDeepPerQuestion = 3
        });
        var svc = new PracticeService(
            t.Db, new Mock<IStorageService>().Object, gen.Object,
            new Mock<ISessionScoringNotifier>().Object, reservation.Object,
            NullLogger<PracticeService>.Instance, adaptive);

        var res = await svc.CreateSessionAsync(candidate, new CreatePracticeSessionRequest(null, null, JobCategory.BE));

        var qs = await t.Db.PracticeQuestions.AsNoTracking()
            .Where(q => q.SessionId == res.Id).OrderBy(q => q.OrderNo).ToListAsync();

        // stride = 1 + 3 = 4 → chừa đúng 3 khe cho chuỗi của mỗi câu gốc.
        Assert.Equal(new[] { 1, 5, 9, 13, 17 }, qs.Select(q => q.OrderNo).ToArray());
        Assert.All(qs, q => Assert.Equal(0, q.Depth));
        Assert.All(qs, q => Assert.Null(q.RootQuestionId));
        Assert.All(qs, q => Assert.Equal(QuestionKind.Seed, q.Kind));
    }

    // INT-17b — trần đào sâu = 0 (chế độ cũ / adaptive tắt) → stride 1 ⇒ đánh số liền nhau y như trước.
    [Fact]
    public async Task Create_AdaptiveTat_CauGocVanDanhSoLienNhau()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();

        var gen = new Mock<IAiServiceQuestionGenerator>();
        gen.Setup(g => g.GenerateQuestionsAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<GeneratedQuestion>
            {
                new() { Content = "Q1" }, new() { Content = "Q2" }, new() { Content = "Q3" }
            });

        var reservation = new Mock<ICreditReservationClient>();
        reservation
            .Setup(r => r.ReserveAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CreditReservationResult(Guid.NewGuid(), 1));

        var svc = new PracticeService(
            t.Db, new Mock<IStorageService>().Object, gen.Object,
            new Mock<ISessionScoringNotifier>().Object, reservation.Object,
            NullLogger<PracticeService>.Instance);   // không truyền AdaptiveOptions ⇒ tắt

        var res = await svc.CreateSessionAsync(candidate, new CreatePracticeSessionRequest(null, null, JobCategory.BE));

        var qs = await t.Db.PracticeQuestions.AsNoTracking()
            .Where(q => q.SessionId == res.Id).OrderBy(q => q.OrderNo).ToListAsync();
        Assert.Equal(new[] { 1, 2, 3 }, qs.Select(q => q.OrderNo).ToArray());
        Assert.Equal(0, (await t.Db.PracticeSessions.AsNoTracking().FirstAsync(x => x.Id == res.Id)).MaxDeepPerQuestion);
    }

    // BC2 (a): reserve OK → tạo session + reserve đúng ví cá nhân (owner=User, ownerId=candidate,
    // sessionId = Id session vừa tạo). Idempotency khớp session thật.
    [Fact]
    public async Task Create_ReserveOk_CreatesSession_AndReservesPersonalWallet()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();

        var gen = new Mock<IAiServiceQuestionGenerator>();
        gen.Setup(g => g.GenerateQuestionsAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<GeneratedQuestion> { new() { Content = "Q1" } });

        var svc = Build(t, gen, out _, out var reservation);
        var req = new CreatePracticeSessionRequest(null, null, JobCategory.BE);

        var res = await svc.CreateSessionAsync(candidate, req);

        Assert.Equal(nameof(SessionStatus.Ready), res.Status);
        // reserve gọi đúng: owner=User, ownerId=candidate, sessionId = Id session vừa tạo.
        reservation.Verify(r => r.ReserveAsync("User", candidate, res.Id, It.IsAny<CancellationToken>()),
            Times.Once);

        var count = await t.Db.PracticeSessions.AsNoTracking().CountAsync(s => s.CandidateId == candidate);
        Assert.Equal(1, count);
    }

    // BC2 (b): ví hết credit → Payment 402 → InsufficientCreditException; KHÔNG có row session,
    // và KHÔNG gọi AI sinh câu hỏi (reserve chặn trước).
    [Fact]
    public async Task Create_ReserveReturns402_NoSessionRow_AndSkipsQuestionGen()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();

        var gen = new Mock<IAiServiceQuestionGenerator>();

        var svc = Build(t, gen, out _, out var reservation);
        reservation
            .Setup(r => r.ReserveAsync("User", It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InsufficientCreditException("Ví không đủ credit"));

        var req = new CreatePracticeSessionRequest(null, null, JobCategory.FE);

        await Assert.ThrowsAsync<InsufficientCreditException>(() =>
            svc.CreateSessionAsync(candidate, req));

        // Không có row session (PAY-5) — cũng không có câu hỏi.
        Assert.Equal(0, await t.Db.PracticeSessions.CountAsync());
        Assert.Equal(0, await t.Db.PracticeQuestions.CountAsync());
        // Reserve chặn trước AI → không tốn 1 lượt gọi Gemini.
        gen.Verify(g => g.GenerateQuestionsAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // BK14: tạo session B2B (campaign) reserve ví ORG (owner=Org theo OrgId), KHÔNG reserve ví cá nhân (User).
    [Fact]
    public async Task CreateCampaignSession_ReservesOrgWallet_NotPersonal()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var orgId = Guid.NewGuid();

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Billing:ConsumeAtQuestionGeneration"] = "true"
        }).Build();
        var svc = Build(t, new Mock<IAiServiceQuestionGenerator>(), out _, out var reservation, config);

        var req = new CreateCampaignSessionRequest(
            Guid.NewGuid(), orgId, JobCategory.BE,
            Questions: new[] { "Q1" },
            Criteria: new[] { new CampaignCriterionInput("Technical depth", null, 1.0m, 5) });

        var res = await svc.CreateCampaignSessionAsync(candidate, req);

        // Reserve owner=Org, sessionId = session vừa tạo (idempotency key P4).
        reservation.Verify(r => r.ReserveAsync("Org", orgId, res.Id, It.IsAny<CancellationToken>()), Times.Once);
        // KHÔNG reserve ví cá nhân (User).
        reservation.Verify(r => r.ReserveAsync("User", It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // BK14: ví org hết credit → ReserveAsync ném InsufficientCreditException (402) TRƯỚC insert →
    // KHÔNG có row session (PAY-5).
    [Fact]
    public async Task CreateCampaignSession_OrgWalletEmpty_Throws402_NoSessionRow()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var orgId = Guid.NewGuid();

        var reservation = new Mock<ICreditReservationClient>();
        reservation
            .Setup(r => r.ReserveAsync("Org", orgId, It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InsufficientCreditException("Tổ chức không đủ credit"));
        var svc = new PracticeService(
            t.Db, new Mock<IStorageService>().Object, new Mock<IAiServiceQuestionGenerator>().Object,
            new Mock<ISessionScoringNotifier>().Object, reservation.Object,
            NullLogger<PracticeService>.Instance);

        var req = new CreateCampaignSessionRequest(
            Guid.NewGuid(), orgId, JobCategory.BE,
            Questions: new[] { "Q1" },
            Criteria: new[] { new CampaignCriterionInput("Technical depth", null, 1.0m, 5) });

        await Assert.ThrowsAsync<InsufficientCreditException>(() =>
            svc.CreateCampaignSessionAsync(candidate, req));

        Assert.Equal(0, await t.Db.PracticeSessions.CountAsync());
    }

    // BK14: create-or-get idempotent — session B2B đang mở → trả lại, KHÔNG reserve lần 2.
    [Fact]
    public async Task GetOrCreateCampaignSession_ExistingOpen_NoSecondReserve()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var orgId = Guid.NewGuid();
        var campaignId = Guid.NewGuid();

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Billing:ConsumeAtQuestionGeneration"] = "true"
        }).Build();
        var svc = Build(t, new Mock<IAiServiceQuestionGenerator>(), out _, out var reservation, config);

        CreateCampaignSessionRequest Req() => new(
            campaignId, orgId, JobCategory.BE,
            Questions: new[] { "Q1" },
            Criteria: new[] { new CampaignCriterionInput("Technical depth", null, 1.0m, 5) });

        var first = await svc.GetOrCreateCampaignSessionAsync(candidate, Req());
        var second = await svc.GetOrCreateCampaignSessionAsync(candidate, Req());

        Assert.Equal(first.Id, second.Id);   // cùng session
        // Reserve chỉ 1 lần (lúc tạo mới), lần 2 (get) không reserve.
        reservation.Verify(r => r.ReserveAsync("Org", orgId, It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Once);
        // PONR1: request Start lặp trả cùng session đã Consumed, tuyệt đối không gọi consume lần hai.
        reservation.Verify(r => r.ConsumeAsync(first.Id, It.IsAny<CancellationToken>()), Times.Once);
    }

    // P1 (B2C audit): thiếu jobCategory (null) → 400 (InvalidOperationException → BadRequest) TRƯỚC
    // reserve: KHÔNG giữ credit (ReserveAsync không được gọi), KHÔNG có row session, KHÔNG gọi AI.
    // Trước fix: non-nullable enum omitted → BA(0) im lặng + vẫn reserve 1 credit.
    [Fact]
    public async Task Create_MissingJobCategory_Throws_NoReserve_NoSessionRow()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();

        var gen = new Mock<IAiServiceQuestionGenerator>();

        var svc = Build(t, gen, out _, out var reservation);
        var req = new CreatePracticeSessionRequest(null, null, null);   // jobCategory thiếu

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.CreateSessionAsync(candidate, req));

        // Guard chặn TRƯỚC reserve → không giữ credit oan (PAY-5).
        reservation.Verify(r => r.ReserveAsync(
                It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Never);
        // Không có row session, không gọi AI.
        Assert.Equal(0, await t.Db.PracticeSessions.CountAsync());
        gen.Verify(g => g.GenerateQuestionsAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // P1-2: reserve THÀNH CÔNG (credit đã trừ) rồi bước hậu-reserve NÉM (ở đây: insert session lỗi
    // UNIQUE PK) → phải hoàn credit (ReleaseAsync đúng sessionId, đúng 1 lần) TRƯỚC khi ném lại lỗi gốc,
    // để credit ví User không treo. Dùng CreateLessonSessionAsync để cấp sẵn sessionId đã tồn tại trong DB.
    [Fact]
    public async Task Create_ReserveOk_InsertThrows_ReleasesCredit_AndRethrows()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var sessionId = Guid.NewGuid();

        // Chèn sẵn 1 row cùng Id qua context RIÊNG (không track ở context service dùng) → Add+SaveChanges
        // trong CreateSessionInternalAsync sẽ đụng UNIQUE(PK) → DbUpdateException (lỗi hậu-reserve thật).
        await using (var seed = t.NewContext())
        {
            var existing = TestDb.Session(candidate, SessionStatus.Ready);
            existing.Id = sessionId;
            seed.Add(existing);
            await seed.SaveChangesAsync();
        }

        var gen = new Mock<IAiServiceQuestionGenerator>();
        var svc = Build(t, gen, out _, out var reservation);
        reservation
            .Setup(r => r.ReleaseAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var req = new CreatePracticeSessionRequest(null, null, JobCategory.BE);

        await Assert.ThrowsAsync<DbUpdateException>(() =>
            svc.CreateLessonSessionAsync(candidate, req, sessionId, focusCriteria: null));

        // Bù trừ: credit đã reserve được hoàn đúng sessionId, đúng 1 lần.
        reservation.Verify(r => r.ReleaseAsync(sessionId, It.IsAny<CancellationToken>()), Times.Once);
        // Lỗi xảy ra trước khi sinh câu hỏi → generator không được gọi.
        gen.Verify(g => g.GenerateQuestionsAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Create_GeneratorReturnsEmpty_SessionFailed_Throws()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();

        var gen = new Mock<IAiServiceQuestionGenerator>();
        gen.Setup(g => g.GenerateQuestionsAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<GeneratedQuestion>());

        var svc = Build(t, gen, out var notifier, out _);
        var req = new CreatePracticeSessionRequest(null, null, JobCategory.FE);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.CreateSessionAsync(candidate, req));

        // Session phải được đánh dấu Failed (không để treo GeneratingQuestions).
        var s = await t.Db.PracticeSessions.AsNoTracking().FirstAsync(x => x.CandidateId == candidate);
        Assert.Equal(SessionStatus.Failed, s.Status);

        // DB2/BK12: AIService trả rỗng cũng là "sinh câu hỏi lỗi" sau reserve → ghi outbox abandoned để release credit.
        notifier.Verify(n => n.EnqueueSessionAbandonedAsync(
            s.Id, "generation_failed", It.IsAny<CancellationToken>()), Times.Once);
    }

    // BK12 (a): B2C reserve → AI sinh câu hỏi NÉM lỗi → Failed → phát SessionAbandoned đúng sessionId
    // + reason=generation_failed (E7 nghe để release credit ví User; nếu không có → orphan credit BC2).
    [Fact]
    public async Task Create_GeneratorThrows_SessionFailed_PublishesAbandonedToReleaseCredit()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();

        var gen = new Mock<IAiServiceQuestionGenerator>();
        gen.Setup(g => g.GenerateQuestionsAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("Gemini down"));

        var svc = Build(t, gen, out var notifier, out _);
        var req = new CreatePracticeSessionRequest(null, null, JobCategory.BE);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.CreateSessionAsync(candidate, req));

        var s = await t.Db.PracticeSessions.AsNoTracking().FirstAsync(x => x.CandidateId == candidate);
        Assert.Equal(SessionStatus.Failed, s.Status);

        // DB2/BK12: ghi outbox abandoned reason=generation_failed (OutboxDispatcher → E7 release ví User).
        // Event shape (campaign_id null B2C) khoá bởi test outbox real-notifier riêng; ở đây verify enqueue.
        notifier.Verify(n => n.EnqueueSessionAbandonedAsync(
            s.Id, "generation_failed", It.IsAny<CancellationToken>()), Times.Once);
    }

    // COMMIT-3: AI sinh câu hỏi ném AiServiceException (upstream lỗi thật) → CreateSession propagate
    // NGUYÊN TYPE (controller map 502), KHÔNG bọc InvalidOperationException (=400). Reserve vẫn release
    // (Đợt-2 P1-2, không regress) + abandon phát (BK12).
    [Fact]
    public async Task Create_GeneratorThrowsAiServiceException_PropagatesAsIs_AndReleasesCredit()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();

        var gen = new Mock<IAiServiceQuestionGenerator>();
        gen.Setup(g => g.GenerateQuestionsAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AiServiceException("AIService /generate-questions trả 503"));

        var svc = Build(t, gen, out var notifier, out var reservation);
        reservation
            .Setup(r => r.ReleaseAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var req = new CreatePracticeSessionRequest(null, null, JobCategory.BE);

        // KHÔNG phải InvalidOperationException — phải là AiServiceException (→ 502 ở controller).
        var ex = await Assert.ThrowsAsync<AiServiceException>(() => svc.CreateSessionAsync(candidate, req));
        Assert.IsNotType<InvalidOperationException>(ex);

        var s = await t.Db.PracticeSessions.AsNoTracking().FirstAsync(x => x.CandidateId == candidate);
        Assert.Equal(SessionStatus.Failed, s.Status);

        // Đợt-2 P1-2: credit đã reserve được release (không treo credit ví User).
        reservation.Verify(r => r.ReleaseAsync(s.Id, It.IsAny<CancellationToken>()), Times.Once);
        // DB2/BK12: ghi outbox abandoned để OutboxDispatcher → E7 (Payment) release.
        notifier.Verify(n => n.EnqueueSessionAbandonedAsync(
            s.Id, "generation_failed", It.IsAny<CancellationToken>()), Times.Once);
    }

    // BK12 (b): phát event là BEST-EFFORT — publisher ném lỗi KHÔNG được chặn luồng: session vẫn
    // Failed trong DB và vẫn ném InvalidOperationException gốc (không nuốt mất lỗi sinh câu hỏi).
    [Fact]
    public async Task Create_GeneratorFails_PublishThrows_StillFailedAndThrows()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();

        var gen = new Mock<IAiServiceQuestionGenerator>();
        gen.Setup(g => g.GenerateQuestionsAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("Gemini down"));

        // DB2 (real notifier): sinh câu hỏi lỗi → session Failed + ghi outbox abandoned(generation_failed)
        // CÙNG transaction, và VẪN ném InvalidOperationException gốc (không nuốt lỗi sinh câu hỏi).
        var reservation = new Mock<ICreditReservationClient>();
        reservation
            .Setup(r => r.ReserveAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CreditReservationResult(Guid.NewGuid(), 1));
        reservation
            .Setup(r => r.ReleaseAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var svc = new PracticeService(
            t.Db, new Mock<IStorageService>().Object, gen.Object,
            TestDb.Notifier(t.Db), reservation.Object, NullLogger<PracticeService>.Instance);

        var req = new CreatePracticeSessionRequest(null, null, JobCategory.FE);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.CreateSessionAsync(candidate, req));

        var s = await t.Db.PracticeSessions.AsNoTracking().FirstAsync(x => x.CandidateId == candidate);
        Assert.Equal(SessionStatus.Failed, s.Status);

        // Outbox abandoned(generation_failed) ghi atomic với Failed → OutboxDispatcher phát để E7 release.
        using var read = t.NewContext();
        Assert.Equal(1, TestDb.OutboxCount(read, s.Id, "session.abandoned"));
        var abandoned = TestDb.AbandonedOutbox(read, s.Id);
        Assert.Equal("generation_failed", abandoned!.Reason);
    }

    // BK12 (c): đường THÀNH CÔNG (session Ready, không Failed) KHÔNG ghi outbox SessionAbandoned.
    [Fact]
    public async Task Create_HappyPath_DoesNotPublishAbandoned()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();

        var gen = new Mock<IAiServiceQuestionGenerator>();
        gen.Setup(g => g.GenerateQuestionsAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<GeneratedQuestion> { new() { Content = "Q1" } });

        var svc = Build(t, gen, out var notifier, out _);
        var req = new CreatePracticeSessionRequest(null, null, JobCategory.BE);

        var res = await svc.CreateSessionAsync(candidate, req);

        Assert.Equal(nameof(SessionStatus.Ready), res.Status);
        notifier.Verify(n => n.EnqueueSessionAbandonedAsync(
            It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Submit_NoAnswers_Throws()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var session = TestDb.Session(candidate, SessionStatus.InProgress);
        t.Db.Add(session);
        await t.Db.SaveChangesAsync();

        var svc = Build(t, new Mock<IAiServiceQuestionGenerator>());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.SubmitSessionAsync(candidate, session.Id));
    }

    [Fact]
    public async Task Submit_WrongStatus_Throws()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var session = TestDb.Session(candidate, SessionStatus.Scored);
        var q = TestDb.Question(session.Id);
        var a = TestDb.Answer(session.Id, q.Id, AnswerStatus.Scored, DateTime.UtcNow, DateTime.UtcNow);
        t.Db.AddRange(session, q, a);
        await t.Db.SaveChangesAsync();

        var svc = Build(t, new Mock<IAiServiceQuestionGenerator>());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.SubmitSessionAsync(candidate, session.Id));
    }

    [Fact]
    public async Task Submit_WrongCandidate_Throws()
    {
        using var t = new TestDb();
        var owner = Guid.NewGuid();
        var session = TestDb.Session(owner, SessionStatus.InProgress);
        t.Db.Add(session);
        await t.Db.SaveChangesAsync();

        var svc = Build(t, new Mock<IAiServiceQuestionGenerator>());

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            svc.SubmitSessionAsync(Guid.NewGuid(), session.Id));
    }

    [Fact]
    public async Task Submit_AllAnswersAlreadyScored_ClosesSessionToScored()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var session = TestDb.Session(candidate, SessionStatus.InProgress);
        var q = TestDb.Question(session.Id);
        // Chấm dần: answer đã Scored TRƯỚC khi submit -> submit phải đóng luôn sang Scored.
        var a = TestDb.Answer(session.Id, q.Id, AnswerStatus.Scored, DateTime.UtcNow, DateTime.UtcNow);
        t.Db.AddRange(session, q, a);
        await t.Db.SaveChangesAsync();

        var svc = Build(t, new Mock<IAiServiceQuestionGenerator>(), out var notifier);
        await svc.SubmitSessionAsync(candidate, session.Id);

        var s = await t.Db.PracticeSessions.AsNoTracking().FirstAsync(x => x.Id == session.Id);
        Assert.Equal(SessionStatus.Scored, s.Status);

        // E2: nhánh "đóng-ngay" của submit (mọi answer đã Scored từ trước, chấm dần xong sớm)
        // CŨNG phải phát SessionScored — không chỉ nhánh đóng qua callback ở AnswerService.
        notifier.Verify(n => n.NotifySessionScoredAsync(session.Id, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // PAY-13: submit đóng-ngay nhưng answer duy nhất đã Failed (0 answer Scored) → SessionAbandoned
    // (phát abandon/release), KHÔNG Scored/consume. Đối xứng với AnswerService.TryCompleteSession.
    [Fact]
    public async Task Submit_AllAnswersFailed_NoScored_ClosesToAbandoned_PublishesAbandoned()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var session = TestDb.Session(candidate, SessionStatus.InProgress);
        var q = TestDb.Question(session.Id);
        // Answer đã Failed trước khi submit (chấm dần lỗi) → 0 answer Scored.
        var a = TestDb.Answer(session.Id, q.Id, AnswerStatus.Failed, DateTime.UtcNow, DateTime.UtcNow);
        t.Db.AddRange(session, q, a);
        await t.Db.SaveChangesAsync();

        var svc = Build(t, new Mock<IAiServiceQuestionGenerator>(), out var notifier);
        await svc.SubmitSessionAsync(candidate, session.Id);

        var s = await t.Db.PracticeSessions.AsNoTracking().FirstAsync(x => x.Id == session.Id);
        Assert.Equal(SessionStatus.SessionAbandoned, s.Status);

        // DB2: ghi outbox abandoned (release), KHÔNG ghi/notify scored (consume).
        notifier.Verify(n => n.EnqueueSessionAbandonedAsync(session.Id, It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once);
        notifier.Verify(n => n.EnqueueSessionScoredAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Never);
        notifier.Verify(n => n.NotifySessionScoredAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // I2 (D21): chốt buổi theo từng câu — câu CHƯA trả lời → answer `Skipped`; câu đã trả lời giữ nguyên.
    // Mọi answer done (Scored + Skipped) → session Scored (không kẹt Scoring vì câu trống).
    [Fact]
    public async Task Submit_UnansweredQuestions_MarkedSkipped_AndClosesToScored()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var session = TestDb.Session(candidate, SessionStatus.InProgress);
        var q1 = TestDb.Question(session.Id, 1);
        var q2 = TestDb.Question(session.Id, 2);
        // q1 trả lời + đã Scored; q2 chưa trả lời.
        var a1 = TestDb.Answer(session.Id, q1.Id, AnswerStatus.Scored, DateTime.UtcNow, DateTime.UtcNow);
        t.Db.AddRange(session, q1, q2, a1);
        await t.Db.SaveChangesAsync();

        var svc = Build(t, new Mock<IAiServiceQuestionGenerator>(), out var notifier);
        await svc.SubmitSessionAsync(candidate, session.Id);

        var s = await t.Db.PracticeSessions.AsNoTracking().FirstAsync(x => x.Id == session.Id);
        Assert.Equal(SessionStatus.Scored, s.Status);

        var answers = await t.Db.PracticeAnswers.AsNoTracking()
            .Where(x => x.SessionId == session.Id).ToListAsync();
        Assert.Equal(2, answers.Count);
        Assert.Contains(answers, x => x.QuestionId == q2.Id && x.Status == AnswerStatus.Skipped);

        notifier.Verify(n => n.NotifySessionScoredAsync(session.Id, It.IsAny<CancellationToken>()), Times.Once);
    }

    // DB2 (real notifier): submit đóng-ngay Scored → ghi outbox SessionScored CÙNG transaction với state-flip
    // (OutboxDispatcher phát để E7 consume). Đối xứng nhánh callback chấm dần.
    [Fact]
    public async Task Submit_ClosesToScored_WritesScoredOutbox_RealNotifier()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var session = TestDb.Session(candidate, SessionStatus.InProgress, JobCategory.BE);
        var q = TestDb.Question(session.Id);
        var crit = TestDb.Criterion(JobCategory.BE);   // maxScore 5, weight 1.0
        crit.MaxScore = 5; crit.Weight = 1.0m;
        var a = TestDb.Answer(session.Id, q.Id, AnswerStatus.Scored, DateTime.UtcNow, DateTime.UtcNow);
        t.Db.AddRange(session, q, crit, a);
        await t.Db.SaveChangesAsync();
        t.Db.AnswerScores.Add(new Isas.InterviewService.Entities.AnswerScore
        {
            Id = Guid.NewGuid(), AnswerId = a.Id, CriterionId = crit.Id,
            AttemptNo = 1, Score = 4m, Reasoning = "ok", RubricVersion = 1, CreatedAt = DateTime.UtcNow
        });
        await t.Db.SaveChangesAsync();

        var reservation = new Mock<ICreditReservationClient>();
        var svc = new PracticeService(
            t.Db, new Mock<IStorageService>().Object, new Mock<IAiServiceQuestionGenerator>().Object,
            TestDb.Notifier(t.Db), reservation.Object, NullLogger<PracticeService>.Instance);

        await svc.SubmitSessionAsync(candidate, session.Id);

        var s = await t.NewContext().PracticeSessions.AsNoTracking().FirstAsync(x => x.Id == session.Id);
        Assert.Equal(SessionStatus.Scored, s.Status);

        using var read = t.NewContext();
        Assert.Equal(1, TestDb.OutboxCount(read, session.Id, "session.scored"));
        var scored = TestDb.ScoredOutbox(read, session.Id);
        Assert.Equal(candidate, scored!.CandidateId);
        Assert.Equal(80m, scored.TotalScore);   // 4/5 = 80%
    }

    // I2: câu chưa trả lời → Skipped, NHƯNG answer đang chấm (Uploaded) chưa xong → buổi giữ Scoring
    // (chờ chấm nốt) — Skipped không "ép" đóng buổi khi còn answer thật chưa xong.
    [Fact]
    public async Task Submit_UnansweredQuestion_WithPendingAnswer_MarksSkipped_StaysScoring()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var session = TestDb.Session(candidate, SessionStatus.InProgress);
        var q1 = TestDb.Question(session.Id, 1);
        var q2 = TestDb.Question(session.Id, 2);
        // q1 đang chờ chấm (Uploaded), q2 chưa trả lời.
        var a1 = TestDb.Answer(session.Id, q1.Id, AnswerStatus.Uploaded, DateTime.UtcNow, DateTime.UtcNow);
        t.Db.AddRange(session, q1, q2, a1);
        await t.Db.SaveChangesAsync();

        var svc = Build(t, new Mock<IAiServiceQuestionGenerator>(), out var notifier);
        await svc.SubmitSessionAsync(candidate, session.Id);

        var s = await t.Db.PracticeSessions.AsNoTracking().FirstAsync(x => x.Id == session.Id);
        Assert.Equal(SessionStatus.Scoring, s.Status);   // còn a1 Uploaded → chưa đóng

        var a2 = await t.Db.PracticeAnswers.AsNoTracking()
            .FirstAsync(x => x.SessionId == session.Id && x.QuestionId == q2.Id);
        Assert.Equal(AnswerStatus.Skipped, a2.Status);

        notifier.Verify(n => n.NotifySessionScoredAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // I2: CreateCampaignSession nhận ExpiresAt (hạn nhận bài B2B) → set session.Deadline.
    [Fact]
    public async Task CreateCampaignSession_WithExpiresAt_SetsDeadline()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var expires = DateTime.UtcNow.AddHours(2);

        var svc = Build(t, new Mock<IAiServiceQuestionGenerator>());
        var req = new CreateCampaignSessionRequest(
            Guid.NewGuid(), Guid.NewGuid(), JobCategory.BE,
            Questions: new[] { "Q1" },
            Criteria: new[] { new CampaignCriterionInput("Technical depth", null, 1.0m, 5) },
            ExpiresAt: expires);

        var res = await svc.CreateCampaignSessionAsync(candidate, req);

        var s = await t.Db.PracticeSessions.AsNoTracking().FirstAsync(x => x.Id == res.Id);
        Assert.NotNull(s.Deadline);
        Assert.True(Math.Abs((s.Deadline!.Value - expires).TotalSeconds) < 2);
    }

    // I2: ExpiresAt null (không truyền) → Deadline null (B2C hoặc B2B chưa cấu hình hạn nhận bài).
    [Fact]
    public async Task CreateCampaignSession_NoExpiresAt_DeadlineNull()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();

        var svc = Build(t, new Mock<IAiServiceQuestionGenerator>());
        var req = new CreateCampaignSessionRequest(
            Guid.NewGuid(), Guid.NewGuid(), JobCategory.BE,
            Questions: new[] { "Q1" },
            Criteria: new[] { new CampaignCriterionInput("Technical depth", null, 1.0m, 5) });

        var res = await svc.CreateCampaignSessionAsync(candidate, req);

        var s = await t.Db.PracticeSessions.AsNoTracking().FirstAsync(x => x.Id == res.Id);
        Assert.Null(s.Deadline);
    }
}
