using Isas.InterviewService.ApplicationDbContext;
using Isas.InterviewService.DTOs;
using Isas.InterviewService.Entities;
using Isas.InterviewService.Enums;
using Isas.InterviewService.Models;
using Isas.InterviewService.Services;
using Isas.InterviewService.Services.Interfaces;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

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

    // BC9 — SessionResultService thật (dùng khi cần notifier THẬT tính tổng kết B2C).
    public static SessionResultService ResultService(InterviewDbContext db, decimal thresholdPct = 50m)
        => new(db,
            Options.Create(new ScoringOptions { ImprovementThresholdPct = thresholdPct }),
            NullLogger<SessionResultService>.Instance);

    // BC10 — summarizer AI giả cho notifier THẬT: comment=null/"" → no-op (không lưu overall_comment);
    // comment có text → trả text; throws → ném (test best-effort không chặn Scored). Không cần AIService thật.
    public static IAiServiceSessionSummarizer Summarizer(string? comment = null, Exception? throws = null)
    {
        var m = new Mock<IAiServiceSessionSummarizer>();
        var setup = m.Setup(s => s.SummarizeAsync(
            It.IsAny<string>(), It.IsAny<decimal>(),
            It.IsAny<IReadOnlyList<SessionSummaryCriterion>>(), It.IsAny<CancellationToken>()));
        if (throws is not null) setup.ThrowsAsync(throws);
        else setup.ReturnsAsync(comment ?? string.Empty);
        return m.Object;
    }

    // BC15 — RoadmapReportService THẬT cho notifier (rollup milestone/roadmap khi lesson Done). generator
    // mặc định = mock no-op (session không gắn lesson → OnLessonDoneAsync return sớm, không gọi AI).
    public static RoadmapReportService RoadmapReport(
        InterviewDbContext db, IAiServiceRoadmapGenerator? generator = null)
        => new(
            db,
            generator ?? new Mock<IAiServiceRoadmapGenerator>().Object,
            Options.Create(new Isas.InterviewService.Models.RoadmapOptions()),
            NullLogger<RoadmapReportService>.Instance);

    // ── Seed helpers ──────────────────────────────────────────────────────
    public static RubricCriterion Criterion(
        JobCategory cat, int version = 1, bool active = true,
        Guid? campaignId = null, string name = "Clarity")
        => new()
        {
            Id = Guid.NewGuid(),
            Name = name,
            Description = "Trình bày rõ ràng",
            Weight = 1.0m,
            MaxScore = 5,
            IsActive = active,
            JobCategory = cat,
            CampaignId = campaignId,
            Version = version
        };

    public static PracticeSession Session(
        Guid candidateId, SessionStatus status, JobCategory cat = JobCategory.BE,
        Guid? campaignId = null, DateTime? createdAt = null)
        => new()
        {
            Id = Guid.NewGuid(),
            CandidateId = candidateId,
            JobCategory = cat,
            CampaignId = campaignId,
            Status = status,
            CreatedAt = createdAt ?? DateTime.UtcNow
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
