using Isas.InterviewService.ApplicationDbContext;
using Isas.InterviewService.Entities;
using Isas.InterviewService.Enums;
using Microsoft.EntityFrameworkCore;

namespace Isas.InterviewService.Tests;

/// <summary>
/// Bắt ở e2e 2026-07-18: điểm per-answer chỉ trả `criterionId` nên client không hiển thị được tên
/// tiêu chí — B2C hiện trơ "Điểm tiêu chí" ×4, transcript B2B của HR hiện mã GUID.
///
/// Tra ngược phía client KHÔNG khả thi: `rubric_criteria` của campaign được mint `Guid.NewGuid()`
/// lúc materialize (<c>PracticeService</c>) nên id này khác hẳn `campaign_criteria.id`. Vì vậy
/// Interview phải trả kèm `CriterionName`/`MaxScore`.
///
/// Test khoá phần kiểm được ở tầng unit: navigation <c>AnswerScore.Criterion</c> nạp được qua
/// <c>ThenInclude</c> và mang đúng Name/MaxScore (đây là dữ liệu mà mapper đọc). Bản thân
/// <c>MapAnswer</c> là private static nên verify qua đường đọc DB thật ở tầng e2e.
/// </summary>
public class AnswerScoreCriterionNameTests
{
    private static async Task<(Guid answerId, Guid criterionId)> SeedAsync(InterviewDbContext db)
    {
        var criterion = new RubricCriterion
        {
            Id = Guid.NewGuid(),
            Name = "Giao tiếp & trình bày",
            Weight = 0.25m,
            MaxScore = 5,
            IsActive = true,
            JobCategory = JobCategory.BE,
            Version = 1,
        };
        var session = new PracticeSession
        {
            Id = Guid.NewGuid(),
            CandidateId = Guid.NewGuid(),
            JobCategory = JobCategory.BE,
            Status = SessionStatus.Scored,
            CreatedAt = DateTime.UtcNow,
        };
        var question = new PracticeQuestion
        {
            Id = Guid.NewGuid(),
            SessionId = session.Id,
            OrderNo = 1,
            Content = "Câu hỏi thử",
            Kind = QuestionKind.Seed,
        };
        var answer = new PracticeAnswer
        {
            Id = Guid.NewGuid(),
            SessionId = session.Id,
            QuestionId = question.Id,
            Status = AnswerStatus.Scored,
            CreatedAt = DateTime.UtcNow,
        };
        var score = new AnswerScore
        {
            Id = Guid.NewGuid(),
            AnswerId = answer.Id,
            CriterionId = criterion.Id,
            Score = 3m,
            Reasoning = "Trình bày rõ ràng.",
            RubricVersion = 1,
            AttemptNo = 1,
        };

        db.RubricCriteria.Add(criterion);
        db.PracticeSessions.Add(session);
        db.PracticeQuestions.Add(question);
        db.PracticeAnswers.Add(answer);
        db.AnswerScores.Add(score);
        await db.SaveChangesAsync();
        return (answer.Id, criterion.Id);
    }

    // Đường đọc mà 3 site trong PracticeService dùng: Include(Scores).ThenInclude(Criterion).
    [Fact]
    public async Task ThenIncludeCriterion_NapDuocTenVaThangDiem()
    {
        using var tdb = new TestDb();
        var (answerId, criterionId) = await SeedAsync(tdb.Db);

        var answer = await tdb.NewContext().PracticeAnswers
            .AsNoTracking()
            .Include(a => a.Scores).ThenInclude(sc => sc.Criterion)
            .FirstAsync(a => a.Id == answerId);

        var score = Assert.Single(answer.Scores);
        Assert.Equal(criterionId, score.CriterionId);
        Assert.NotNull(score.Criterion);
        Assert.Equal("Giao tiếp & trình bày", score.Criterion.Name);
        Assert.Equal(5, score.Criterion.MaxScore);
    }

    // Quên ThenInclude → Criterion null. Mapper dùng `?.` nên phải ra null, KHÔNG được ném NRE
    // giữa luồng người dùng xem kết quả buổi phỏng vấn.
    [Fact]
    public async Task KhongThenInclude_CriterionNull_KhongNemNRE()
    {
        using var tdb = new TestDb();
        var (answerId, _) = await SeedAsync(tdb.Db);

        var answer = await tdb.NewContext().PracticeAnswers
            .AsNoTracking()
            .Include(a => a.Scores)
            .FirstAsync(a => a.Id == answerId);

        var score = Assert.Single(answer.Scores);
        var name = score.Criterion?.Name;      // chính là biểu thức mapper dùng
        var maxScore = score.Criterion?.MaxScore;

        Assert.Null(name);
        Assert.Null(maxScore);
    }
}
