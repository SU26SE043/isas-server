using Isas.InterviewService.ApplicationDbContext;
using Isas.InterviewService.DTOs;
using Isas.InterviewService.Entities;
using Isas.InterviewService.Enums;
using Isas.InterviewService.Services;
using Isas.InterviewService.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Isas.InterviewService.Tests;

/// <summary>
/// UX3-B2 — <c>RoadmapSummaryResponse.MilestoneCount</c> / <c>MilestoneDoneCount</c>.
///
/// <para>Trang danh sách lộ trình trước đây gọi <c>GET /roadmaps/{id}</c> cho TỪNG thẻ mỗi lần mở
/// dashboard (FE chờ <c>progressPercent</c>/<c>currentMilestoneId</c> — không tồn tại — nên nhánh
/// làm giàu chạy 100% số lần). Hai con số đếm để FE tự tính % tiến độ mà khỏi vòng đó.</para>
///
/// <para>Hai lớp test theo mẫu <see cref="RootQuestionAggregationAdp1Tests"/>: ca SỐ chạy qua
/// <see cref="RoadmapService.ListAsync"/> thật trên SQLite (bắt "production trả sai số"); ca KHOÁ SQL
/// soi <c>ToQueryString</c> của <see cref="RoadmapService.BuildSummaryQuery"/> trên provider Npgsql
/// thật (bắt "đổi sang Include kéo cả cây").</para>
/// </summary>
public class RoadmapMilestoneCountsUx3B2Tests
{
    private static RoadmapService Service(TestDb t)
        => new(t.Db, new Mock<IStorageService>().Object,
               new Mock<IAiServiceRoadmapGenerator>().Object, NullLogger<RoadmapService>.Instance);

    private static Roadmap Row(Guid owner, DateTime createdAt, params MilestoneStatus[] milestoneStatuses)
    {
        var r = new Roadmap
        {
            Id = Guid.NewGuid(),
            CandidateId = owner,
            JobCategory = JobCategory.BE,
            Level = RoadmapLevel.Middle,
            Mode = RoadmapMode.LevelUp,
            Language = "vi",
            Status = RoadmapStatus.Active,
            CreatedAt = createdAt,
        };
        var no = 0;
        foreach (var st in milestoneStatuses)
            r.Milestones.Add(new RoadmapMilestone
            {
                Id = Guid.NewGuid(), RoadmapId = r.Id, OrderNo = ++no,
                Title = $"Chặng {no}", Status = st,
            });
        return r;
    }

    [Fact]
    public async Task List_LoTrinh4Chang2HoanTat_TraCount4_DoneCount2()
    {
        using var t = new TestDb();
        var owner = Guid.NewGuid();
        t.Db.Set<Roadmap>().Add(Row(owner, DateTime.UtcNow,
            MilestoneStatus.Completed, MilestoneStatus.Completed,
            MilestoneStatus.InProgress, MilestoneStatus.Pending));
        await t.Db.SaveChangesAsync();

        var item = Assert.Single((await Service(t).ListAsync(owner)).Items);

        Assert.Equal(4, item.MilestoneCount);
        Assert.Equal(2, item.MilestoneDoneCount);
    }

    [Fact]
    public async Task List_LoTrinhKhongChang_Tra0Va0()
    {
        using var t = new TestDb();
        var owner = Guid.NewGuid();
        t.Db.Set<Roadmap>().Add(Row(owner, DateTime.UtcNow));   // 0 milestone
        await t.Db.SaveChangesAsync();

        var item = Assert.Single((await Service(t).ListAsync(owner)).Items);

        Assert.Equal(0, item.MilestoneCount);
        Assert.Equal(0, item.MilestoneDoneCount);
    }

    [Fact]
    public async Task List_MoiLoTrinh_DemDungChangCuaChinhNo()
    {
        using var t = new TestDb();
        var owner = Guid.NewGuid();
        var now = DateTime.UtcNow;
        // r1 mới hơn → đứng trước; 3 chặng, 1 Done. r2 cũ hơn; 2 chặng, 2 Done.
        t.Db.Set<Roadmap>().AddRange(
            Row(owner, now, MilestoneStatus.Completed, MilestoneStatus.Pending, MilestoneStatus.InProgress),
            Row(owner, now.AddMinutes(-5), MilestoneStatus.Completed, MilestoneStatus.Completed));
        await t.Db.SaveChangesAsync();

        var items = (await Service(t).ListAsync(owner)).Items;

        Assert.Equal(2, items.Count);
        Assert.Equal((3, 1), (items[0].MilestoneCount, items[0].MilestoneDoneCount));
        Assert.Equal((2, 2), (items[1].MilestoneCount, items[1].MilestoneDoneCount));
    }

    /// <summary>
    /// Khoá hợp đồng SQL: hai con số đếm chặng PHẢI là subquery scalar <c>COUNT(*)</c> trong CÙNG một
    /// câu truy vấn — KHÔNG <c>LEFT JOIN roadmap_milestones</c> kéo cả cây (docblock
    /// <see cref="RoadmapSummaryResponse"/>). Soi trên provider Npgsql THẬT (không cần DB chạy).
    /// Đổi <see cref="RoadmapService.BuildSummaryQuery"/> sang <c>.Include(x =&gt; x.Milestones)</c>
    /// + đếm trong bộ nhớ ⇒ test này ĐỎ.
    /// </summary>
    [Fact]
    public void BuildSummaryQuery_DemChangLaSubqueryScalar_KhongJoinCaCay()
    {
        var opt = new DbContextOptionsBuilder<InterviewDbContext>()
            .UseNpgsql("Host=localhost;Database=probe;Username=x;Password=y")
            .UseSnakeCaseNamingConvention()
            .Options;
        using var db = new InterviewDbContext(opt);

        var owner = Guid.NewGuid();
        var sql = RoadmapService.BuildSummaryQuery(
            db.Set<Roadmap>().AsNoTracking().Where(x => x.CandidateId == owner), 20).ToQueryString();

        // Hai con số đếm có mặt và ở dạng COUNT.
        Assert.Contains("count(*)", sql, StringComparison.OrdinalIgnoreCase);
        // ... TRÊN bảng milestone, tính TRONG SQL.
        Assert.Contains("roadmap_milestones", sql);
        // Nhưng KHÔNG phải bằng cách JOIN kéo cả cây: milestone chỉ được chạm qua subquery tương quan
        // (m.roadmap_id = r.id), KHÔNG có "JOIN roadmap_milestones" ở mức FROM của câu ngoài.
        Assert.DoesNotContain("JOIN roadmap_milestones", sql, StringComparison.OrdinalIgnoreCase);
        // Và tuyệt đối không đụng bảng lesson (cây con của milestone).
        Assert.DoesNotContain("roadmap_lessons", sql);
        // Keyset vẫn do SQL sắp + cắt, không phải bộ nhớ.
        Assert.Contains("ORDER BY r.created_at DESC, r.id DESC", sql);
        Assert.Contains("LIMIT", sql);
    }
}
