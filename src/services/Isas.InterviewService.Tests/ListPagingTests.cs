using Isas.InterviewService.ApplicationDbContext;
using Isas.InterviewService.Entities;
using Isas.InterviewService.Enums;
using Isas.InterviewService.Services;
using Isas.InterviewService.Services.Interfaces;
using Isas.Shared.Pagination;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Isas.InterviewService.Tests;

/// <summary>
/// Keyset pagination cho 3 endpoint danh sách trước nay KHÔNG có trần dòng nào:
/// `GET /api/files/files` · `GET /practice/roadmaps` · `GET /practice/cv-analysis`
/// (mẫu DB8/DB31 — cursor opaque `(CreatedAt DESC, Id DESC)`, body vẫn là mảng JSON, next-cursor
/// ở header `X-Next-Cursor`).
///
/// Kèm hai thứ KHÔNG phải phân trang nhưng cùng nằm trong cái giá phải trả của "list trả nguyên
/// entity/cả cây": files bỏ `parsed_text` (toàn văn CV) khỏi SQL, roadmap bỏ Include cây milestone.
/// </summary>
public class ListPagingTests
{
    // ── helpers ───────────────────────────────────────────────────────────

    private static StorageService BuildStorage(InterviewDbContext db)
        => new(NullLogger<StorageService>.Instance,
            new Mock<Amazon.S3.IAmazonS3>().Object,
            Microsoft.Extensions.Options.Options.Create(new Isas.InterviewService.Models.FileStorageOptions()),
            db);

    private static RoadmapService BuildRoadmap(InterviewDbContext db)
        => new(db,
            new Mock<IStorageService>().Object,
            new Mock<IAiServiceRoadmapGenerator>().Object,
            NullLogger<RoadmapService>.Instance);

    private static CvAnalysisService BuildCvAnalysis(InterviewDbContext db)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Billing:CvAnalysisCredits"] = "1" }).Build();
        return new CvAnalysisService(
            db,
            new Mock<IStorageService>().Object,
            new Mock<IAiServiceCvAnalyzer>().Object,
            new Mock<ICreditReservationClient>().Object,
            config,
            NullLogger<CvAnalysisService>.Instance);
    }

    // createdAt giảm dần (ids[0] = mới nhất) → khớp thứ tự kỳ vọng của keyset DESC.
    private static async Task<List<Guid>> SeedFiles(
        TestDb t, Guid userId, int n, string fileType = "cv", string parsedText = "TOÀN VĂN CV BÍ MẬT")
    {
        var ids = new List<Guid>();
        var now = DateTime.UtcNow;
        for (var i = 0; i < n; i++)
        {
            var f = new FileRecord
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                FileType = fileType,
                OriginalName = $"cv-{i}.pdf",
                StoragePath = $"cv/{userId}/{i}.pdf",
                StorageBucket = "isas",
                MimeType = "application/pdf",
                FileSize = 1024 + i,
                ParsedText = parsedText,
                ParseStatus = "Parsed",
                CreatedAt = now.AddMinutes(-i),
                UpdatedAt = now.AddMinutes(-i)
            };
            t.Db.Add(f);
            ids.Add(f.Id);
        }
        await t.Db.SaveChangesAsync();
        return ids;
    }

    private static async Task<List<Guid>> SeedRoadmaps(TestDb t, Guid candidateId, int n, int milestonesEach = 0)
    {
        var ids = new List<Guid>();
        var now = DateTime.UtcNow;
        for (var i = 0; i < n; i++)
        {
            var r = new Roadmap
            {
                Id = Guid.NewGuid(),
                CandidateId = candidateId,
                JobCategory = JobCategory.BE,
                Level = RoadmapLevel.Junior,
                CvId = null,                       // tránh FK file_records (SQLite CÓ enforce FK)
                Status = RoadmapStatus.Active,
                CreatedAt = now.AddMinutes(-i)
            };
            for (var m = 0; m < milestonesEach; m++)
                r.Milestones.Add(new RoadmapMilestone
                {
                    Id = Guid.NewGuid(),
                    OrderNo = m + 1,
                    Title = $"Chặng {m + 1}",
                    FocusCriteria = ["Clarity"],
                    Status = MilestoneStatus.Pending
                });
            t.Db.Add(r);
            ids.Add(r.Id);
        }
        await t.Db.SaveChangesAsync();
        return ids;
    }

    private static async Task<List<Guid>> SeedCvAnalyses(TestDb t, Guid candidateId, int n)
    {
        var ids = new List<Guid>();
        var now = DateTime.UtcNow;
        for (var i = 0; i < n; i++)
        {
            var a = new CvAnalysis
            {
                Id = Guid.NewGuid(),
                CandidateId = candidateId,
                CvId = Guid.NewGuid(),
                JobCategory = JobCategory.BE,
                Summary = $"Tóm tắt {i}",
                Strengths = ["Điểm mạnh"],
                Weaknesses = ["Điểm yếu"],
                Suggestions = ["Gợi ý"],
                CreatedAt = now.AddMinutes(-i)
            };
            t.Db.Add(a);
            ids.Add(a.Id);
        }
        await t.Db.SaveChangesAsync();
        return ids;
    }

    // ── (1) files: keyset ─────────────────────────────────────────────────

    // (a) limit chặn số dòng 1 trang + phát cursor. Gỡ Take/cursor → 5 dòng → ĐỎ.
    [Fact]
    public async Task Files_LimitCapsPage_AndEmitsCursor()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var ids = await SeedFiles(t, user, 5);

        var page = await BuildStorage(t.Db).GetFilesByUserId(user, limit: 2);

        Assert.Equal(2, page.Items.Count);
        Assert.Equal(new[] { ids[0], ids[1] }, page.Items.Select(x => x.Id).ToArray());
        Assert.NotNull(page.NextCursor);
    }

    // (b)+(c) đi hết trang bằng cursor → đủ, đúng thứ tự, KHÔNG trùng/sót; trang cuối hết cursor.
    [Fact]
    public async Task Files_CursorWalk_CoversEveryRowExactlyOnce()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var ids = await SeedFiles(t, user, 5);

        var seen = new List<Guid>();
        string? cursor = null;
        for (var guard = 0; guard < 10; guard++)
        {
            var page = await BuildStorage(t.Db).GetFilesByUserId(user, cursor: cursor, limit: 2);
            seen.AddRange(page.Items.Select(x => x.Id));
            cursor = page.NextCursor;
            if (cursor is null) break;
        }

        Assert.Null(cursor);
        Assert.Equal(ids, seen);
    }

    // (d) cursor rác KHÔNG được thành 500 — Decode tổng ⇒ coi như trang đầu.
    [Fact]
    public async Task Files_MalformedCursor_FallsBackToFirstPage()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var ids = await SeedFiles(t, user, 3);

        var page = await BuildStorage(t.Db).GetFilesByUserId(user, cursor: "khong-phai-base64!!", limit: 2);

        Assert.Equal(new[] { ids[0], ids[1] }, page.Items.Select(x => x.Id).ToArray());
    }

    // (e) không truyền cursor/limit ⇒ mặc định = trần 500 ⇒ hành vi y như trước.
    [Fact]
    public async Task Files_NoCursor_KeepsLegacyBehaviour()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        await SeedFiles(t, user, 5);

        var page = await BuildStorage(t.Db).GetFilesByUserId(user);

        Assert.Equal(5, page.Items.Count);
        Assert.Null(page.NextCursor);
        Assert.Equal(500, KeysetPaging.DefaultLimit);
    }

    // Phân trang KHÔNG được làm rò file của user khác.
    [Fact]
    public async Task Files_StillScopedToOwner()
    {
        using var t = new TestDb();
        var me = Guid.NewGuid();
        await SeedFiles(t, me, 2);
        await SeedFiles(t, Guid.NewGuid(), 3);

        var page = await BuildStorage(t.Db).GetFilesByUserId(me, limit: 500);

        Assert.Equal(2, page.Items.Count);
    }

    // Lọc fileType đẩy xuống SQL — lọc SAU khi lấy trang sẽ cho trang rỗng dù còn dữ liệu khớp.
    [Fact]
    public async Task Files_FileTypeFilter_IsPushedDown()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        await SeedFiles(t, user, 3, fileType: "cv");
        await SeedFiles(t, user, 2, fileType: "jd");

        var cv = await BuildStorage(t.Db).GetFilesByUserId(user, fileType: "cv", limit: 500);
        var jd = await BuildStorage(t.Db).GetFilesByUserId(user, fileType: "jd", limit: 500);
        var all = await BuildStorage(t.Db).GetFilesByUserId(user, limit: 500);

        Assert.Equal(3, cv.Items.Count);
        Assert.All(cv.Items, x => Assert.Equal("cv", x.FileType));
        Assert.Equal(2, jd.Items.Count);
        Assert.Equal(5, all.Items.Count);   // không truyền filter = không lọc (hành vi cũ)
    }

    // ── (2) files: parsed_text KHÔNG được rời khỏi DB ──────────────────────

    // Hợp đồng chính của task: DTO không có chỗ nào chứa toàn văn CV. Nếu ai đó đổi lại trả entity
    // FileRecord thì test này ĐỎ ngay (record summary không có property nào tên parsedText).
    [Fact]
    public async Task Files_ListPayload_HasNoParsedTextOrStorageCoordinates()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        await SeedFiles(t, user, 1, parsedText: "TOÀN VĂN CV BÍ MẬT");

        var page = await BuildStorage(t.Db).GetFilesByUserId(user);

        var item = Assert.Single(page.Items);
        var propNames = item.GetType().GetProperties().Select(p => p.Name).ToArray();
        Assert.DoesNotContain("ParsedText", propNames);
        Assert.DoesNotContain("StoragePath", propNames);
        Assert.DoesNotContain("StorageBucket", propNames);
        // Những gì màn hình danh sách THỰC SỰ cần thì vẫn còn.
        Assert.Equal("cv-0.pdf", item.OriginalName);
        Assert.Equal("Parsed", item.ParseStatus);
    }

    // Mạnh hơn test trên: chứng minh cột parsed_text KHÔNG BỊ ĐỌC LÊN từ DB, chứ không phải chỉ bị
    // giấu ở tầng JSON. Bắt SQL thật bằng interceptor. Đổi `.Select(DTO)` thành nạp entity rồi map
    // sau ⇒ SQL có "parsed_text" ⇒ ĐỎ. Đây là điểm khác biệt giữa "ẩn đi" và "không đọc".
    [Fact]
    public async Task Files_ListQuery_NeverSelectsParsedTextColumn()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        await SeedFiles(t, user, 3);

        var spy = new SqlSpy();
        var options = new DbContextOptionsBuilder<InterviewDbContext>()
            .UseSqlite(t.Connection).UseSnakeCaseNamingConvention()
            .AddInterceptors(spy).Options;
        using var db = new InterviewDbContext(options);

        await BuildStorage(db).GetFilesByUserId(user, limit: 500);

        var fileQueries = spy.Commands
            .Where(c => c.Contains("file_records", StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.NotEmpty(fileQueries);
        Assert.All(fileQueries, sql =>
            Assert.DoesNotContain("parsed_text", sql, StringComparison.OrdinalIgnoreCase));
    }

    private sealed class SqlSpy : Microsoft.EntityFrameworkCore.Diagnostics.DbCommandInterceptor
    {
        private readonly List<string> _commands = [];
        public IReadOnlyList<string> Commands { get { lock (_commands) return _commands.ToList(); } }

        private void Record(System.Data.Common.DbCommand command)
        {
            lock (_commands) _commands.Add(command.CommandText);
        }

        public override Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader> ReaderExecuting(
            System.Data.Common.DbCommand command,
            Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader> result)
        {
            Record(command);
            return result;
        }

        public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader>> ReaderExecutingAsync(
            System.Data.Common.DbCommand command,
            Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Record(command);
            return ValueTask.FromResult(result);
        }
    }

    // ── (3) roadmaps: keyset + bỏ cây milestone ───────────────────────────

    [Fact]
    public async Task Roadmaps_LimitCapsPage_AndEmitsCursor()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var ids = await SeedRoadmaps(t, candidate, 5);

        var page = await BuildRoadmap(t.Db).ListAsync(candidate, cursor: null, limit: 2);

        Assert.Equal(2, page.Items.Count);
        Assert.Equal(new[] { ids[0], ids[1] }, page.Items.Select(x => x.Id).ToArray());
        Assert.NotNull(page.NextCursor);
    }

    [Fact]
    public async Task Roadmaps_CursorWalk_CoversEveryRowExactlyOnce()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var ids = await SeedRoadmaps(t, candidate, 5);

        var seen = new List<Guid>();
        string? cursor = null;
        for (var guard = 0; guard < 10; guard++)
        {
            var page = await BuildRoadmap(t.Db).ListAsync(candidate, cursor, limit: 2);
            seen.AddRange(page.Items.Select(x => x.Id));
            cursor = page.NextCursor;
            if (cursor is null) break;
        }

        Assert.Null(cursor);
        Assert.Equal(ids, seen);
    }

    [Fact]
    public async Task Roadmaps_MalformedCursor_FallsBackToFirstPage()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var ids = await SeedRoadmaps(t, candidate, 3);

        var page = await BuildRoadmap(t.Db).ListAsync(candidate, cursor: "rác!!", limit: 2);

        Assert.Equal(new[] { ids[0], ids[1] }, page.Items.Select(x => x.Id).ToArray());
    }

    [Fact]
    public async Task Roadmaps_NoCursor_KeepsLegacyBehaviour()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        await SeedRoadmaps(t, candidate, 5);

        var page = await BuildRoadmap(t.Db).ListAsync(candidate);

        Assert.Equal(5, page.Items.Count);
        Assert.Null(page.NextCursor);
    }

    [Fact]
    public async Task Roadmaps_StillScopedToOwner()
    {
        using var t = new TestDb();
        var me = Guid.NewGuid();
        await SeedRoadmaps(t, me, 2);
        await SeedRoadmaps(t, Guid.NewGuid(), 3);

        var page = await BuildRoadmap(t.Db).ListAsync(me, limit: 500);

        Assert.Equal(2, page.Items.Count);
    }

    // List KHÔNG được kéo bảng milestone/lesson. Trả lại Include cây ⇒ SQL chạm roadmap_milestones ⇒ ĐỎ.
    [Fact]
    public async Task Roadmaps_ListQuery_DoesNotTouchMilestoneTables()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        await SeedRoadmaps(t, candidate, 3, milestonesEach: 2);

        var spy = new SqlSpy();
        var options = new DbContextOptionsBuilder<InterviewDbContext>()
            .UseSqlite(t.Connection).UseSnakeCaseNamingConvention()
            .AddInterceptors(spy).Options;
        using var db = new InterviewDbContext(options);

        var page = await BuildRoadmap(db).ListAsync(candidate, limit: 500);

        Assert.Equal(3, page.Items.Count);
        Assert.All(spy.Commands, sql =>
            Assert.DoesNotContain("roadmap_milestones", sql, StringComparison.OrdinalIgnoreCase));
    }

    // Chi tiết KHÔNG được rụng cây theo — regression cho việc list bỏ Include.
    [Fact]
    public async Task Roadmaps_DetailStillReturnsFullTree()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var ids = await SeedRoadmaps(t, candidate, 1, milestonesEach: 2);

        var detail = await BuildRoadmap(t.Db).GetAsync(candidate, ids[0]);

        Assert.NotNull(detail);
        Assert.Equal(2, detail!.Milestones.Count);
    }

    // ── (4) cv-analysis: keyset (shape GIỮ NGUYÊN) ────────────────────────

    [Fact]
    public async Task CvAnalysis_LimitCapsPage_AndEmitsCursor()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var ids = await SeedCvAnalyses(t, candidate, 5);

        var page = await BuildCvAnalysis(t.Db).ListAsync(candidate, cursor: null, limit: 2);

        Assert.Equal(2, page.Items.Count);
        Assert.Equal(new[] { ids[0], ids[1] }, page.Items.Select(x => x.Id).ToArray());
        Assert.NotNull(page.NextCursor);
    }

    [Fact]
    public async Task CvAnalysis_CursorWalk_CoversEveryRowExactlyOnce()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var ids = await SeedCvAnalyses(t, candidate, 5);

        var seen = new List<Guid>();
        string? cursor = null;
        for (var guard = 0; guard < 10; guard++)
        {
            var page = await BuildCvAnalysis(t.Db).ListAsync(candidate, cursor, limit: 2);
            seen.AddRange(page.Items.Select(x => x.Id));
            cursor = page.NextCursor;
            if (cursor is null) break;
        }

        Assert.Null(cursor);
        Assert.Equal(ids, seen);
    }

    [Fact]
    public async Task CvAnalysis_MalformedCursor_FallsBackToFirstPage()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var ids = await SeedCvAnalyses(t, candidate, 3);

        var page = await BuildCvAnalysis(t.Db).ListAsync(candidate, cursor: "%%%", limit: 2);

        Assert.Equal(new[] { ids[0], ids[1] }, page.Items.Select(x => x.Id).ToArray());
    }

    [Fact]
    public async Task CvAnalysis_NoCursor_KeepsLegacyBehaviour()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        await SeedCvAnalyses(t, candidate, 5);

        var page = await BuildCvAnalysis(t.Db).ListAsync(candidate);

        Assert.Equal(5, page.Items.Count);
        Assert.Null(page.NextCursor);
    }

    [Fact]
    public async Task CvAnalysis_StillScopedToOwner()
    {
        using var t = new TestDb();
        var me = Guid.NewGuid();
        await SeedCvAnalyses(t, me, 2);
        await SeedCvAnalyses(t, Guid.NewGuid(), 3);

        var page = await BuildCvAnalysis(t.Db).ListAsync(me, limit: 500);

        Assert.Equal(2, page.Items.Count);
    }

    // Shape KHÔNG đổi — FE render đủ các mảng này NGAY trên trang danh sách (không có màn chi tiết),
    // và chúng là `string[]` non-optional được @for duyệt thẳng ⇒ thiếu = văng runtime, không phải
    // "thiếu chữ". Khoá lại để lần sau ai định cắt cho gọn thì phải làm trang chi tiết trước.
    [Fact]
    public async Task CvAnalysis_ListPayload_KeepsFieldsFeRendersInline()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        await SeedCvAnalyses(t, candidate, 1);

        var page = await BuildCvAnalysis(t.Db).ListAsync(candidate);

        var item = Assert.Single(page.Items);
        Assert.Equal("Tóm tắt 0", item.Summary);
        Assert.Equal(["Điểm mạnh"], item.Strengths);
        Assert.Equal(["Điểm yếu"], item.Weaknesses);
        Assert.Equal(["Gợi ý"], item.Suggestions);
    }

    // ── (5) hợp đồng dịch Npgsql ──────────────────────────────────────────

    // Keyset chỉ có nghĩa nếu Npgsql DỊCH ĐƯỢC vị ngữ + ORDER BY xuống SQL. Nếu EF không dịch được
    // (vd Guid.CompareTo) nó sẽ đánh giá phía client — trên SQLite test vẫn XANH trong im lặng còn
    // production thì kéo cả bảng về rồi mới cắt. Probe bằng provider Npgsql thật (không cần DB chạy).
    [Fact]
    public void KeysetPredicateAndOrdering_TranslateOnNpgsql()
    {
        var opt = new DbContextOptionsBuilder<InterviewDbContext>()
            .UseNpgsql("Host=localhost;Database=probe;Username=x;Password=y")
            .UseSnakeCaseNamingConvention()
            .Options;
        using var db = new InterviewDbContext(opt);

        var cur = new KeysetCursor(DateTime.UtcNow, Guid.NewGuid());
        var owner = Guid.NewGuid();

        // (a) file_records — kèm projection DTO: khẳng định parsed_text KHÔNG có trong SQL Postgres
        // (test interceptor ở trên chạy trên SQLite; đây là provider thật sẽ chạy production).
        var files = db.FileRecords.AsNoTracking()
            .Where(f => f.UserId == owner)
            .Where(f => f.CreatedAt < cur.CreatedAt
                || (f.CreatedAt == cur.CreatedAt && f.Id.CompareTo(cur.Id) < 0))
            .OrderByDescending(f => f.CreatedAt).ThenByDescending(f => f.Id)
            .Take(2)
            .Select(f => new { f.Id, f.FileType, f.OriginalName })
            .ToQueryString();

        // Thứ tự keyset phải do SQL làm, không phải sắp trong bộ nhớ.
        Assert.Contains("ORDER BY f.created_at DESC, f.id DESC", files);
        Assert.Contains("LIMIT", files);
        // Guid.CompareTo PHẢI dịch thành so sánh uuid trong WHERE. Không dịch được thì EF đánh giá
        // phía client ⇒ kéo cả bảng về rồi mới cắt (SQLite test vẫn xanh, production thì không).
        Assert.Contains("f.id < @cur_Id", files);
        Assert.Contains("f.created_at < @cur_CreatedAt", files);
        // Projection DTO: toàn văn CV không có trong câu SQL mà production sẽ chạy.
        Assert.DoesNotContain("parsed_text", files);

        // (b) roadmaps — không được JOIN sang bảng milestone.
        var roadmaps = db.Set<Roadmap>().AsNoTracking()
            .Where(x => x.CandidateId == owner)
            .Where(x => x.CreatedAt < cur.CreatedAt
                || (x.CreatedAt == cur.CreatedAt && x.Id.CompareTo(cur.Id) < 0))
            .OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id)
            .Take(2)
            .Select(x => new { x.Id, x.Status })
            .ToQueryString();

        Assert.Contains("ORDER BY r.created_at DESC, r.id DESC", roadmaps);
        Assert.Contains("LIMIT", roadmaps);
        Assert.Contains("r.id < @cur_Id", roadmaps);
        Assert.DoesNotContain("roadmap_milestones", roadmaps);

        // (c) cv_analyses.
        var cvs = db.Set<CvAnalysis>().AsNoTracking()
            .Where(x => x.CandidateId == owner)
            .Where(x => x.CreatedAt < cur.CreatedAt
                || (x.CreatedAt == cur.CreatedAt && x.Id.CompareTo(cur.Id) < 0))
            .OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id)
            .Take(2)
            .ToQueryString();

        Assert.Contains("ORDER BY c.created_at DESC, c.id DESC", cvs);
        Assert.Contains("LIMIT", cvs);
        Assert.Contains("c.id < @cur_Id", cvs);
    }
}
