using System.Security.Claims;
using Isas.InterviewService.ApplicationDbContext;
using Isas.InterviewService.Controllers;
using Isas.InterviewService.DTOs;
using Isas.InterviewService.Entities;
using Isas.InterviewService.Enums;
using Isas.InterviewService.Services;
using Isas.InterviewService.Services.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Isas.InterviewService.Tests;

/// <summary>
/// PARITY — thứ mà <c>GET /practice/history?status=Scored&amp;excludeCampaign=true</c> (wizard
/// picker) trả về PHẢI ĐÚNG BẰNG thứ mà <c>RoadmapService.CreateAsync</c> chấp nhận làm nguồn
/// baseline. Lệch một vế là picker cho chọn buổi mà create sẽ từ chối bằng 404 batch không nói id
/// nào sai — người dùng chọn đúng thứ UI cho phép rồi vẫn dính lỗi không giải thích được.
///
/// <para>Cả hai đường CÙNG dùng <see cref="RoadmapSessionEligibility"/> (vế "không phải campaign"
/// là MỘT expression object dùng lại nguyên văn; vế trạng thái là MỘT hằng số duy nhất) — test này
/// là bằng chứng HÀNH VI cho việc dùng chung đó, không chỉ tin vào việc đọc code thấy cùng tên.</para>
/// </summary>
public class RoadmapHistoryEligibilityParityTests
{
    private static RoadmapGenAiResult SampleRoadmap()
        => new(new List<GeneratedMilestone>
        {
            new("M1", new List<string> { "Clarity" }, new List<GeneratedLesson> { new("L1") })
        });

    private static PracticeService BuildPractice(InterviewDbContext db)
        => new(db,
            new Mock<IStorageService>().Object,
            new Mock<IAiServiceQuestionGenerator>().Object,
            new Mock<ISessionScoringNotifier>().Object,
            new Mock<ICreditReservationClient>().Object,
            NullLogger<PracticeService>.Instance);

    private static RoadmapsController RoadmapController(TestDb t, Guid userId)
    {
        var gen = new Mock<IAiServiceRoadmapGenerator>();
        gen.Setup(x => x.GenerateAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<RoadmapWeakness>?>(),
                It.IsAny<string?>(),
                It.IsAny<IReadOnlyList<QuestionTargetCriterionDto>?>(), It.IsAny<string>(),
                It.IsAny<RoadmapMode>(), It.IsAny<CancellationToken>(),
                It.IsAny<IReadOnlyList<RoadmapMistake>?>()))   // REC1-B7 — arity khớp interface (9 tham số)
            .ReturnsAsync(SampleRoadmap());
        var service = new RoadmapService(
            t.Db, new Mock<IStorageService>().Object, gen.Object, NullLogger<RoadmapService>.Instance);
        var controller = new RoadmapsController(
            service, new Mock<IRoadmapLessonService>().Object,
            new Mock<IRoadmapReportService>().Object, NullLogger<RoadmapsController>.Instance);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(ClaimTypes.NameIdentifier, userId.ToString())], "test"))
            }
        };
        return controller;
    }

    private static async Task<(Guid eligible, Guid wrongStatus, Guid isCampaign)> SeedThree(TestDb t, Guid candidate)
    {
        var now = DateTime.UtcNow;
        var eligible = TestDb.Session(candidate, SessionStatus.Scored, createdAt: now);
        var wrongStatus = TestDb.Session(candidate, SessionStatus.InProgress, createdAt: now.AddMinutes(-1));
        var isCampaign = TestDb.Session(
            candidate, SessionStatus.Scored, campaignId: Guid.NewGuid(), createdAt: now.AddMinutes(-2));
        t.Db.AddRange(eligible, wrongStatus, isCampaign);

        // MIS1-B6 — Guard 1/2/3: `eligible` phải mang điểm yếu + 1 lỗi nội dung trích được, nếu
        // không CreateAsync sẽ từ chối nó ở Guard 2/3 dù picker đã chấp nhận — test này CHỈ đo
        // parity ELIGIBILITY (trạng thái/không-campaign), không đo Guard 2/3, nên chỉ `eligible`
        // cần dữ liệu này (wrongStatus/isCampaign đã bị loại ở bước sở hữu/eligibility, KHÔNG bao
        // giờ chạm tới Guard 2/3).
        var crit = TestDb.Criterion(JobCategory.BE, name: "Clarity");
        t.Db.RubricCriteria.Add(crit);
        t.Db.SessionCriterionScores.Add(new SessionCriterionScore
        {
            Id = Guid.NewGuid(), SessionId = eligible.Id, CriterionId = crit.Id, CriterionName = "Clarity",
            AverageScore = 2m, MaxScore = crit.MaxScore, Percentage = 40m, Weight = 1m,
            NeedsImprovement = true, CreatedAt = now
        });
        var question = TestDb.Question(eligible.Id, order: 500);
        t.Db.PracticeQuestions.Add(question);
        var answer = TestDb.Answer(eligible.Id, question.Id, AnswerStatus.Scored, now, now);
        answer.Transcript = "Câu trả lời của ứng viên cho Clarity";
        t.Db.PracticeAnswers.Add(answer);
        t.Db.AnswerScores.Add(new AnswerScore
        {
            Id = Guid.NewGuid(), AnswerId = answer.Id, CriterionId = crit.Id, AttemptNo = 1, Score = 1,
            Reasoning = "Chưa nắm vững Clarity.", RubricVersion = 1, CreatedAt = now
        });

        await t.Db.SaveChangesAsync();
        return (eligible.Id, wrongStatus.Id, isCampaign.Id);
    }

    // ── Picker trả ĐÚNG 1 buổi — cùng buổi mà CreateAsync chấp nhận ──────────────────────
    [Fact]
    public async Task Picker_TraDungBuoiHopLe_VaCreateAsyncChapNhanDungBuoiDo()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var (eligible, wrongStatus, isCampaign) = await SeedThree(t, candidate);

        // Picker: đúng bộ tham số wizard dùng.
        var page = await BuildPractice(t.Db).GetHistoryAsync(
            candidate, status: RoadmapSessionEligibility.RequiredStatus.ToString(), excludeCampaign: true);
        var pickedId = Assert.Single(page.Items).Id;
        Assert.Equal(eligible, pickedId);

        // CreateAsync: chọn ĐÚNG buổi picker vừa trả về → phải THÀNH CÔNG.
        var ctrl = RoadmapController(t, candidate);
        var ok = await ctrl.Create(
            new CreateRoadmapRequest(JobCategory.BE, RoadmapLevel.Junior, null, SessionIds: [pickedId]),
            default);
        Assert.IsType<CreatedResult>(ok);
    }

    // ── CreateAsync từ chối đúng những buổi picker đã loại (không "cho qua sót") ─────────
    [Theory]
    [InlineData(false)]   // wrongStatus
    [InlineData(true)]    // isCampaign
    public async Task CreateAsync_TuChoiBuoiPickerDaLoai(bool useCampaignSession)
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var (eligible, wrongStatus, isCampaign) = await SeedThree(t, candidate);
        var rejectedId = useCampaignSession ? isCampaign : wrongStatus;

        // Picker KHÔNG trả buổi này.
        var page = await BuildPractice(t.Db).GetHistoryAsync(
            candidate, status: RoadmapSessionEligibility.RequiredStatus.ToString(), excludeCampaign: true);
        Assert.DoesNotContain(page.Items, x => x.Id == rejectedId);

        // Và CreateAsync cũng từ chối đúng buổi đó bằng 404 (parity — không phải picker "chặt hơn"
        // create một cách vô nghĩa).
        var ctrl = RoadmapController(t, candidate);
        var result = await ctrl.Create(
            new CreateRoadmapRequest(JobCategory.BE, RoadmapLevel.Junior, null, SessionIds: [rejectedId]),
            default);
        Assert.IsType<NotFoundObjectResult>(result);
    }
}
