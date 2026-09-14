using System.Data.Common;
using Isas.CampaignService.DTOs;
using Isas.CampaignService.Models;
using Isas.CampaignService.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Moq;

using CampaignSvc = Isas.CampaignService.Services.CampaignService;

namespace Isas.CampaignService.Tests;

/// <summary>
/// BUG-1 (L3 dev, Postgres): <c>PUT /campaign/{id}</c> với <c>criteria[]</c> ECHO id tiêu chí đang có mốc ⇒
/// <b>500 DbUpdateConcurrencyException "expected to affect 1 row(s), but actually affected 0"</b> — cả khi gửi
/// <c>levels</c> lẫn khi không gửi (carry-over CAMP-16). Có sẵn từ RNK1·HĐ-5 (merge theo id), SC2 biến thành
/// đường đi thường xuyên vì FE luôn echo id.
///
/// <para><b>Gốc:</b> <c>campaign_criterion_levels.id</c> là store-generated (<c>HasDefaultValueSql</c>). Với tiêu chí
/// TÁI DÙNG (đang tracked), <c>target.Levels.Clear()</c> rồi <c>target.Levels.Add(mốc mới có Id gán sẵn)</c> ⇒ EF
/// DetectChanges gặp entity qua fixup navigation với khoá KHÁC default của một khoá store-generated ⇒ coi là
/// <b>Modified</b> (row có sẵn) ⇒ sinh UPDATE chứ không INSERT ⇒ 0 row ⇒ ném. Đúng bẫy F9 đã ghi ở
/// <c>GenerateCampaignQuestionsAsync</c> ("DbSet.AddRange chứ KHÔNG campaign.Questions.Add()"). Tiêu chí MỚI không
/// dính vì đi qua <c>_db.CampaignCriteria.AddRange(added)</c> đánh Added cả graph.</para>
///
/// <para>Mọi test ở đây seed qua context RIÊNG rồi gọi service qua context KHÁC — đúng hình dạng production
/// (Include Criteria.ThenInclude(Levels) ⇒ mốc cũ tracked).</para>
/// </summary>
public class CampaignCriterionLevelsEchoIdBug1Tests
{
    private const string D0 = "CÓ: không nêu được ý nào | CÒN THIẾU: toàn bộ nội dung câu hỏi";
    private const string D3 = "CÓ: nêu đúng khái niệm | CÒN THIẾU: ví dụ và đánh đổi";
    private const string D5 = "CÓ: nêu đúng khái niệm và ví dụ | CÒN THIẾU: không có đánh đổi";
    private const string D2Moi = "CÓ: nêu được một nửa ý | CÒN THIẾU: nửa còn lại và ví dụ";
    private const string D5Moi = "CÓ: nêu khái niệm, ví dụ, đánh đổi | CÒN THIẾU: chưa nói giới hạn";

    /// <summary>Ghi lại mọi câu lệnh SQL không phải SELECT — để khoá HÌNH DẠNG (INSERT vs UPDATE) cho mốc mới.</summary>
    private sealed class SqlCapture : DbCommandInterceptor
    {
        public List<string> Statements { get; } = new();
        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        { Statements.Add(command.CommandText); return result; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken ct = default)
        { Statements.Add(command.CommandText); return ValueTask.FromResult(result); }
        public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
        { Statements.Add(command.CommandText); return result; }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
        { Statements.Add(command.CommandText); return ValueTask.FromResult(result); }
    }

    private static CampaignSvc NewService(CampaignDbContext db) =>
        new(db, Mock.Of<IFileService>(), Mock.Of<ILogger<CampaignSvc>>(),
            Mock.Of<IParserService>(), Mock.Of<ICriteriaSuggester>(), Mock.Of<IInvitationEmailPublisher>());

    private static CampaignCriterionLevel Level(Guid criterionId, int score, string d)
        => new() { Id = Guid.NewGuid(), CriterionId = criterionId, Score = score, Descriptor = d, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };

    /// <summary>Campaign Draft + 2 tiêu chí (A, B) mỗi cái 3 mốc {0,3,5} — seed qua context riêng đã dispose.</summary>
    private static async Task<(Guid CampId, CampaignCriterion A, CampaignCriterion B)> SeedAsync(CampaignTestDb tdb, Guid owner)
    {
        using var db = tdb.NewContext();
        var camp = CampaignTestDb.NewCampaign(owner, CampaignStatus.Draft);
        db.Campaigns.Add(camp);
        CampaignCriterion Crit(int order, string name) => new()
        {
            Id = Guid.NewGuid(), CampaignId = camp.Id, OrderNo = order, Name = name, Weight = 0.5m, MaxScore = 5,
            Source = CriterionSource.HrEdited, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        };
        var a = Crit(0, "A"); var b = Crit(1, "B");
        db.CampaignCriteria.AddRange(a, b);
        foreach (var c in new[] { a, b })
            db.CampaignCriterionLevels.AddRange(Level(c.Id, 0, D0), Level(c.Id, 3, D3), Level(c.Id, 5, D5));
        await db.SaveChangesAsync();
        return (camp.Id, a, b);
    }

    private static async Task<List<CampaignCriterionLevel>> LevelsAsync(CampaignTestDb tdb, Guid campaignId)
    {
        using var check = tdb.NewContext();
        return await check.CampaignCriterionLevels.AsNoTracking()
            .Where(l => l.Criterion.CampaignId == campaignId)
            .OrderBy(l => l.Criterion.OrderNo).ThenBy(l => l.Score).ToListAsync();
    }

    private static CriterionItem Echo(CampaignCriterion c, List<CriterionLevelItem>? levels, string? name = null, decimal? weight = null)
        => new() { Id = c.Id, Name = name ?? c.Name, Weight = weight ?? c.Weight, MaxScore = c.MaxScore, Levels = levels };

    private static List<CriterionLevelItem> Lv(params (int Score, string D)[] items)
        => items.Select(i => new CriterionLevelItem { Score = i.Score, Descriptor = i.D }).ToList();

    // (a) echo id + levels MỚI ⇒ SaveChanges thành công, mốc cũ xoá, mốc mới INSERT (id đổi, nội dung đúng)
    [Fact]
    public async Task EchoId_KemLevelsMoi_LuuDuoc_MocCuXoa_MocMoiInsert()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var (campId, a, b) = await SeedAsync(tdb, owner);
        var before = await LevelsAsync(tdb, campId);
        Assert.Equal(6, before.Count);

        await NewService(tdb.NewContext()).UpdateCampaignAsync(owner, owner, campId, new UpdateCampaignRequest
        {
            Criteria = new List<CriterionItem>
            {
                Echo(a, Lv((0, D0), (2, D2Moi), (5, D5Moi))),
                Echo(b, Lv((0, D0), (2, D2Moi), (5, D5Moi))),
            }
        }, default);

        var after = await LevelsAsync(tdb, campId);
        Assert.Equal(6, after.Count);
        Assert.Empty(after.Select(l => l.Id).Intersect(before.Select(l => l.Id)));   // mốc mới, id mới
        Assert.Equal(new[] { 0, 2, 5, 0, 2, 5 }, after.Select(l => l.Score));
        Assert.Equal(D2Moi, after[1].Descriptor);
        Assert.Equal(new[] { a.Id, a.Id, a.Id, b.Id, b.Id, b.Id }, after.Select(l => l.CriterionId));   // id tiêu chí GIỮ
    }

    // (b) echo id, KHÔNG gửi levels ⇒ carry-over: lưu được, mốc giữ nội dung (rebuild ⇒ id mới)
    [Fact]
    public async Task EchoId_KhongGuiLevels_CarryOver_LuuDuoc_NoiDungGiu()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var (campId, a, b) = await SeedAsync(tdb, owner);

        await NewService(tdb.NewContext()).UpdateCampaignAsync(owner, owner, campId, new UpdateCampaignRequest
        {
            Criteria = new List<CriterionItem> { Echo(a, levels: null), Echo(b, levels: null) }
        }, default);

        var after = await LevelsAsync(tdb, campId);
        Assert.Equal(6, after.Count);
        Assert.Equal(new[] { 0, 3, 5, 0, 3, 5 }, after.Select(l => l.Score));
        Assert.Equal(new[] { D0, D3, D5, D0, D3, D5 }, after.Select(l => l.Descriptor));
    }

    // (c) đổi tên A + xoá B ⇒ lưu được; mốc của B đi theo (Cascade), A giữ id + mốc mới
    [Fact]
    public async Task EchoId_DoiTenA_XoaB_LuuDuoc_MocBMat_MocAInsert()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var (campId, a, b) = await SeedAsync(tdb, owner);

        await NewService(tdb.NewContext()).UpdateCampaignAsync(owner, owner, campId, new UpdateCampaignRequest
        {
            Criteria = new List<CriterionItem> { Echo(a, Lv((0, D0), (5, D5Moi)), name: "A đổi tên", weight: 1.0m) }
        }, default);

        var after = await LevelsAsync(tdb, campId);
        Assert.Equal(2, after.Count);
        Assert.All(after, l => Assert.Equal(a.Id, l.CriterionId));
        Assert.Equal(new[] { 0, 5 }, after.Select(l => l.Score));
        using var check = tdb.NewContext();
        Assert.Equal(new[] { "A đổi tên" }, await check.CampaignCriteria.Where(c => c.CampaignId == campId).Select(c => c.Name).ToListAsync());
        Assert.Equal(0, await check.CampaignCriterionLevels.CountAsync(l => l.CriterionId == b.Id));
    }

    /// <summary>
    /// Khoá HÌNH DẠNG SQL — chính thứ đo được trên Postgres: mốc mới của tiêu chí tái dùng phải đi bằng INSERT
    /// vào <c>campaign_criterion_levels</c>, KHÔNG có UPDATE nào lên bảng đó (UPDATE = entity bị track Modified =
    /// 0 row trên Postgres). Bảo hiểm cho ca SQLite nào đó không ném dù state sai.
    /// </summary>
    [Fact]
    public async Task EchoId_SqlShape_MocMoiLaINSERT_KhongUPDATE()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var (campId, a, b) = await SeedAsync(tdb, owner);
        var capture = new SqlCapture();

        await NewService(tdb.NewContext(capture)).UpdateCampaignAsync(owner, owner, campId, new UpdateCampaignRequest
        {
            Criteria = new List<CriterionItem> { Echo(a, Lv((0, D0), (5, D5Moi))), Echo(b, levels: null) }
        }, default);

        var onLevels = capture.Statements.Where(s => s.Contains("campaign_criterion_levels", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.Contains(onLevels, s => s.TrimStart().StartsWith("INSERT", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(onLevels, s => s.TrimStart().StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(onLevels, s => s.TrimStart().StartsWith("DELETE", StringComparison.OrdinalIgnoreCase));   // mốc cũ orphan-delete
    }
}
