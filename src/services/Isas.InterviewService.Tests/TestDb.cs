using Isas.InterviewService.ApplicationDbContext;
using Isas.InterviewService.Entities;
using Isas.InterviewService.Enums;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Isas.InterviewService.Tests;

/// <summary>
/// Tạo InterviewDbContext chạy trên SQLite in-memory (giữ connection mở để DB sống).
/// Dùng schema sinh từ model hiện tại (EnsureCreated) -> đã có cột LastScoringPublishedAt.
/// </summary>
public sealed class TestDb : IDisposable
{
    private readonly SqliteConnection _conn;
    public InterviewDbContext Db { get; }

    public TestDb()
    {
        _conn = new SqliteConnection("DataSource=:memory:");
        _conn.Open();
        Db = NewContext();
        Db.Database.EnsureCreated();
    }

    // Context mới dùng chung connection -> chung DB in-memory (cho test republisher cần scope riêng).
    public InterviewDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<InterviewDbContext>()
            .UseSqlite(_conn)
            .Options;
        return new InterviewDbContext(options);
    }

    public SqliteConnection Connection => _conn;

    public void Dispose()
    {
        Db.Dispose();
        _conn.Dispose();
    }

    // ── Seed helpers ──────────────────────────────────────────────────────
    public static RubricCriterion Criterion(JobCategory cat, int version = 1, bool active = true)
        => new()
        {
            Id = Guid.NewGuid(),
            Name = "Clarity",
            Description = "Trình bày rõ ràng",
            Weight = 1.0m,
            MaxScore = 5,
            IsActive = active,
            JobCategory = cat,
            Version = version
        };

    public static PracticeSession Session(
        Guid candidateId, SessionStatus status, JobCategory cat = JobCategory.BE)
        => new()
        {
            Id = Guid.NewGuid(),
            CandidateId = candidateId,
            JobCategory = cat,
            Status = status,
            CreatedAt = DateTime.UtcNow
        };

    public static PracticeQuestion Question(Guid sessionId, int order = 1)
        => new()
        {
            Id = Guid.NewGuid(),
            SessionId = sessionId,
            OrderNo = order,
            Content = "Giải thích dependency injection?",
            TimeLimitSec = 120
        };

    public static PracticeAnswer Answer(
        Guid sessionId, Guid questionId, AnswerStatus status,
        DateTime createdAt, DateTime? lastPublished)
        => new()
        {
            Id = Guid.NewGuid(),
            SessionId = sessionId,
            QuestionId = questionId,
            AudioObjectKey = "answer-audio/x.webm",
            Status = status,
            DurationSec = 30,
            CreatedAt = createdAt,
            LastScoringPublishedAt = lastPublished
        };
}
