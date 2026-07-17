using Isas.InterviewService.Entities;
using Microsoft.EntityFrameworkCore;

namespace Isas.InterviewService.Tests;

/// <summary>
/// DB5 — khoá hợp đồng index cho 2 background sweeper (quét mỗi 2', trước đây seq-scan cả bảng):
///   • SessionAbandonSweeper (practice_sessions): B2B deadline + B2C inactivity.
///   • StuckAnswerRepublisher (practice_answers): status + last_scoring_published_at.
/// Kiểm tra index tồn tại trên EF model đúng cột + filter (partial). Fixture TestDb gọi EnsureCreated
/// trên SQLite (snake_case) → cũng exercise luôn DDL partial-index: sai snake_case sẽ vỡ ngay dựng DB.
/// </summary>
public class SweeperIndexTests
{
    // (1) B2B expired sweep: partial index trên Deadline, filter "deadline IS NOT NULL".
    [Fact]
    public void PracticeSessions_HasDeadlinePartialIndex()
    {
        using var t = new TestDb();
        var indexes = t.Db.Model.FindEntityType(typeof(PracticeSession))!.GetIndexes();

        var idx = indexes.SingleOrDefault(i =>
            i.Properties.Count == 1 && i.Properties[0].Name == nameof(PracticeSession.Deadline));

        Assert.NotNull(idx);
        Assert.Equal("ix_practice_sessions_deadline", idx!.GetDatabaseName());
        Assert.Equal("deadline IS NOT NULL", idx.GetFilter());
    }

    // (2) B2C inactivity sweep: partial index trên CreatedAt, filter "campaign_id IS NULL AND deadline IS NULL".
    [Fact]
    public void PracticeSessions_HasB2CActivePartialIndex()
    {
        using var t = new TestDb();
        var indexes = t.Db.Model.FindEntityType(typeof(PracticeSession))!.GetIndexes();

        var idx = indexes.SingleOrDefault(i =>
            i.GetDatabaseName() == "ix_practice_sessions_b2c_active");

        Assert.NotNull(idx);
        Assert.Equal(nameof(PracticeSession.CreatedAt), Assert.Single(idx!.Properties).Name);
        Assert.Equal("campaign_id IS NULL AND deadline IS NULL", idx.GetFilter());
    }

    // (3) Stuck-answer republish: composite (Status, LastScoringPublishedAt), non-partial (filter null).
    [Fact]
    public void PracticeAnswers_HasStatusLspCompositeIndex()
    {
        using var t = new TestDb();
        var indexes = t.Db.Model.FindEntityType(typeof(PracticeAnswer))!.GetIndexes();

        var idx = indexes.SingleOrDefault(i =>
            i.Properties.Count == 2
            && i.Properties[0].Name == nameof(PracticeAnswer.Status)
            && i.Properties[1].Name == nameof(PracticeAnswer.LastScoringPublishedAt));

        Assert.NotNull(idx);
        Assert.Equal("ix_practice_answers_status_lsp", idx!.GetDatabaseName());
        Assert.Null(idx.GetFilter());   // composite non-partial
    }
}
