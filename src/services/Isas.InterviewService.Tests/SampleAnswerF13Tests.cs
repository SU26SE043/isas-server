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
/// F13 (FR07) — câu trả lời MẪU chuyên nghiệp cho mỗi câu, sinh CÙNG lượt chấm.
///
/// ⚠ KHÔNG liên quan <c>RubricLevel.ExampleAnswers</c>: cái đó là anchor ĐẦU VÀO để hiệu chỉnh
/// AI lúc chấm (prompts.py), không bao giờ trả ra cho người dùng, và trên thực tế luôn rỗng vì
/// không có write path nào ghi <c>RubricLevel</c>. Cột <c>practice_answers.sample_answer</c> ở
/// đây là thứ khác hẳn: đầu RA, một bản/câu trả lời, hiển thị cho người luyện.
///
/// Phần cần khoá bằng test là quy tắc GHI (cái mà con người dễ làm sai về sau):
///   • attempt 1 (temp=0) là bản chọn → ghi đè được ⇒ retry idempotent;
///   • attempt 2..N (E10) chỉ điền khi trống ⇒ nội dung không nhảy theo attempt;
///   • payload rỗng KHÔNG xoá bản đang có ⇒ 1 lần LLM im lặng không thổi bay gợi ý hợp lệ;
///   • upload lại (INT-3) PHẢI xoá ⇒ không hiển thị gợi ý của bài đã bị thu âm đè.
/// </summary>
public class SampleAnswerF13Tests
{
    private static AnswerService Build(TestDb t, out Mock<IStorageService> storage)
    {
        storage = new Mock<IStorageService>();
        storage
            .Setup(s => s.UploadAsync(
                It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<Guid>(),
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("answer-audio/seed.webm");

        var notifier = new Mock<ISessionScoringNotifier>();
        notifier
            .Setup(n => n.NotifySessionScoredAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        return new AnswerService(
            t.Db, storage.Object, new Mock<IScoringJobPublisher>().Object, notifier.Object,
            TestDb.ScoringOpts(), NullLogger<AnswerService>.Instance);
    }

    private static AnswerScoreCallbackRequest Callback(
        Guid criterionId, string? sample, int attemptNo = 1, string transcript = "trả lời") =>
        new()
        {
            Transcript = transcript,
            RubricVersion = 1,
            AttemptNo = attemptNo,
            SampleAnswer = sample,
            Scores = { new ScoreItemDto { CriterionId = criterionId, Score = 3m, Reasoning = "ok" } }
        };

    private static async Task<(PracticeSession s, PracticeQuestion q, RubricCriterion c, PracticeAnswer a)>
        SeedAsync(TestDb t, Guid candidate)
    {
        var session = TestDb.Session(candidate, SessionStatus.Scoring);
        var q = TestDb.Question(session.Id);
        var crit = TestDb.Criterion(session.JobCategory);
        var answer = TestDb.Answer(session.Id, q.Id, AnswerStatus.Scoring, DateTime.UtcNow, DateTime.UtcNow);
        t.Db.AddRange(session, q, crit, answer);
        await t.Db.SaveChangesAsync();
        return (session, q, crit, answer);
    }

    [Fact]
    public async Task SaveResult_LuuSampleAnswer()
    {
        using var t = new TestDb();
        var (_, _, crit, answer) = await SeedAsync(t, Guid.NewGuid());
        var svc = Build(t, out _);

        await svc.SaveResultAsync(answer.Id, Callback(crit.Id, "Theo tôi, DI là kỹ thuật..."));

        var saved = await t.Db.PracticeAnswers.AsNoTracking().FirstAsync(a => a.Id == answer.Id);
        Assert.Equal("Theo tôi, DI là kỹ thuật...", saved.SampleAnswer);
    }

    [Fact]
    public async Task SaveResult_SampleRong_KhongLuu_ChamVanThanhCong()
    {
        // Worker/image CŨ không gửi field → null. Không được vì thế mà hỏng lượt chấm (PAY-13).
        using var t = new TestDb();
        var (_, _, crit, answer) = await SeedAsync(t, Guid.NewGuid());
        var svc = Build(t, out _);

        await svc.SaveResultAsync(answer.Id, Callback(crit.Id, sample: null));

        var saved = await t.Db.PracticeAnswers.AsNoTracking().Include(a => a.Scores)
            .FirstAsync(a => a.Id == answer.Id);
        Assert.Null(saved.SampleAnswer);
        Assert.Equal(AnswerStatus.Scored, saved.Status);   // vẫn chấm xong bình thường
        Assert.Single(saved.Scores);
    }

    [Fact]
    public async Task SaveResult_SampleTrangRong_KhongLuuChuoiTrang()
    {
        using var t = new TestDb();
        var (_, _, crit, answer) = await SeedAsync(t, Guid.NewGuid());
        var svc = Build(t, out _);

        await svc.SaveResultAsync(answer.Id, Callback(crit.Id, "   "));

        var saved = await t.Db.PracticeAnswers.AsNoTracking().FirstAsync(a => a.Id == answer.Id);
        Assert.Null(saved.SampleAnswer);   // KHÔNG lưu chuỗi trắng → FE khỏi hiện ô rỗng
    }

    [Fact]
    public async Task SaveResult_RetryCungAttempt1_GhiDe_Idempotent()
    {
        using var t = new TestDb();
        var (_, _, crit, answer) = await SeedAsync(t, Guid.NewGuid());
        var svc = Build(t, out _);

        await svc.SaveResultAsync(answer.Id, Callback(crit.Id, "bản 1", attemptNo: 1));
        await svc.SaveResultAsync(answer.Id, Callback(crit.Id, "bản 1 sửa", attemptNo: 1));

        var saved = await t.Db.PracticeAnswers.AsNoTracking().FirstAsync(a => a.Id == answer.Id);
        Assert.Equal("bản 1 sửa", saved.SampleAnswer);
    }

    [Fact]
    public async Task SaveResult_AttemptSau_KhongDeLenBanCuaAttempt1()
    {
        // E10 self-consistency: attempt 2..N chạy temp>0 → bản kém tin cậy hơn attempt 1 (temp=0).
        // Nếu để đè, nội dung người dùng đọc sẽ nhảy tuỳ theo attempt nào callback sau cùng.
        using var t = new TestDb();
        var (_, _, crit, answer) = await SeedAsync(t, Guid.NewGuid());
        var svc = Build(t, out _);

        await svc.SaveResultAsync(answer.Id, Callback(crit.Id, "bản attempt 1", attemptNo: 1));
        await svc.SaveResultAsync(answer.Id, Callback(crit.Id, "bản attempt 2", attemptNo: 2));

        var saved = await t.Db.PracticeAnswers.AsNoTracking().FirstAsync(a => a.Id == answer.Id);
        Assert.Equal("bản attempt 1", saved.SampleAnswer);
    }

    [Fact]
    public async Task SaveResult_AttemptSauDienVaoKhiAttempt1KhongTra()
    {
        // Cứu ca attempt 1 bị LLM bỏ field: vẫn còn cơ hội có gợi ý thay vì mất hẳn.
        using var t = new TestDb();
        var (_, _, crit, answer) = await SeedAsync(t, Guid.NewGuid());
        var svc = Build(t, out _);

        await svc.SaveResultAsync(answer.Id, Callback(crit.Id, sample: null, attemptNo: 1));
        await svc.SaveResultAsync(answer.Id, Callback(crit.Id, "bản attempt 2", attemptNo: 2));

        var saved = await t.Db.PracticeAnswers.AsNoTracking().FirstAsync(a => a.Id == answer.Id);
        Assert.Equal("bản attempt 2", saved.SampleAnswer);
    }

    [Fact]
    public async Task SaveResult_CallbackSauKhongCoSample_KhongXoaBanDangCo()
    {
        using var t = new TestDb();
        var (_, _, crit, answer) = await SeedAsync(t, Guid.NewGuid());
        var svc = Build(t, out _);

        await svc.SaveResultAsync(answer.Id, Callback(crit.Id, "bản tốt", attemptNo: 1));
        await svc.SaveResultAsync(answer.Id, Callback(crit.Id, sample: null, attemptNo: 1));

        var saved = await t.Db.PracticeAnswers.AsNoTracking().FirstAsync(a => a.Id == answer.Id);
        Assert.Equal("bản tốt", saved.SampleAnswer);
    }

    [Fact]
    public async Task UploadLai_XoaSampleAnswerCu_INT3()
    {
        // Gợi ý bám câu trả lời CŨ ("bù chỗ bạn còn thiếu"); giữ lại sau khi thu âm đè
        // = hiển thị lời khuyên cho một bài không còn tồn tại.
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var (session, q, crit, answer) = await SeedAsync(t, candidate);
        var svc = Build(t, out _);

        await svc.SaveResultAsync(answer.Id, Callback(crit.Id, "gợi ý cho bài cũ"));
        Assert.Equal("gợi ý cho bài cũ",
            (await t.Db.PracticeAnswers.AsNoTracking().FirstAsync(a => a.Id == answer.Id)).SampleAnswer);

        // Session đã Scored sau callback → mở lại để upload được (INT-5 chỉ cho Ready/InProgress).
        var s = await t.Db.PracticeSessions.FirstAsync(x => x.Id == session.Id);
        s.Status = SessionStatus.InProgress;
        await t.Db.SaveChangesAsync();

        using var audio = new MemoryStream(new byte[] { 1 });
        await svc.UploadAnswerAsync(session.Id, q.Id, candidate, audio, "audio/webm", 30);

        var after = await t.Db.PracticeAnswers.AsNoTracking().FirstAsync(a => a.Id == answer.Id);
        Assert.Null(after.SampleAnswer);
        Assert.Null(after.Transcript);   // cùng nhóm reset INT-3
    }
}
