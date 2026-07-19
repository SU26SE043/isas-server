using Isas.InterviewService.DTOs;
using Isas.InterviewService.Entities;
using Isas.InterviewService.Enums;
using Isas.InterviewService.Services;
using Isas.InterviewService.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Isas.InterviewService.Tests;

/// <summary>
/// BK23 — đóng dấu <c>answer_scores.prompt_version</c> lúc chấm.
///
/// <para>F21 thêm cột + <c>GetPromptVersionStampAsync()</c> nhưng KHÔNG đấu dây writer nào, nên
/// cột NULL trên mọi dòng: tính năng "cho admin sửa prompt chấm" chạy mà không ghi lại prompt nào
/// tạo ra điểm nào. Hệ quả là sau một lần sửa, điểm cũ và điểm mới không còn so sánh được nhưng
/// KHÔNG có gì trong dữ liệu nói ra điều đó — trong khi điểm đang dùng để xếp hạng ứng viên
/// (CAMP-10/E4) và đo cải thiện theo thời gian (BC15).</para>
///
/// <para><b>Nguồn con dấu = AIService</b>, gửi kèm callback. Không phải Interview tự đọc DB lúc
/// lưu: AIService cache mảnh prompt theo TTL và cố ý fail-open về cache CŨ khi registry lỗi (F21),
/// còn chấm thì bất đồng bộ qua RabbitMQ + republish được sau hàng giờ ⇒ "phiên bản trong DB lúc
/// callback về" thường xuyên KHÁC "phiên bản đã thực sự chấm". Con dấu sai tệ hơn NULL.</para>
///
/// <para>⚠ Bất biến an toàn quan trọng nhất ở đây: con dấu là cột KIỂM TOÁN, thiếu/hỏng nó
/// TUYỆT ĐỐI không được làm answer <c>Failed</c> — Failed = mất 1 credit (PAY-13). Mẫu đã có ở
/// F13/F11: nhận null, bỏ qua, chấm tiếp.</para>
/// </summary>
public class PromptVersionStampBk23Tests
{
    private static AnswerService Build(TestDb t, int selfConsistencyN = 1)
    {
        var notifier = new Mock<ISessionScoringNotifier>();
        notifier
            .Setup(n => n.NotifySessionScoredAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        return new AnswerService(
            t.Db, new Mock<IStorageService>().Object, new Mock<IScoringJobPublisher>().Object,
            notifier.Object, TestDb.ScoringOpts(selfConsistencyN: selfConsistencyN),
            NullLogger<AnswerService>.Instance);
    }

    private static AnswerScoreCallbackRequest Callback(
        Guid criterionId, int? promptVersion, int attemptNo = 1, decimal score = 3m) =>
        new()
        {
            Transcript = "trả lời",
            RubricVersion = 1,
            AttemptNo = attemptNo,
            PromptVersion = promptVersion,
            Scores = { new ScoreItemDto { CriterionId = criterionId, Score = score, Reasoning = "ok" } }
        };

    private static async Task<(RubricCriterion c, PracticeAnswer a)> SeedAsync(TestDb t)
    {
        var session = TestDb.Session(Guid.NewGuid(), SessionStatus.Scoring);
        var q = TestDb.Question(session.Id);
        var crit = TestDb.Criterion(session.JobCategory);
        var answer = TestDb.Answer(session.Id, q.Id, AnswerStatus.Scoring, DateTime.UtcNow, DateTime.UtcNow);
        t.Db.AddRange(session, q, crit, answer);
        await t.Db.SaveChangesAsync();
        return (crit, answer);
    }

    // ── Bất biến chính: chấm xong thì dòng điểm CÓ con dấu ────────────────────────────────

    [Fact]
    public async Task ChamXong_DongDauPromptVersion_KhongConNull()
    {
        // Đây là bất biến mà cả BK23 tồn tại để có. Trước fix, dòng này NULL vĩnh viễn.
        using var t = new TestDb();
        var (crit, answer) = await SeedAsync(t);

        await Build(t).SaveResultAsync(answer.Id, Callback(crit.Id, promptVersion: 12));

        var saved = await t.Db.AnswerScores.AsNoTracking().FirstAsync(s => s.AnswerId == answer.Id);
        Assert.Equal(12, saved.PromptVersion);
    }

    [Fact]
    public async Task ConDau_KhopDungBanPromptWorkerGui_KhongPhaiSoKhac()
    {
        // Con dấu phải là con số worker gửi, không phải thứ Interview tự bịa/tự đọc ở đâu khác.
        // Nếu ai đó sau này "tối ưu" thành đọc GetPromptVersionStampAsync() lúc lưu, test này đỏ:
        // bảng prompt_templates ở đây RỖNG (stamp DB = 0) trong khi lượt chấm mang version 41.
        using var t = new TestDb();
        var (crit, answer) = await SeedAsync(t);
        Assert.Empty(await t.Db.PromptTemplates.AsNoTracking().ToListAsync());

        await Build(t).SaveResultAsync(answer.Id, Callback(crit.Id, promptVersion: 41));

        var saved = await t.Db.AnswerScores.AsNoTracking().FirstAsync(s => s.AnswerId == answer.Id);
        Assert.Equal(41, saved.PromptVersion);
    }

    [Fact]
    public async Task ConDau0_LuuLa0_KhongBiEpThanhNull()
    {
        // 0 = "chấm bằng bản mặc định thuần" — là THÔNG TIN, khác hẳn null = "không biết".
        // Gộp hai ca này là mất đúng thứ cần để biết có so sánh được hay không (interview.md §F21).
        using var t = new TestDb();
        var (crit, answer) = await SeedAsync(t);

        await Build(t).SaveResultAsync(answer.Id, Callback(crit.Id, promptVersion: 0));

        var saved = await t.Db.AnswerScores.AsNoTracking().FirstAsync(s => s.AnswerId == answer.Id);
        Assert.Equal(0, saved.PromptVersion);
    }

    // ── Bất biến an toàn: thiếu/hỏng con dấu KHÔNG được làm hỏng lượt chấm (PAY-13) ────────

    [Fact]
    public async Task WorkerCu_KhongGuiConDau_VanChamXong_ConDauNull()
    {
        // Worker/image CŨ (deploy lệch nhịp .NET là chuyện thường ở đây) không gửi field.
        // Phải: điểm vẫn lưu, answer vẫn Scored, con dấu để NULL — KHÔNG Failed, KHÔNG mất credit.
        using var t = new TestDb();
        var (crit, answer) = await SeedAsync(t);

        await Build(t).SaveResultAsync(answer.Id, Callback(crit.Id, promptVersion: null));

        var saved = await t.Db.AnswerScores.AsNoTracking().FirstAsync(s => s.AnswerId == answer.Id);
        Assert.Null(saved.PromptVersion);
        Assert.Equal(3m, saved.Score);

        var a = await t.Db.PracticeAnswers.AsNoTracking().FirstAsync(x => x.Id == answer.Id);
        Assert.Equal(AnswerStatus.Scored, a.Status);
    }

    [Fact]
    public async Task ConDauAm_LuuNull_VanChamXong_KhongFailed()
    {
        // version có CHECK `> 0` ở tầng DB ⇒ tổng các mảnh active không bao giờ âm ⇒ số âm chỉ có
        // thể là worker hỏng/lệch hợp đồng. Lưu rác vào cột kiểm toán tệ hơn để trống. Nhưng
        // TUYỆT ĐỐI không ném: biến cột audit thành đường làm answer Failed = đường mất credit.
        using var t = new TestDb();
        var (crit, answer) = await SeedAsync(t);

        await Build(t).SaveResultAsync(answer.Id, Callback(crit.Id, promptVersion: -5));

        var saved = await t.Db.AnswerScores.AsNoTracking().FirstAsync(s => s.AnswerId == answer.Id);
        Assert.Null(saved.PromptVersion);

        var a = await t.Db.PracticeAnswers.AsNoTracking().FirstAsync(x => x.Id == answer.Id);
        Assert.Equal(AnswerStatus.Scored, a.Status);
    }

    // ── E10 self-consistency: con dấu là thuộc tính của ATTEMPT, không phải của answer ──────

    [Fact]
    public async Task NhieuAttempt_MoiAttemptGiuConDauCuaChinhNo()
    {
        // 1 answer có N attempt = N lượt gọi AI riêng, mỗi lượt refresh registry riêng. Nên lưu
        // per-row: prompt đổi giữa chừng là THẤY ĐƯỢC, không bị một giá trị "đại diện" nuốt mất.
        using var t = new TestDb();
        var (crit, answer) = await SeedAsync(t);
        var svc = Build(t, selfConsistencyN: 2);

        await svc.SaveResultAsync(answer.Id, Callback(crit.Id, promptVersion: 5, attemptNo: 1));
        await svc.SaveResultAsync(answer.Id, Callback(crit.Id, promptVersion: 9, attemptNo: 2));

        var rows = await t.Db.AnswerScores.AsNoTracking()
            .Where(s => s.AnswerId == answer.Id).OrderBy(s => s.AttemptNo).ToListAsync();
        Assert.Equal([5, 9], rows.Select(r => r.PromptVersion).ToArray());
    }

    [Fact]
    public async Task AttemptTronHaiPhienBanPrompt_GanCoSoiLai()
    {
        // Điểm chốt = median GIỮA các attempt. Trộn hai prompt ⇒ median lấy trên hai thước đo
        // khác nhau: con số vẫn ra, vẫn trông bình thường, không gì nói rằng nó vô nghĩa.
        // Cờ soi lại (KHÔNG loại attempt, KHÔNG Failed) là mức can thiệp đúng.
        using var t = new TestDb();
        var (crit, answer) = await SeedAsync(t);
        var svc = Build(t, selfConsistencyN: 2);

        await svc.SaveResultAsync(answer.Id, Callback(crit.Id, promptVersion: 5, attemptNo: 1));
        await svc.SaveResultAsync(answer.Id, Callback(crit.Id, promptVersion: 9, attemptNo: 2));

        var a = await t.Db.PracticeAnswers.AsNoTracking().FirstAsync(x => x.Id == answer.Id);
        Assert.True(a.NeedsReview);
        Assert.Equal(AnswerStatus.Scored, a.Status);   // cờ, không phải hỏng
    }

    [Fact]
    public async Task AttemptCungMotPhienBanPrompt_KhongGanCo()
    {
        // Vế ÂM: cùng thước đo thì KHÔNG được gắn cờ, nếu không cờ kêu suốt và thành vô dụng.
        // Điểm giữ giống nhau để loại nhiễu từ spread (E10) và reasoning ngắn (E11).
        using var t = new TestDb();
        var (crit, answer) = await SeedAsync(t);
        var svc = Build(t, selfConsistencyN: 2);

        await svc.SaveResultAsync(answer.Id, Callback(crit.Id, promptVersion: 5, attemptNo: 1));
        await svc.SaveResultAsync(answer.Id, Callback(crit.Id, promptVersion: 5, attemptNo: 2));

        var a = await t.Db.PracticeAnswers.AsNoTracking().FirstAsync(x => x.Id == answer.Id);
        Assert.False(a.NeedsReview);
    }

    [Fact]
    public async Task AttemptKhuyetConDau_KhongSuyRaLaKhacThuocDo()
    {
        // null = "KHÔNG BIẾT chấm bằng prompt nào" (worker cũ). Suy ra "khác thước đo" từ
        // "không biết" là bịa — và sẽ làm mọi answer chấm bởi worker hỗn hợp bị gắn cờ oan,
        // đúng kiểu nhiễu khiến người ta tắt cờ đi rồi mất luôn tín hiệu thật.
        using var t = new TestDb();
        var (crit, answer) = await SeedAsync(t);
        var svc = Build(t, selfConsistencyN: 2);

        await svc.SaveResultAsync(answer.Id, Callback(crit.Id, promptVersion: 5, attemptNo: 1));
        await svc.SaveResultAsync(answer.Id, Callback(crit.Id, promptVersion: null, attemptNo: 2));

        var a = await t.Db.PracticeAnswers.AsNoTracking().FirstAsync(x => x.Id == answer.Id);
        Assert.False(a.NeedsReview);
    }

    // ── Idempotency: retry cùng attempt phải THAY con dấu, không nhân đôi dòng ─────────────

    [Fact]
    public async Task RetryCungAttempt_ThayConDau_KhongNhanDoiDong()
    {
        // Worker retry gửi lại cùng attempt+rubricVersion (có thể sau khi prompt đã đổi).
        // Dòng cũ bị xoá rồi ghi lại ⇒ con dấu phải là của lượt chấm MỚI NHẤT, và chỉ 1 dòng.
        using var t = new TestDb();
        var (crit, answer) = await SeedAsync(t);
        var svc = Build(t);

        await svc.SaveResultAsync(answer.Id, Callback(crit.Id, promptVersion: 5));
        await svc.SaveResultAsync(answer.Id, Callback(crit.Id, promptVersion: 8));

        var rows = await t.Db.AnswerScores.AsNoTracking()
            .Where(s => s.AnswerId == answer.Id).ToListAsync();
        Assert.Single(rows);
        Assert.Equal(8, rows[0].PromptVersion);
    }
}
