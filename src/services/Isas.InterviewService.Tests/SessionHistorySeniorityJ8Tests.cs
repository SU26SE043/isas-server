using Isas.InterviewService.ApplicationDbContext;
using Isas.InterviewService.Enums;
using Isas.InterviewService.Services;
using Isas.InterviewService.Services.Interfaces;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Isas.InterviewService.Tests;

/// <summary>
/// J8 — cấp độ ứng viên phải xuất hiện trong <c>GET /practice/sessions/history</c>. Trong B2C,
/// cấp độ ảnh hưởng cả câu hỏi (J4) lẫn trọng tâm chấm (J5) ⇒ đổi cấp độ là đổi bối cảnh: so điểm
/// buổi Junior với buổi Senior trên cùng biểu đồ tiến bộ là so hai thứ khác nhau.
/// </summary>
public class SessionHistorySeniorityJ8Tests
{
    private static PracticeService BuildPractice(TestDb t) =>
        new(
            t.Db, new Mock<IStorageService>().Object,
            new Mock<IAiServiceQuestionGenerator>().Object,
            new Mock<ISessionScoringNotifier>().Object,
            new Mock<ICreditReservationClient>().Object,
            NullLogger<PracticeService>.Instance);

    [Fact]
    public async Task History_ReturnsSeniority_ForEachSession()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var session = TestDb.Session(candidate, SessionStatus.Scored, JobCategory.BE);
        session.Seniority = "Senior";
        t.Db.Add(session);
        await t.Db.SaveChangesAsync();

        var history = await BuildPractice(t).GetHistoryAsync(candidate);

        var item = Assert.Single(history.Items);
        Assert.Equal("Senior", item.Seniority);
    }

    // Buổi cũ (session.Seniority mặc định "Junior" — DB DefaultValue) vẫn phải trả về một chuỗi
    // hợp lệ, không null/rỗng: field non-nullable trên DTO, khớp cột non-nullable trên entity.
    [Fact]
    public async Task History_DefaultSeniority_IsJunior_ForSessionsCreatedWithoutExplicitLevel()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var session = TestDb.Session(candidate, SessionStatus.Ready, JobCategory.BE);
        t.Db.Add(session);
        await t.Db.SaveChangesAsync();

        var history = await BuildPractice(t).GetHistoryAsync(candidate);

        var item = Assert.Single(history.Items);
        Assert.Equal("Junior", item.Seniority);
    }
}
