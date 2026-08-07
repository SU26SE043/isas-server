using System.Text.Json;
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
/// DB19 — UseSnakeCaseNamingConvention() để cột SQLite mang tên snake_case, khớp SQL của model-level
/// CHECK ck_rubric_criteria_single_owner ("campaign_id IS NULL OR candidate_id IS NULL"). Không có
/// convention, EnsureCreated sinh CHECK tham chiếu cột snake_case không tồn tại → vỡ toàn bộ test.
/// Test dùng LINQ (property expression) nên đổi tên cột không đổi hành vi CRUD.
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
            .UseSnakeCaseNamingConvention()
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

    // DB2 — SessionScoringNotifier THẬT (ghi outbox-row + side-effect BC9/10/14/15). Notifier KHÔNG còn
    // giữ publisher; publish thật do OutboxDispatcher. summarizer/roadmapReport override khi cần test riêng.
    public static SessionScoringNotifier Notifier(
        InterviewDbContext db,
        IAiServiceSessionSummarizer? summarizer = null,
        IRoadmapReportService? roadmapReport = null)
        => new(db,
            ResultService(db),
            summarizer ?? Summarizer(),
            roadmapReport ?? RoadmapReport(db),
            NullLogger<SessionScoringNotifier>.Instance);

    // DB2 — đọc outbox-row settlement-event của 1 session (deserialize Payload đúng options mặc định như
    // OutboxMessage.For* → khớp wire cũ). Dùng NewContext để đọc bản đã commit (không dính change-tracker).
    public static SessionScoredEvent? ScoredOutbox(InterviewDbContext db, Guid sessionId)
    {
        var row = db.OutboxMessages.AsNoTracking()
            .SingleOrDefault(m => m.SessionId == sessionId && m.Type == OutboxMessage.SessionScoredType);
        return row is null ? null : JsonSerializer.Deserialize<SessionScoredEvent>(row.Payload);
    }

    public static SessionAbandonedEvent? AbandonedOutbox(InterviewDbContext db, Guid sessionId)
    {
        var row = db.OutboxMessages.AsNoTracking()
            .SingleOrDefault(m => m.SessionId == sessionId && m.Type == OutboxMessage.SessionAbandonedType);
        return row is null ? null : JsonSerializer.Deserialize<SessionAbandonedEvent>(row.Payload);
    }

    // Số outbox-row của 1 session theo Type (dùng cho Times.Once/Never → 1/0).
    public static int OutboxCount(InterviewDbContext db, Guid sessionId, string type)
        => db.OutboxMessages.AsNoTracking().Count(m => m.SessionId == sessionId && m.Type == type);

    // E10 — ScoringOptions cho AnswerService. Mặc định N=1 (self-consistency TẮT) → giữ hành vi cũ;
    // test self-consistency truyền N>1 + ngưỡng spread + temperature.
    public static IOptions<ScoringOptions> ScoringOpts(
        int selfConsistencyN = 1, decimal varianceThreshold = 1m, double temperature = 0.4,
        int minReasoningLen = 0)   // E11 — 0 = tắt (opt-in); >0 để test cờ nhận xét quá ngắn
        => Options.Create(new ScoringOptions
        {
            SelfConsistencyN = selfConsistencyN,
            VarianceThreshold = varianceThreshold,
            SelfConsistencyTemperature = temperature,
            MinReasoningLen = minReasoningLen
        });

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
    // Q8 — `language` có DEFAULT "vi" nên mọi test cũ không phải sửa dòng nào, nhưng chính vì trước
    // đây KHÔNG có chiều này mà toàn bộ suite đơn ngữ ⇒ vế lọc `c.Language` bị thiếu ở production
    // không thể biểu hiện. Test song ngữ phải truyền tường minh.
    // candidateId != null = rubric RIÊNG của candidate (BC16); null = seed mặc định dùng chung.
    public static RubricCriterion Criterion(
        JobCategory cat, int version = 1, bool active = true,
        Guid? campaignId = null, string name = "Clarity", Guid? candidateId = null,
        string language = "vi")
        => new()
        {
            Language = language,
            Id = Guid.NewGuid(),
            Name = name,
            Description = "Trình bày rõ ràng",
            Weight = 1.0m,
            MaxScore = 5,
            IsActive = active,
            JobCategory = cat,
            CampaignId = campaignId,
            CandidateId = candidateId,
            Version = version
        };

    public static PracticeSession Session(
        Guid candidateId, SessionStatus status, JobCategory cat = JobCategory.BE,
        Guid? campaignId = null, DateTime? createdAt = null, DateTime? deadline = null,
        string language = "vi")
        => new()
        {
            Language = language,
            Id = Guid.NewGuid(),
            CandidateId = candidateId,
            JobCategory = cat,
            CampaignId = campaignId,
            Status = status,
            CreatedAt = createdAt ?? DateTime.UtcNow,
            Deadline = deadline   // I2: null = không hard-deadline (B2C); có giá trị = hạn chót nhận bài (B2B)
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
