using Isas.InterviewService.ApplicationDbContext;
using Isas.InterviewService.Entities;
using Isas.InterviewService.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;

namespace Isas.InterviewService.Tests;

/// <summary>
/// DB5 (nền) + DB26/DB27/DB31 — khoá hợp đồng index cho background sweeper + 2 đường đọc nóng:
///   • SessionAbandonSweeper (practice_sessions): B2B deadline + B2C inactivity — DB27 RE-SHAPE để
///     status (cột chọn lọc) vào filter, thay vì chỉ neo cột bất biến anti-selective.
///   • StuckAnswerRepublisher (practice_answers): status + last_scoring_published_at.
///   • StorageService.GetFilesByUserId (file_records) — DB26.
///   • Lịch sử buổi luyện keyset-paginated (practice_sessions) — DB31.
/// Kiểm tra index tồn tại trên EF model đúng cột + filter (partial). Fixture TestDb gọi EnsureCreated
/// trên SQLite (snake_case) → cũng exercise luôn DDL partial-index: sai snake_case sẽ vỡ ngay dựng DB.
/// </summary>
public class SweeperIndexTests
{
    // (1) B2B expired sweep — DB27: partial index trên Deadline, filter mang status (cột chọn lọc).
    [Fact]
    public void PracticeSessions_HasDeadlinePartialIndex()
    {
        using var t = new TestDb();
        var indexes = t.Db.Model.FindEntityType(typeof(PracticeSession))!.GetIndexes();

        var idx = indexes.SingleOrDefault(i =>
            i.GetDatabaseName() == "ix_practice_sessions_deadline");

        Assert.NotNull(idx);
        Assert.Equal(nameof(PracticeSession.Deadline), Assert.Single(idx!.Properties).Name);
        Assert.Equal("status IN ('Ready', 'InProgress') AND deadline IS NOT NULL", idx.GetFilter());
    }

    // (2) Inactivity sweep — DB27: partial index trên CreatedAt, filter mang status + deadline.
    [Fact]
    public void PracticeSessions_HasB2CActivePartialIndex()
    {
        using var t = new TestDb();
        var indexes = t.Db.Model.FindEntityType(typeof(PracticeSession))!.GetIndexes();

        var idx = indexes.SingleOrDefault(i =>
            i.GetDatabaseName() == "ix_practice_sessions_b2c_active");

        Assert.NotNull(idx);
        Assert.Equal(nameof(PracticeSession.CreatedAt), Assert.Single(idx!.Properties).Name);
        Assert.Equal(
            "status IN ('Ready', 'InProgress') AND deadline IS NULL",
            idx.GetFilter());
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

    // (4) DB26 — file_records(user_id): trước đây bảng KHÔNG có index nào ngoài PK.
    [Fact]
    public void FileRecords_HasUserIdIndex()
    {
        using var t = new TestDb();
        var indexes = t.Db.Model.FindEntityType(typeof(FileRecord))!.GetIndexes();

        var idx = indexes.SingleOrDefault(i =>
            i.Properties.Count == 1 && i.Properties[0].Name == nameof(FileRecord.UserId));

        Assert.NotNull(idx);
        Assert.Equal("ix_file_records_user_id", idx!.GetDatabaseName());
        Assert.Null(idx.GetFilter());
    }

    // (5) DB31 — keyset lịch sử: (candidate_id, created_at DESC, id DESC) khớp ORDER BY của phân trang.
    [Fact]
    public void PracticeSessions_HasCandidateHistoryKeysetIndex()
    {
        using var t = new TestDb();
        // IsDescending KHÔNG nằm trong read-optimized model → phải đọc qua design-time model.
        var indexes = t.Db.GetService<IDesignTimeModel>().Model
            .FindEntityType(typeof(PracticeSession))!.GetIndexes();

        var idx = indexes.SingleOrDefault(i =>
            i.GetDatabaseName() == "ix_practice_sessions_candidate_history");

        Assert.NotNull(idx);
        Assert.Equal(
            new[]
            {
                nameof(PracticeSession.CandidateId),
                nameof(PracticeSession.CreatedAt),
                nameof(PracticeSession.Id)
            },
            idx!.Properties.Select(p => p.Name));
        // candidate_id lọc bằng '=' → ASC; 2 cột đuôi DESC khớp `ORDER BY created_at DESC, id DESC`.
        Assert.Equal(new[] { false, true, true }, idx.IsDescending);
    }

    // (6) DB31 — index single-col candidate_id CŨ phải BIẾN MẤT: nó là tiền tố trái của composite (5)
    // ⇒ giữ lại chỉ tốn ghi/dung lượng. Khoá lại kẻo ai đó "thêm cho chắc" lần nữa.
    [Fact]
    public void PracticeSessions_NoRedundantStandaloneCandidateIdIndex()
    {
        using var t = new TestDb();
        var indexes = t.Db.Model.FindEntityType(typeof(PracticeSession))!.GetIndexes();

        Assert.DoesNotContain(indexes, i =>
            i.Properties.Count == 1 && i.Properties[0].Name == nameof(PracticeSession.CandidateId));
    }

    // (7) DB27 — HỢP ĐỒNG SỐNG CÒN của 2 partial index trên: planner chỉ dùng được partial index nếu
    // CHỨNG MINH được predicate query ⇒ predicate index. Điều đó chỉ đúng khi EF render enum status
    // thành LITERAL. Nếu ai đó đổi sang so bằng biến/tham số (`status = @p`), Postgres hết chứng minh
    // được và 2 index kia thành VÔ DỤNG — im lặng, không test nào khác đỏ. Test này bắt đúng ca đó.
    [Fact]
    public void SweeperQueries_RenderStatusAsLiteral_SoPartialIndexesStayUsable()
    {
        var opt = new DbContextOptionsBuilder<InterviewDbContext>()
            .UseNpgsql("Host=localhost;Database=probe;Username=x;Password=y")
            .UseSnakeCaseNamingConvention()
            .Options;
        using var db = new InterviewDbContext(opt);

        var now = DateTime.UtcNow;

        // (a) B2B — khớp filter "status IN ('Ready', 'InProgress') AND deadline IS NOT NULL".
        var b2b = db.PracticeSessions
            .Where(s => (s.Status == SessionStatus.Ready || s.Status == SessionStatus.InProgress)
                        && s.Deadline != null && s.Deadline < now)
            .Select(s => s.Id)
            .ToQueryString();
        Assert.Contains("status IN ('Ready', 'InProgress')", b2b);
        Assert.Contains("deadline IS NOT NULL", b2b);

        // (b) No-deadline inactivity — khớp filter "status IN ('Ready', 'InProgress') AND deadline IS NULL".
        var b2c = db.PracticeSessions
            .Where(s => (s.Status == SessionStatus.Ready || s.Status == SessionStatus.InProgress)
                        && s.Deadline == null && s.CreatedAt < now)
            .Select(s => s.Id)
            .ToQueryString();
        Assert.Contains("status IN ('Ready', 'InProgress')", b2c);
        Assert.Contains("deadline IS NULL", b2c);
    }
}
