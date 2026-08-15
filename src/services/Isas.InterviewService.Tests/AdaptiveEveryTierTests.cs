using Isas.InterviewService.DTOs;
using Isas.InterviewService.Enums;
using Isas.InterviewService.Models;
using Isas.InterviewService.Services;
using Isas.InterviewService.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace Isas.InterviewService.Tests;

/// <summary>
/// ADAPTIVE Ở MỌI TIER — mọi buổi tiêu đúng 1 credit bất kể gói (PAY-1/BC-1), nên không gói nào được
/// lấy mất engine phỏng vấn. <c>Adaptive:Enabled</c> là SÀN; entitlement chỉ được CỘNG.
///
/// Vì sao file này tồn tại: trước nó, <c>grep Tiering src/services/Isas.InterviewService.Tests</c> ra
/// RỖNG — toàn bộ nhánh tiering của <c>ResolveSessionSettings</c> (nhánh quyết định buổi có adaptive
/// hay không, và trần bao nhiêu câu) chưa từng có test nào chạy qua. Bật <c>Tiering:Enabled</c> trên
/// prod là bước cấu hình, không phải deploy ⇒ nhánh không test được bật bằng một biến môi trường.
/// </summary>
public sealed class AdaptiveEveryTierTests
{
    private sealed class StubEntitlements(EntitlementSnapshot value) : IEntitlementClient
    {
        public Task<EntitlementSnapshot> ResolveUserAsync(Guid candidateId, CancellationToken ct = default)
            => Task.FromResult(value);
    }

    private static IConfiguration Config(bool tiering) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Tiering:Enabled"] = tiering ? "true" : "false"
        }).Build();

    private static PracticeService Build(
        TestDb t, AdaptiveOptions adaptive, EntitlementSnapshot entitlement, bool tiering = true)
    {
        var gen = new Mock<IAiServiceQuestionGenerator>();
        // Đủ cả 2 overload: rubric rỗng (không tiêu chí nội dung) rơi xuống overload 4 tham số.
        gen.Setup(g => g.GenerateQuestionsAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Enumerable.Range(1, 5).Select(i => new GeneratedQuestion { Content = $"Q{i}" }).ToList());
        gen.Setup(g => g.GenerateQuestionsAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<IReadOnlyList<string>?>(), It.IsAny<int?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Enumerable.Range(1, 5).Select(i => new GeneratedQuestion { Content = $"Q{i}" }).ToList());

        var reservation = new Mock<ICreditReservationClient>();
        reservation.Setup(r => r.ReserveAsync(
                It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CreditReservationResult(Guid.NewGuid(), 1));

        return new PracticeService(
            t.Db, new Mock<IStorageService>().Object, gen.Object,
            new Mock<ISessionScoringNotifier>().Object, reservation.Object,
            NullLogger<PracticeService>.Instance, Options.Create(adaptive),
            config: Config(tiering), entitlements: new StubEntitlements(entitlement));
    }

    private static EntitlementSnapshot Tier(
        string code, bool adaptive, int maxQuestions = 0, int maxFollowUps = 0) =>
        new("resolved", code, 0, adaptive, maxQuestions, maxFollowUps, false, 1, false, false, false);

    private static AdaptiveOptions ChainMode() =>
        new() { Enabled = true, SeedCount = 5, MaxQuestions = 20, MaxFollowUps = 3, MaxDeepPerQuestion = 3 };

    /// <summary>
    /// Gói KHÔNG bật adaptive (gói free cũ, hoặc plan admin tạo mà quên tick — DTO mặc định false)
    /// vẫn phải nhận buổi adaptive khi engine đang bật. Đây là ca chính của luật.
    /// </summary>
    [Fact]
    public async Task GoiKhongBatAdaptive_EngineDangBat_BuoiVanAdaptive()
    {
        using var t = new TestDb();
        var svc = Build(t, ChainMode(), Tier("free", adaptive: false));

        var res = await svc.CreateSessionAsync(
            Guid.NewGuid(), new CreatePracticeSessionRequest(null, null, JobCategory.BE));

        var s = await t.Db.PracticeSessions.AsNoTracking().SingleAsync(x => x.Id == res.Id);
        Assert.True(s.AdaptiveEnabled);
        Assert.Equal(3, s.MaxDeepPerQuestion);
        // Gói không khai trần (0) ⇒ trần buổi lấy từ cấu hình, KHÔNG phải 0 câu.
        Assert.Equal(20, s.MaxQuestions);
        Assert.Equal("free", s.TierCode);       // vẫn đóng dấu đúng gói đã resolve
    }

    /// <summary>
    /// Payment sập → <see cref="EntitlementSnapshot.Free"/>. Ứng viên ĐÃ bị trừ 1 credit ở bước reserve
    /// ngay trước đó, nên hạ cấp im lặng xuống luồng tĩnh là lấy tiền rồi giao hàng khác.
    /// </summary>
    [Fact]
    public async Task PaymentSap_FallbackFree_KhongMatAdaptive_VaKhongVeKhongCau()
    {
        using var t = new TestDb();
        var svc = Build(t, ChainMode(), EntitlementSnapshot.Free);

        var res = await svc.CreateSessionAsync(
            Guid.NewGuid(), new CreatePracticeSessionRequest(null, null, JobCategory.BE));

        var s = await t.Db.PracticeSessions.AsNoTracking().SingleAsync(x => x.Id == res.Id);
        Assert.True(s.AdaptiveEnabled);
        Assert.Equal(20, s.MaxQuestions);   // hành vi cũ: `Math.Clamp(x, 0, 0)` cho ra 0 = buổi không trần-hợp-lệ
    }

    /// <summary>Trần DƯƠNG của gói vẫn thắng — đây là cần gạt kiếm tiền còn lại, không được nới hộ.</summary>
    [Fact]
    public async Task GoiCapTranDuong_TranGoiVanThang()
    {
        using var t = new TestDb();
        var svc = Build(t, ChainMode(), Tier("plus", adaptive: true, maxQuestions: 8));
        var candidate = Guid.NewGuid();

        var options = await svc.GetSessionOptionsAsync(candidate, "BE");
        Assert.Equal(8, options.QuestionCountMax);
        Assert.Equal(8, options.DefaultQuestionCount);

        // Vượt trần gói → 400 TRƯỚC reserve (PAY-5), không phải cắt im lặng.
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.CreateSessionAsync(
            candidate, new CreatePracticeSessionRequest(null, null, JobCategory.BE, QuestionCount: 12)));

        var res = await svc.CreateSessionAsync(
            candidate, new CreatePracticeSessionRequest(null, null, JobCategory.BE));
        var s = await t.Db.PracticeSessions.AsNoTracking().SingleAsync(x => x.Id == res.Id);
        Assert.Equal(8, s.MaxQuestions);
    }

    /// <summary>
    /// Chiều CỘNG vẫn còn: gói bật adaptive trong khi cờ rollout chung còn tắt ⇒ tier đó CÓ adaptive.
    /// Đây là thứ giữ cho <c>Plan.AdaptiveEnabled</c> không thành cột chết sau khi bỏ vế TRỪ.
    /// </summary>
    [Fact]
    public async Task CoRolloutTat_NhungGoiBatAdaptive_VanCoAdaptive()
    {
        using var t = new TestDb();
        var svc = Build(t,
            new AdaptiveOptions { Enabled = false, SeedCount = 5, MaxQuestions = 20, MaxDeepPerQuestion = 3 },
            Tier("pro", adaptive: true, maxQuestions: 12, maxFollowUps: 5));

        var res = await svc.CreateSessionAsync(
            Guid.NewGuid(), new CreatePracticeSessionRequest(null, null, JobCategory.BE));

        var s = await t.Db.PracticeSessions.AsNoTracking().SingleAsync(x => x.Id == res.Id);
        Assert.True(s.AdaptiveEnabled);
        Assert.Equal(12, s.MaxQuestions);
    }

    /// <summary>
    /// <c>Tiering:Enabled=false</c> (trạng thái prod hiện tại) KHÔNG được đổi hành vi: engine tắt thì
    /// buổi vẫn là luồng tĩnh, dù snapshot Free nay mang <c>AdaptiveEnabled = true</c>.
    /// </summary>
    [Fact]
    public async Task TieringTat_EngineTat_VanLaLuongTinh()
    {
        using var t = new TestDb();
        var svc = Build(t, new AdaptiveOptions { Enabled = false }, EntitlementSnapshot.Free, tiering: false);

        var res = await svc.CreateSessionAsync(
            Guid.NewGuid(), new CreatePracticeSessionRequest(null, null, JobCategory.BE));

        var s = await t.Db.PracticeSessions.AsNoTracking().SingleAsync(x => x.Id == res.Id);
        Assert.False(s.AdaptiveEnabled);
        Assert.Equal(0, s.MaxQuestions);          // 0 = không trần cứng, số câu do AIService quyết
        Assert.Equal(0, s.MaxDeepPerQuestion);
        Assert.Equal("legacy", s.EntitlementSource);
    }

    /// <summary>
    /// `Adaptive:MaxQuestions = 0` (cấu hình khai "không trần cứng") phải giữ nguyên nghĩa cũ: lựa chọn
    /// của ứng viên đi thẳng vào session, KHÔNG bị `Math.Clamp(x, 0, 0)` xoá về 0.
    /// </summary>
    [Fact]
    public async Task CauHinhKhongTranCung_GiuNguyenLuaChonCuaUngVien()
    {
        using var t = new TestDb();
        var svc = Build(t,
            new AdaptiveOptions { Enabled = true, SeedCount = 5, MaxQuestions = 0, MaxDeepPerQuestion = 3 },
            Tier("free", adaptive: false));

        var res = await svc.CreateSessionAsync(
            Guid.NewGuid(), new CreatePracticeSessionRequest(null, null, JobCategory.BE, QuestionCount: 9));

        var s = await t.Db.PracticeSessions.AsNoTracking().SingleAsync(x => x.Id == res.Id);
        Assert.Equal(9, s.MaxQuestions);
    }
}
