using Isas.InterviewService.DTOs;
using Isas.InterviewService.Enums;
using Isas.InterviewService.Models;
using Isas.InterviewService.Services;
using Isas.InterviewService.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace Isas.InterviewService.Tests;

/// <summary>
/// Ứng viên B2C tự chọn CHẾ ĐỘ (có đào sâu / đúng số câu đã chọn) và ĐỘ SÂU (1..3 câu đào sâu mỗi
/// câu gốc). Trước đây cả hai chỉ đến từ cấu hình server, wizard không chạm được.
///
/// <para>Ba bất biến mà bộ test này tồn tại để giữ, xếp theo mức thiệt hại nếu vỡ:</para>
/// <list type="number">
///   <item>Giá trị sai phải bị từ chối <b>TRƯỚC</b> <c>ReserveAsync</c> — 400 sau reserve nghĩa là ứng
///     viên mất 1 credit vì gõ sai một con số (PAY-5).</item>
///   <item>Ứng viên <b>KHÔNG</b> được chọn <c>0</c>: <c>0</c> không phải "tắt đào sâu" mà là bộ chọn
///     chế độ engine, và nó lật cả nghĩa của <c>MaxFollowUps</c>.</item>
///   <item>Ứng viên <b>KHÔNG</b> tự bật được adaptive khi admin/gói đã tắt — cấu hình là trần, nếu
///     không thì một ô chọn trên wizard vô hiệu hoá được cả kill-switch vận hành.</item>
/// </list>
/// </summary>
public class AdaptiveDepthChoiceTests
{
    private static PracticeService Build(
        TestDb t, Mock<IAiServiceQuestionGenerator> gen, AdaptiveOptions adaptive,
        out Mock<ICreditReservationClient> reservation)
    {
        reservation = new Mock<ICreditReservationClient>();
        reservation
            .Setup(r => r.ReserveAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CreditReservationResult(Guid.NewGuid(), 1));

        return new PracticeService(
            t.Db, new Mock<IStorageService>().Object, gen.Object,
            new Mock<ISessionScoringNotifier>().Object, reservation.Object,
            NullLogger<PracticeService>.Instance, Options.Create(adaptive));
    }

    /// <summary>
    /// Generator trả về ĐÚNG số câu được xin, và ghi lại con số đó.
    ///
    /// <para>Trả cứng một số lớn thì nhánh KHÔNG adaptive — nhánh không có <c>Take</c> vì đã xin đúng
    /// số cần — sẽ giữ nguyên cả đống câu thừa, và test hoá ra đang đo cái mock chứ không đo code.
    /// AIService thật trả đúng số đã xin, mock phải hành xử như vậy.</para>
    /// </summary>
    private static Mock<IAiServiceQuestionGenerator> Generator(Action<int?> onCount)
    {
        var gen = new Mock<IAiServiceQuestionGenerator>();
        gen.Setup(g => g.GenerateQuestionsAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<IReadOnlyList<string>?>(), It.IsAny<int?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string?, string?, IReadOnlyList<string>?, int?, string, CancellationToken>(
                (_, _, _, _, count, _, _) => onCount(count))
            .ReturnsAsync((string _, string? _, string? _, IReadOnlyList<string>? _, int? count, string _, CancellationToken _) =>
                Enumerable.Range(1, count is > 0 ? count.Value : 5)
                    .Select(i => new GeneratedQuestion { Content = $"Q{i}" }).ToList());
        return gen;
    }

    private static AdaptiveOptions Adaptive(int maxDeep = 3, bool enabled = true) => new()
    {
        Enabled = enabled, SeedCount = 5, MaxQuestions = 20, MaxFollowUps = 3, MaxDeepPerQuestion = maxDeep
    };

    // ── Không chọn ⇒ y hệt hôm nay ────────────────────────────────────────────────────────────
    //
    // Đây là bất biến giữ cho toàn bộ test cũ còn là lưới an toàn thật: nếu bỏ trường mới ra khỏi
    // request mà hành vi đổi, nghĩa là mọi test đang khoá `ResolveSessionSettings` vừa bị đổi tiền đề
    // dưới chân mà không ai khai báo.
    [Fact]
    public async Task KhongChon_GiuNguyenCauHinhServer()
    {
        using var t = new TestDb();
        var svc = Build(t, Generator(_ => { }), Adaptive(), out _);

        var res = await svc.CreateSessionAsync(
            Guid.NewGuid(), new CreatePracticeSessionRequest(null, null, JobCategory.BE));

        var s = await t.Db.PracticeSessions.AsNoTracking().FirstAsync(x => x.Id == res.Id);
        Assert.True(s.AdaptiveEnabled);
        Assert.Equal(3, s.MaxDeepPerQuestion);
        Assert.Equal(0, s.MaxFollowUps);   // chế độ chuỗi ép trần-theo-buổi về 0 (INT-17b)
    }

    // ── Chọn độ sâu ⇒ đóng dấu VÀ đổi số câu gốc xin AI ───────────────────────────────────────
    //
    // Assert cả hai vế có chủ đích. Chỉ assert giá trị đóng dấu thì một hồi quy làm `ComputeSeedCount`
    // mất liên hệ với độ sâu sẽ không test nào kêu — buổi vẫn ghi "độ sâu 1" mà hình dạng thật không đổi.
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task ChonDoSau_DongDauLenSession_VaDoiSoCauGocXinAI(int chosen)
    {
        using var t = new TestDb();
        int? requested = null;
        var svc = Build(t, Generator(c => requested = c), Adaptive(), out _);

        var res = await svc.CreateSessionAsync(Guid.NewGuid(), new CreatePracticeSessionRequest(
            null, null, JobCategory.BE, QuestionCount: 12, MaxDeepPerQuestion: chosen));

        var s = await t.Db.PracticeSessions.AsNoTracking().FirstAsync(x => x.Id == res.Id);
        Assert.Equal(chosen, s.MaxDeepPerQuestion);
        Assert.Equal(0, s.MaxFollowUps);

        // Số câu gốc = ceil(12 / (1 + độ sâu)), kẹp bởi SeedCount=5 và sàn theo số tiêu chí nội dung.
        Assert.NotNull(requested);
        Assert.InRange(requested!.Value, 1, 12);
    }

    // ── Ngoài miền ⇒ 400 và KHÔNG BAO GIỜ reserve ─────────────────────────────────────────────
    //
    // Ca `0` là ca quan trọng nhất: nó khoá việc ứng viên KHÔNG đổi được chế độ engine của buổi thi.
    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(-1)]
    [InlineData(100_000)]
    public async Task DoSauNgoaiMien_Bi400_VaKhongBaoGioReserve(int bad)
    {
        using var t = new TestDb();
        var svc = Build(t, Generator(_ => { }), Adaptive(), out var reservation);

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.CreateSessionAsync(
            Guid.NewGuid(), new CreatePracticeSessionRequest(
                null, null, JobCategory.BE, MaxDeepPerQuestion: bad)));

        reservation.Verify(
            r => r.ReserveAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Never);
        Assert.Equal(0, await t.Db.PracticeSessions.CountAsync());
    }

    // ── Vượt trần CẤU HÌNH (dù vẫn dưới trần hệ thống) ⇒ 400, và options báo ĐÚNG trần đó ─────
    //
    // Đây là bất biến "số báo cho UI == số dùng để từ chối". Hai chỗ tính khác nhau chính là bug: repo
    // đã dính đúng lớp đó một lần với `questionCount` (UI cho bấm, server trả 400, người dùng không
    // hiểu mình sai ở đâu).
    [Fact]
    public async Task VuotTranCauHinh_Bi400_VaOptionsBaoDungTranDo()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var svc = Build(t, Generator(_ => { }), Adaptive(maxDeep: 2), out var reservation);

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.CreateSessionAsync(
            candidate, new CreatePracticeSessionRequest(null, null, JobCategory.BE, MaxDeepPerQuestion: 3)));
        reservation.Verify(
            r => r.ReserveAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Never);

        var options = await svc.GetSessionOptionsAsync(candidate, "BE");
        Assert.Equal(2, options.MaxDeepPerQuestionMax);
        Assert.Equal(1, options.MaxDeepPerQuestionMin);
    }

    // ── Kill-switch cấu hình = 0 ⇒ KẸP LẶNG, không ném ────────────────────────────────────────
    //
    // Bất đối xứng có chủ đích với ca trên. `0` không phải "người dùng gõ sai" mà là trạng thái vận
    // hành. Ném ở đây biến một cần gạt giảm tải thành sự cố: mọi tab FE đang mở còn cache options báo
    // max = 3 sẽ nhận 400 khi bấm "Bắt đầu", và câu lỗi thì vô nghĩa ("phải trong khoảng 1..0").
    [Fact]
    public async Task CauHinhTat_KhongNem_MaKepVe0()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var svc = Build(t, Generator(_ => { }), Adaptive(maxDeep: 0), out _);

        var res = await svc.CreateSessionAsync(candidate, new CreatePracticeSessionRequest(
            null, null, JobCategory.BE, QuestionCount: 5, MaxDeepPerQuestion: 3));

        var s = await t.Db.PracticeSessions.AsNoTracking().FirstAsync(x => x.Id == res.Id);
        Assert.Equal(0, s.MaxDeepPerQuestion);

        var options = await svc.GetSessionOptionsAsync(candidate, "BE");
        Assert.Equal(0, options.MaxDeepPerQuestionMax);
        Assert.Equal(0, options.MaxDeepPerQuestionMin);   // 0/0 ⇒ UI ẩn hẳn ô chọn
    }

    // ── Chế độ "đúng số câu đã chọn" ⇒ buổi TĨNH, đủ N câu, không câu chèn ───────────────────
    [Fact]
    public async Task TatDaoSau_NhanDungSoCauDaChon_KhongCauChen()
    {
        using var t = new TestDb();
        int? requested = null;
        var svc = Build(t, Generator(c => requested = c), Adaptive(), out _);

        var res = await svc.CreateSessionAsync(Guid.NewGuid(), new CreatePracticeSessionRequest(
            null, null, JobCategory.BE, QuestionCount: 6, AdaptiveEnabled: false));

        var s = await t.Db.PracticeSessions.AsNoTracking().FirstAsync(x => x.Id == res.Id);
        Assert.False(s.AdaptiveEnabled);
        Assert.Equal(0, s.MaxDeepPerQuestion);
        Assert.Equal(0, s.MaxFollowUps);

        // Xin AI đúng 6 câu và giữ đúng 6 — không chia ngân sách cho khe đào sâu nào.
        Assert.Equal(6, requested);
        Assert.Equal(6, await t.Db.PracticeQuestions.CountAsync(q => q.SessionId == res.Id));
    }

    // ── Ứng viên KHÔNG tự bật được adaptive khi cấu hình đã tắt ───────────────────────────────
    //
    // Ngữ nghĩa CHỈ-CHO-TỪ-CHỐI. Vỡ bất biến này thì `Adaptive:Enabled=false` — cần gạt tắt tính năng
    // toàn hệ — bị một ô chọn trên wizard vô hiệu hoá.
    [Fact]
    public async Task GuiTrue_KhiCauHinhTat_VanTat()
    {
        using var t = new TestDb();
        var svc = Build(t, Generator(_ => { }), Adaptive(enabled: false), out _);

        var res = await svc.CreateSessionAsync(Guid.NewGuid(), new CreatePracticeSessionRequest(
            null, null, JobCategory.BE, QuestionCount: 5, AdaptiveEnabled: true));

        var s = await t.Db.PracticeSessions.AsNoTracking().FirstAsync(x => x.Id == res.Id);
        Assert.False(s.AdaptiveEnabled);
        Assert.Equal(0, s.MaxDeepPerQuestion);
    }

    // ── Trường cũ `MaxDeepPerQuestion` của options giữ nguyên NGHĨA ───────────────────────────
    //
    // Nó là mức MẶC ĐỊNH khi client không chọn, KHÔNG phải trần. Chặn việc ai đó sau này lặng lẽ đổi
    // nghĩa nó thành trần rồi FE hiểu nhầm mà không có lỗi nào nổ.
    [Fact]
    public async Task OptionsGiuNguyenNghia_MaxDeepPerQuestionLaMucMacDinh()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var svc = Build(t, Generator(_ => { }), Adaptive(maxDeep: 2), out _);

        var options = await svc.GetSessionOptionsAsync(candidate, "BE");
        Assert.Equal(2, options.MaxDeepPerQuestion);       // mức mặc định
        Assert.Equal(2, options.MaxDeepPerQuestionMax);    // trần — trùng số ở cấu hình này, khác nghĩa
        Assert.True(options.AdaptiveEnabled);
    }
}
