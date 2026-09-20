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
/// CAMP-21 áp CẢ B2C (2026-09-21): câu GỐC bỏ trống = mất điểm, cùng luật với B2B.
/// <c>overall = trung bình cộng pct × (câu gốc đã trả lời / tổng câu gốc)</c>.
///
/// Hai vế phải khoá riêng: (1) đường TẠO buổi luyện ghim <c>skip_penalty = true</c> — thiếu vế này thì
/// <c>SkipPenaltyRule.Apply</c> luôn thoát sớm và luật thành no-op im lặng; (2) đường TÍNH điểm B2C
/// (<see cref="SessionResultService"/>) thật sự nhân hệ số — thiếu vế này thì cột ghim thành cột chết.
/// </summary>
public class B2cSkipPenaltyTests
{
    // ── Vế 1: đường tạo buổi ghim cờ ────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateSession_B2C_GhimSkipPenaltyTrue()
    {
        using var t = new TestDb();
        var candidateId = Guid.NewGuid();
        var svc = BuildPractice(t);

        var res = await svc.CreateSessionAsync(
            candidateId, new CreatePracticeSessionRequest(null, null, JobCategory.BE));

        var session = await t.Db.PracticeSessions.AsNoTracking().FirstAsync(s => s.Id == res.Id);
        Assert.Null(session.CampaignId);          // đúng là buổi B2C
        Assert.True(session.SkipPenalty);
    }

    // Lesson (BC14) đi CÙNG đường CreateSessionInternalAsync — khoá riêng để ai tách nhánh lesson
    // ra sau này không vô tình bỏ rơi cờ.
    [Fact]
    public async Task CreateLessonSession_B2C_GhimSkipPenaltyTrue()
    {
        using var t = new TestDb();
        var candidateId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var svc = BuildPractice(t);

        await svc.CreateLessonSessionAsync(
            candidateId, new CreatePracticeSessionRequest(null, null, JobCategory.BE),
            sessionId, focusCriteria: null, lessonContext: null);

        var session = await t.Db.PracticeSessions.AsNoTracking().FirstAsync(s => s.Id == sessionId);
        Assert.True(session.SkipPenalty);
    }

    // ── Vế 2: đường tính điểm B2C nhân hệ số ───────────────────────────────────────────────

    // 3 câu gốc, trả lời 2 (mỗi câu 80%), bỏ trống 1 ⇒ 80 × 2/3 = 53.33.
    [Fact]
    public async Task Compute_B2C_SkipPenaltyTrue_BoTrong1Trong3CauGoc_Nhan2Phan3()
    {
        using var t = new TestDb();
        var session = TestDb.Session(Guid.NewGuid(), SessionStatus.Scored, JobCategory.BE);
        session.SkipPenalty = true;
        var crit = Crit(JobCategory.BE, "Clarity", maxScore: 5);
        var q1 = TestDb.Question(session.Id, 1);
        var q2 = TestDb.Question(session.Id, 2);
        var q3 = TestDb.Question(session.Id, 3);   // bỏ trống — không answer nào
        var a1 = TestDb.Answer(session.Id, q1.Id, AnswerStatus.Scored, DateTime.UtcNow, DateTime.UtcNow);
        var a2 = TestDb.Answer(session.Id, q2.Id, AnswerStatus.Scored, DateTime.UtcNow, DateTime.UtcNow);
        t.Db.AddRange(session, crit, q1, q2, q3, a1, a2,
            Score(a1.Id, crit.Id, 4m), Score(a2.Id, crit.Id, 4m));   // 4/5 = 80%
        await t.Db.SaveChangesAsync();

        await TestDb.ResultService(t.Db).ComputeAndStoreAsync(session.Id);

        var s = await t.Db.PracticeSessions.AsNoTracking().FirstAsync(x => x.Id == session.Id);
        Assert.Equal(53.33m, s.OverallScore);
        // Breakdown từng tiêu chí KHÔNG bị phạt — phạt chỉ áp lên điểm tổng (cùng B2B).
        var row = await t.Db.SessionCriterionScores.AsNoTracking().SingleAsync(x => x.SessionId == session.Id);
        Assert.Equal(80m, row.Percentage);
    }

    // Buổi tạo TRƯỚC bản này ghim `false` ⇒ giữ nguyên trung bình cộng — không hồi tố điểm cũ.
    [Fact]
    public async Task Compute_B2C_SkipPenaltyFalse_KhongPhat_KhongHoiTo()
    {
        using var t = new TestDb();
        var session = TestDb.Session(Guid.NewGuid(), SessionStatus.Scored, JobCategory.BE);
        session.SkipPenalty = false;
        var crit = Crit(JobCategory.BE, "Clarity", maxScore: 5);
        var q1 = TestDb.Question(session.Id, 1);
        var q2 = TestDb.Question(session.Id, 2);
        var q3 = TestDb.Question(session.Id, 3);
        var a1 = TestDb.Answer(session.Id, q1.Id, AnswerStatus.Scored, DateTime.UtcNow, DateTime.UtcNow);
        var a2 = TestDb.Answer(session.Id, q2.Id, AnswerStatus.Scored, DateTime.UtcNow, DateTime.UtcNow);
        t.Db.AddRange(session, crit, q1, q2, q3, a1, a2,
            Score(a1.Id, crit.Id, 4m), Score(a2.Id, crit.Id, 4m));
        await t.Db.SaveChangesAsync();

        await TestDb.ResultService(t.Db).ComputeAndStoreAsync(session.Id);

        var s = await t.Db.PracticeSessions.AsNoTracking().FirstAsync(x => x.Id == session.Id);
        Assert.Equal(80m, s.OverallScore);
    }

    // Ghi âm IM LẶNG (có audio, VAD kết luận no_speech) KHÔNG tính là trả lời — cùng vị ngữ với B2B.
    // 2 câu gốc: 1 trả lời thật 80%, 1 nộp im lặng ⇒ 80 × 1/2 = 40 (không phải 80).
    [Fact]
    public async Task Compute_B2C_NopImLang_KhongTinhLaTraLoi()
    {
        using var t = new TestDb();
        var session = TestDb.Session(Guid.NewGuid(), SessionStatus.Scored, JobCategory.BE);
        session.SkipPenalty = true;
        var crit = Crit(JobCategory.BE, "Clarity", maxScore: 5);
        var q1 = TestDb.Question(session.Id, 1);
        var q2 = TestDb.Question(session.Id, 2);
        var a1 = TestDb.Answer(session.Id, q1.Id, AnswerStatus.Scored, DateTime.UtcNow, DateTime.UtcNow);
        var silent = TestDb.Answer(session.Id, q2.Id, AnswerStatus.Skipped, DateTime.UtcNow, null,
            audioObjectKey: "answer-audio/silent.webm", rejectReason: "no_speech");
        t.Db.AddRange(session, crit, q1, q2, a1, silent, Score(a1.Id, crit.Id, 4m));
        await t.Db.SaveChangesAsync();

        await TestDb.ResultService(t.Db).ComputeAndStoreAsync(session.Id);

        var s = await t.Db.PracticeSessions.AsNoTracking().FirstAsync(x => x.Id == session.Id);
        Assert.Equal(40m, s.OverallScore);
    }

    // Câu ĐÀO SÂU (FollowUp) không vào mẫu số: 1 câu gốc đã trả lời + 1 câu đào sâu bỏ trống
    // ⇒ seed 1/1 ⇒ KHÔNG phạt. (Độ dài chuỗi do AI quyết lúc thi, không phải do ứng viên.)
    [Fact]
    public async Task Compute_B2C_CauDaoSauBoTrong_KhongVaoMauSo()
    {
        using var t = new TestDb();
        var session = TestDb.Session(Guid.NewGuid(), SessionStatus.Scored, JobCategory.BE);
        session.SkipPenalty = true;
        var crit = Crit(JobCategory.BE, "Clarity", maxScore: 5);
        var seed = TestDb.Question(session.Id, 1);
        var deep = TestDb.Question(session.Id, 2);
        deep.Kind = QuestionKind.FollowUp;
        deep.Depth = 1;
        deep.RootQuestionId = seed.Id;
        var a1 = TestDb.Answer(session.Id, seed.Id, AnswerStatus.Scored, DateTime.UtcNow, DateTime.UtcNow);
        t.Db.AddRange(session, crit, seed, deep, a1, Score(a1.Id, crit.Id, 4m));
        await t.Db.SaveChangesAsync();

        await TestDb.ResultService(t.Db).ComputeAndStoreAsync(session.Id);

        var s = await t.Db.PracticeSessions.AsNoTracking().FirstAsync(x => x.Id == session.Id);
        Assert.Equal(80m, s.OverallScore);
    }

    // Câu đào sâu ĐÃ TRẢ LỜI cũng không được đếm vào tử số: 2 câu gốc (1 trả lời, 1 bỏ) + 1 đào sâu
    // đã trả lời dưới câu gốc 1 ⇒ seed 1/2 ⇒ ×1/2. Đếm nhầm cả đào sâu ra 2/2 = xoá mất hình phạt
    // — chuỗi đào sâu dài (do AI quyết) sẽ "bù" cho câu gốc bỏ trống.
    [Fact]
    public async Task Compute_B2C_CauDaoSauDaTraLoi_KhongVaoTuSo()
    {
        using var t = new TestDb();
        var session = TestDb.Session(Guid.NewGuid(), SessionStatus.Scored, JobCategory.BE);
        session.SkipPenalty = true;
        var crit = Crit(JobCategory.BE, "Clarity", maxScore: 5);
        var seed1 = TestDb.Question(session.Id, 1);
        var deep = TestDb.Question(session.Id, 2);
        deep.Kind = QuestionKind.FollowUp;
        deep.Depth = 1;
        deep.RootQuestionId = seed1.Id;
        var seed2 = TestDb.Question(session.Id, 3);   // bỏ trống
        var a1 = TestDb.Answer(session.Id, seed1.Id, AnswerStatus.Scored, DateTime.UtcNow, DateTime.UtcNow);
        var aDeep = TestDb.Answer(session.Id, deep.Id, AnswerStatus.Scored, DateTime.UtcNow, DateTime.UtcNow);
        t.Db.AddRange(session, crit, seed1, deep, seed2, a1, aDeep,
            Score(a1.Id, crit.Id, 4m), Score(aDeep.Id, crit.Id, 4m));   // đều 80%
        await t.Db.SaveChangesAsync();

        await TestDb.ResultService(t.Db).ComputeAndStoreAsync(session.Id);

        var s = await t.Db.PracticeSessions.AsNoTracking().FirstAsync(x => x.Id == session.Id);
        Assert.Equal(40m, s.OverallScore);   // 80 × 1/2
    }

    // ── Vế 3: GET trả đủ dữ liệu để màn kết quả GIẢI THÍCH phép nhân ───────────────────────

    // Buổi ghim luật: GET trả skipPenalty=true + seed 2/3 + điểm trước phạt 80 ⇒ client dựng được
    // "80 × 2/3 = 53.33" từ chính response, không phải tự suy từ answeredCount (đếm cả đào sâu).
    [Fact]
    public async Task GetSession_B2C_TraSeedVaDiemTruocPhat_KhopVoiPhepNhan()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var session = TestDb.Session(candidate, SessionStatus.Scored, JobCategory.BE);
        session.SkipPenalty = true;
        var crit = Crit(JobCategory.BE, "Clarity", maxScore: 5);
        var q1 = TestDb.Question(session.Id, 1);
        var deep = TestDb.Question(session.Id, 2);            // đào sâu ĐÃ trả lời — không vào seed_*
        deep.Kind = QuestionKind.FollowUp; deep.Depth = 1; deep.RootQuestionId = q1.Id;
        var q2 = TestDb.Question(session.Id, 3);
        var q3 = TestDb.Question(session.Id, 4);              // bỏ trống
        var a1 = TestDb.Answer(session.Id, q1.Id, AnswerStatus.Scored, DateTime.UtcNow, DateTime.UtcNow);
        var aD = TestDb.Answer(session.Id, deep.Id, AnswerStatus.Scored, DateTime.UtcNow, DateTime.UtcNow);
        var a2 = TestDb.Answer(session.Id, q2.Id, AnswerStatus.Scored, DateTime.UtcNow, DateTime.UtcNow);
        t.Db.AddRange(session, crit, q1, deep, q2, q3, a1, aD, a2,
            Score(a1.Id, crit.Id, 4m), Score(aD.Id, crit.Id, 4m), Score(a2.Id, crit.Id, 4m));
        await t.Db.SaveChangesAsync();
        await TestDb.ResultService(t.Db).ComputeAndStoreAsync(session.Id);

        var resp = await BuildPracticeForGet(t).GetSessionAsync(candidate, session.Id);

        var r = resp!.Result!;
        Assert.True(r.SkipPenalty);
        Assert.Equal(3, r.SeedTotal);            // q1, q2, q3 — KHÔNG đếm deep
        Assert.Equal(2, r.SeedAnswered);         // q1, q2 — deep đã trả lời KHÔNG đếm
        Assert.Equal(80m, r.ScoreBeforePenalty);
        Assert.Equal(53.33m, r.OverallScore);
        // Đối chứng: answeredCount/totalQuestions đếm CẢ đào sâu (3/4) ⇒ không dùng được để giải thích ×2/3.
        Assert.Equal(3, r.AnsweredCount);
        Assert.Equal(4, r.TotalQuestions);
    }

    // Buổi cũ (false) ⇒ ba field null — không bịa "0/0" cho buổi không có luật (BK23).
    [Fact]
    public async Task GetSession_B2C_BuoiCuKhongGhimLuat_BaFieldNull()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var session = TestDb.Session(candidate, SessionStatus.Scored, JobCategory.BE);
        session.SkipPenalty = false;
        var crit = Crit(JobCategory.BE, "Clarity", maxScore: 5);
        var q1 = TestDb.Question(session.Id, 1);
        var a1 = TestDb.Answer(session.Id, q1.Id, AnswerStatus.Scored, DateTime.UtcNow, DateTime.UtcNow);
        t.Db.AddRange(session, crit, q1, a1, Score(a1.Id, crit.Id, 4m));
        await t.Db.SaveChangesAsync();
        await TestDb.ResultService(t.Db).ComputeAndStoreAsync(session.Id);

        var resp = await BuildPracticeForGet(t).GetSessionAsync(candidate, session.Id);

        var r = resp!.Result!;
        Assert.False(r.SkipPenalty);
        Assert.Null(r.SeedTotal);
        Assert.Null(r.SeedAnswered);
        Assert.Null(r.ScoreBeforePenalty);
    }

    private static PracticeService BuildPracticeForGet(TestDb t)
        => new(t.Db, new Mock<IStorageService>().Object,
            new Mock<IAiServiceQuestionGenerator>().Object, new Mock<ISessionScoringNotifier>().Object,
            new Mock<ICreditReservationClient>().Object, NullLogger<PracticeService>.Instance);

    // ── helpers ───────────────────────────────────────────────────────────────────────────

    private static PracticeService BuildPractice(TestDb t)
    {
        var gen = new Mock<IAiServiceQuestionGenerator>();
        gen.Setup(g => g.GenerateQuestionsAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<GeneratedQuestion> { new() { Content = "Q1" } });

        var reservation = new Mock<ICreditReservationClient>();
        reservation
            .Setup(r => r.ReserveAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CreditReservationResult(Guid.NewGuid(), 1));

        return new PracticeService(
            t.Db, new Mock<IStorageService>().Object, gen.Object,
            new Mock<ISessionScoringNotifier>().Object, reservation.Object,
            NullLogger<PracticeService>.Instance);
    }

    private static AnswerScore Score(Guid answerId, Guid criterionId, decimal score)
        => new()
        {
            Id = Guid.NewGuid(),
            AnswerId = answerId,
            CriterionId = criterionId,
            AttemptNo = 1,
            Score = score,
            Reasoning = "x",
            RubricVersion = 1,
            CreatedAt = DateTime.UtcNow
        };

    private static RubricCriterion Crit(JobCategory cat, string name, int maxScore)
        => new()
        {
            Id = Guid.NewGuid(),
            Name = name,
            Description = name,
            Weight = 1m,
            MaxScore = maxScore,
            IsActive = true,
            JobCategory = cat,
            CampaignId = null,
            Language = "vi",
            Version = 1
        };
}
