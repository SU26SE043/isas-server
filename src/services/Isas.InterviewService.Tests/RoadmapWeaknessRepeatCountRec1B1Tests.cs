using Isas.InterviewService.DTOs;
using Isas.InterviewService.Entities;
using Isas.InterviewService.Enums;
using Isas.InterviewService.Services;
using Isas.InterviewService.Services.Interfaces;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Isas.InterviewService.Tests;

/// <summary>
/// REC1-B1 — <c>RoadmapService.CreateAsync</c> phải chọn tiêu chí đưa vào lộ trình theo SỐ LẦN TÁI
/// PHẠM (bao nhiêu BUỔI bị đánh dấu <c>NeedsImprovement</c>), KHÔNG PHẢI chỉ đọc cờ của buổi MỚI
/// NHẤT.
///
/// <para>Trước bản vá, <c>if (baseline.ContainsKey(name)) continue;</c> gộp CHUNG hai việc: (1) chỉ
/// set <c>baseline[name]</c> một lần (first-seen — buổi mới nhất thắng) và (2) chỉ đọc
/// <c>NeedsImprovement</c> một lần (VÔ TÌNH cũng chỉ ở buổi mới nhất, vì <c>continue</c> nhảy qua
/// TRƯỚC khi kiểm tra cờ ở mọi buổi cũ hơn). Hệ quả: tiêu chí yếu 3 buổi liên tiếp mà buổi gần nhất
/// tình cờ ổn sẽ KHÔNG BAO GIỜ vào lộ trình — không lỗi, không cảnh báo.</para>
/// </summary>
public class RoadmapWeaknessRepeatCountRec1B1Tests
{
    private static int _orderCounter;

    // GenMock tối giản — chỉ cần bắt được `weaknesses` được RoadmapService.CreateAsync gửi xuống
    // generator, không cần đi qua HTTP thật (khác RoadmapMistakePayloadMis1B5Tests, vốn kiểm hình
    // dạng JSON ra dây — REC1-B1 chỉ cần kiểm giá trị C# tới generator, hình dạng JSON đã có test
    // riêng ở AiServiceRoadmapGenerator).
    private static Mock<IAiServiceRoadmapGenerator> GenMock(out Func<IReadOnlyList<RoadmapWeakness>?> captured)
    {
        IReadOnlyList<RoadmapWeakness>? weaknesses = null;
        var m = new Mock<IAiServiceRoadmapGenerator>();
        m.Setup(x => x.GenerateAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<RoadmapWeakness>?>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<IReadOnlyList<QuestionTargetCriterionDto>?>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<CriterionEvidence>?>(), It.IsAny<RoadmapMode>(),
                It.IsAny<string?>(), It.IsAny<CancellationToken>(),
                It.IsAny<IReadOnlyList<RoadmapMistake>?>()))
            .Callback<string, string, IReadOnlyList<RoadmapWeakness>?, string?, string?, string?,
                IReadOnlyList<QuestionTargetCriterionDto>?, string, IReadOnlyList<CriterionEvidence>?,
                RoadmapMode, string?, CancellationToken, IReadOnlyList<RoadmapMistake>?>(
                (_, _, w, _, _, _, _, _, _, _, _, _, _) => weaknesses = w)
            .ReturnsAsync(new RoadmapGenAiResult(new List<GeneratedMilestone>
            {
                new("Milestone 1", new List<string> { "Clarity" },
                    new List<GeneratedLesson> { new("Lesson 1.1") })
            }));
        captured = () => weaknesses;
        return m;
    }

    /// <summary>
    /// Seed 1 buổi Scored + 1 SessionCriterionScore cho <paramref name="criterion"/> + (nếu
    /// <paramref name="needsImprovement"/>) đúng 1 AnswerScore dưới ngưỡng — đủ để Guard 3
    /// (<c>ROADMAP_NO_CONTENT_MISTAKES</c>) có gì để trích, không chặn <c>CreateAsync</c>.
    /// </summary>
    private static Guid AddSession(
        TestDb t, Guid candidateId, RubricCriterion criterion, decimal pct, bool needsImprovement,
        DateTime createdAt)
    {
        if (t.Db.RubricCriteria.Local.All(c => c.Id != criterion.Id)
            && !t.Db.RubricCriteria.Any(c => c.Id == criterion.Id))
            t.Db.RubricCriteria.Add(criterion);

        var session = TestDb.Session(candidateId, SessionStatus.Scored, JobCategory.BE, createdAt: createdAt);
        t.Db.PracticeSessions.Add(session);

        t.Db.SessionCriterionScores.Add(new SessionCriterionScore
        {
            Id = Guid.NewGuid(),
            SessionId = session.Id,
            CriterionId = criterion.Id,
            CriterionName = criterion.Name,
            AverageScore = 2m,
            MaxScore = criterion.MaxScore,
            Percentage = pct,
            Weight = 1m,
            NeedsImprovement = needsImprovement,
            CreatedAt = createdAt
        });

        if (needsImprovement)
        {
            var question = TestDb.Question(session.Id, order: ++_orderCounter);
            t.Db.PracticeQuestions.Add(question);
            var answer = TestDb.Answer(session.Id, question.Id, AnswerStatus.Scored, createdAt, createdAt);
            answer.Transcript = "Câu trả lời của ứng viên cho " + criterion.Name;
            t.Db.PracticeAnswers.Add(answer);
            t.Db.AnswerScores.Add(new AnswerScore
            {
                Id = Guid.NewGuid(),
                AnswerId = answer.Id,
                CriterionId = criterion.Id,
                AttemptNo = 1,
                // 1/5 = 20% < ImprovementThresholdPct mặc định (50%) — LUÔN dưới ngưỡng.
                Score = 1,
                Reasoning = $"Chưa nắm vững {criterion.Name}.",
                RubricVersion = 1,
                CreatedAt = createdAt
            });
        }
        return session.Id;
    }

    // ═══════════ Test 1 — yếu ở buổi CŨ, ổn ở buổi MỚI NHẤT ⇒ VẪN vào weaknesses ═══════════

    [Fact]
    public async Task Create_TieuChiYeuOBuoiCu_OnOBuoiMoiNhat_VanVaoWeaknesses()
    {
        using var t = new TestDb();
        var candidateId = Guid.NewGuid();
        var crit = TestDb.Criterion(JobCategory.BE, name: "Clarity");
        var now = DateTime.UtcNow;

        // Buổi CŨ (2 ngày trước): yếu → sinh content mistake cho Guard 3.
        var oldSid = AddSession(t, candidateId, crit, pct: 20, needsImprovement: true, createdAt: now.AddDays(-2));
        // Buổi MỚI NHẤT: ổn — theo luật CŨ, đây là buổi DUY NHẤT được đọc cờ NeedsImprovement.
        var newSid = AddSession(t, candidateId, crit, pct: 90, needsImprovement: false, createdAt: now);
        await t.Db.SaveChangesAsync();

        var gen = GenMock(out var captured);
        var svc = new RoadmapService(
            t.Db, new Mock<IStorageService>().Object, gen.Object, NullLogger<RoadmapService>.Instance);

        await svc.CreateAsync(
            candidateId,
            new CreateRoadmapRequest(JobCategory.BE, RoadmapLevel.Junior, null, SessionIds: [oldSid, newSid]),
            default);

        var weaknesses = captured();
        Assert.NotNull(weaknesses);
        var clarity = Assert.Single(weaknesses!, w => w.CriterionName == "Clarity");
        // Tái phạm ĐÚNG 1/2 buổi (chỉ buổi cũ) — không phải 0 (bug cũ) và không phải 2 (buổi ổn
        // không được tính là tái phạm).
        Assert.Equal(1, clarity.WeakSessions);
        Assert.Equal(2, clarity.TotalSessions);
    }

    // ═══════════ Test 2 — baseline VẪN là điểm buổi mới nhất, KHÔNG thành trung bình ═══════════

    [Fact]
    public async Task Create_Baseline_VanLaDiemBuoiMoiNhat_KhongThanhTrungBinh()
    {
        using var t = new TestDb();
        var candidateId = Guid.NewGuid();
        var crit = TestDb.Criterion(JobCategory.BE, name: "Clarity");
        var now = DateTime.UtcNow;

        // Buổi CŨ: 10% (yếu). Buổi MỚI NHẤT: 80% (ổn). Trung bình = 45% — nếu baseline lỡ bị đổi
        // thành trung bình (hoặc thành giá trị buổi CŨ NHẤT), test này phải bắt được ngay.
        var oldSid = AddSession(t, candidateId, crit, pct: 10, needsImprovement: true, createdAt: now.AddDays(-3));
        var newSid = AddSession(t, candidateId, crit, pct: 80, needsImprovement: false, createdAt: now);
        await t.Db.SaveChangesAsync();

        var gen = GenMock(out var captured);
        var svc = new RoadmapService(
            t.Db, new Mock<IStorageService>().Object, gen.Object, NullLogger<RoadmapService>.Instance);

        await svc.CreateAsync(
            candidateId,
            new CreateRoadmapRequest(JobCategory.BE, RoadmapLevel.Junior, null, SessionIds: [oldSid, newSid]),
            default);

        var clarity = Assert.Single(captured()!, w => w.CriterionName == "Clarity");
        Assert.Equal(80m, clarity.Percentage);
    }
}
