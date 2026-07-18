using System.Data.Common;
using Isas.InterviewService.ApplicationDbContext;
using Isas.InterviewService.Entities;
using Isas.InterviewService.Enums;
using Isas.InterviewService.Services;
using Isas.InterviewService.Services.Interfaces;
using Isas.Shared.Pagination;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Isas.InterviewService.Tests;

// DB31 — (a) lịch sử phỏng vấn keyset-paged (mẫu DB8) thay vì trả trọn đời trong 1 payload;
//        (b) AsSplitQuery cho các truy vấn Include(Scores).ThenInclude(Criterion).
public class HistoryPagingSplitQueryDb31Tests
{
    private static PracticeService BuildPractice(InterviewDbContext db)
        => new(db,
            new Mock<IStorageService>().Object,
            new Mock<IAiServiceQuestionGenerator>().Object,
            new Mock<ISessionScoringNotifier>().Object,
            new Mock<ICreditReservationClient>().Object,
            NullLogger<PracticeService>.Instance);

    // Tạo n session cho candidate, createdAt giảm dần (mới nhất trước).
    private static async Task<List<Guid>> SeedSessions(TestDb t, Guid candidate, int n)
    {
        var ids = new List<Guid>();
        var now = DateTime.UtcNow;
        for (var i = 0; i < n; i++)
        {
            var s = TestDb.Session(candidate, SessionStatus.Scored, createdAt: now.AddMinutes(-i));
            t.Db.Add(s);
            ids.Add(s.Id);   // ids[0] = mới nhất
        }
        await t.Db.SaveChangesAsync();
        return ids;
    }

    // ── (a) keyset pagination ─────────────────────────────────────────────

    // limit chặn số row 1 trang + phát cursor cho trang sau. Gỡ Take/cursor → 5 row → ĐỎ.
    [Fact]
    public async Task Db31_History_LimitCapsPage_AndEmitsCursor()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var ids = await SeedSessions(t, candidate, 5);

        var page = await BuildPractice(t.Db).GetHistoryAsync(candidate, cursor: null, limit: 2);

        Assert.Equal(2, page.Items.Count);
        Assert.Equal(new[] { ids[0], ids[1] }, page.Items.Select(x => x.Id).ToArray());   // mới nhất trước
        Assert.NotNull(page.NextCursor);
    }

    // Đi hết các trang bằng cursor → gom đủ + đúng thứ tự + KHÔNG trùng/sót; trang cuối hết cursor.
    [Fact]
    public async Task Db31_History_CursorWalk_CoversEveryRowExactlyOnce()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var ids = await SeedSessions(t, candidate, 5);

        var seen = new List<Guid>();
        string? cursor = null;
        for (var guard = 0; guard < 10; guard++)
        {
            var page = await BuildPractice(t.Db).GetHistoryAsync(candidate, cursor, limit: 2);
            seen.AddRange(page.Items.Select(x => x.Id));
            cursor = page.NextCursor;
            if (cursor is null) break;
        }

        Assert.Null(cursor);            // đã tới trang cuối
        Assert.Equal(ids, seen);        // đủ 5, đúng thứ tự mới→cũ, không trùng
    }

    // Backward-compat DB8: không truyền limit ⇒ mặc định = trần cũ (500) ⇒ hành vi y như trước.
    [Fact]
    public async Task Db31_History_NoLimit_KeepsLegacyBehaviour()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        await SeedSessions(t, candidate, 5);

        var page = await BuildPractice(t.Db).GetHistoryAsync(candidate);

        Assert.Equal(5, page.Items.Count);
        Assert.Null(page.NextCursor);   // chưa đầy trang ⇒ hết
        Assert.Equal(500, KeysetPaging.DefaultLimit);
    }

    // BC-3 — phân trang KHÔNG được làm rò lịch sử của candidate khác.
    [Fact]
    public async Task Db31_History_StillScopedToOwner()
    {
        using var t = new TestDb();
        var me = Guid.NewGuid();
        await SeedSessions(t, me, 2);
        await SeedSessions(t, Guid.NewGuid(), 3);

        var page = await BuildPractice(t.Db).GetHistoryAsync(me, cursor: null, limit: 500);

        Assert.Equal(2, page.Items.Count);
    }

    // Cursor rác không được thành 500 — KeysetCursor.Decode tổng ⇒ coi như trang đầu.
    [Fact]
    public async Task Db31_History_MalformedCursor_FallsBackToFirstPage()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var ids = await SeedSessions(t, candidate, 3);

        var page = await BuildPractice(t.Db).GetHistoryAsync(candidate, cursor: "khong-phai-base64!!", limit: 2);

        Assert.Equal(new[] { ids[0], ids[1] }, page.Items.Select(x => x.Id).ToArray());
    }

    // ── (b) AsSplitQuery ──────────────────────────────────────────────────

    // Đếm SELECT chạm practice_answers: split query ⇒ ≥2 (answer riêng, scores riêng) thay vì 1 JOIN
    // lặp transcript trên mọi dòng score. Gỡ AsSplitQuery → 1 → ĐỎ.
    private sealed class AnswerQueryCounter : DbCommandInterceptor
    {
        public int Count;

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            if (command.CommandText.Contains("practice_answers", StringComparison.OrdinalIgnoreCase))
                Interlocked.Increment(ref Count);
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(ReaderExecuting(command, eventData, result));
    }

    [Fact]
    public async Task Db31_GetSession_UsesSplitQuery_AndKeepsScores()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var critA = TestDb.Criterion(JobCategory.BE, name: "Clarity");
        var critB = TestDb.Criterion(JobCategory.BE, name: "Depth");
        var session = TestDb.Session(candidate, SessionStatus.Scored);
        var q = TestDb.Question(session.Id);
        var a = TestDb.Answer(session.Id, q.Id, AnswerStatus.Scored, DateTime.UtcNow, DateTime.UtcNow);
        a.Transcript = "nội dung trả lời dài";
        t.Db.AddRange(critA, critB, session, q, a);
        t.Db.Add(new AnswerScore { AnswerId = a.Id, CriterionId = critA.Id, Score = 4m, RubricVersion = 1 });
        t.Db.Add(new AnswerScore { AnswerId = a.Id, CriterionId = critB.Id, Score = 5m, RubricVersion = 1 });
        await t.Db.SaveChangesAsync();

        var counter = new AnswerQueryCounter();
        var options = new DbContextOptionsBuilder<InterviewDbContext>()
            .UseSqlite(t.Connection).UseSnakeCaseNamingConvention()
            .AddInterceptors(counter).Options;
        using var db = new InterviewDbContext(options);

        var res = await BuildPractice(db).GetSessionAsync(candidate, session.Id);

        Assert.NotNull(res);
        Assert.True(counter.Count >= 2, $"kỳ vọng split query (≥2 truy vấn), thực tế {counter.Count}");
        // REGRESSION: split query KHÔNG được làm mất dữ liệu con.
        var answered = Assert.Single(res!.Questions, x => x.Answer is not null);
        Assert.Equal("nội dung trả lời dài", answered.Answer!.Transcript);
        Assert.Equal(2, answered.Answer.Scores.Count);
    }
}
