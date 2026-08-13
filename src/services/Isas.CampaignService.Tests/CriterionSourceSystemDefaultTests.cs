using Isas.CampaignService.Models;
using Microsoft.EntityFrameworkCore;

namespace Isas.CampaignService.Tests;

/// <summary>
/// CAMP-20 — <c>CriterionSource.SystemDefault</c> là giá trị THỨ BA của
/// <c>ck_campaign_criteria_source</c>, KHÔNG thay <c>AiSuggested</c>.
///
/// <para>SQLite (snake_case TestDb) CÓ enforce CHECK trên EF10 (tiền lệ
/// <c>CriterionWeightRangeCheckTests</c>), nên nhóm test này chứng minh được cả ba vế: giá trị mới
/// được nhận, hai giá trị cũ vẫn hợp lệ (⇒ hàng đang có không cần backfill), và chuỗi ngoài tập bị
/// chặn (⇒ CHECK là một danh sách đóng chứ không phải cột tự do).</para>
///
/// <para>⚠ Cái nhóm test này KHÔNG chứng minh được — nói thẳng để không ai tin nhầm: <b>thứ tự
/// deploy</b>. SQLite dựng schema bằng <c>EnsureCreated</c> theo model hiện tại, tức luôn là bản CHECK
/// đã nới; DB thật thì apply migration là một bước riêng. Deploy code trước migration ⇒ <c>23514</c>
/// trên mọi lượt publish rơi vào nhánh AI-lỗi, mà ở đây vẫn xanh 100%.</para>
/// </summary>
public class CriterionSourceSystemDefaultTests
{
    // Seed 1 campaign (parent FK — SQLite EF10 enforce FK, bài học DB9) + 1 criterion mang `source`
    // đã cho. Ghi source qua SQL thô cho ca "giá trị ngoài enum" vì C# không dựng được enum lạ.
    private static async Task<Exception?> InsertCriterionAsync(CampaignTestDb tdb, CriterionSource source)
    {
        var camp = CampaignTestDb.NewCampaign(Guid.NewGuid());
        tdb.Db.Campaigns.Add(camp);
        await tdb.Db.SaveChangesAsync();

        tdb.Db.CampaignCriteria.Add(new CampaignCriterion
        {
            Id = Guid.NewGuid(),
            CampaignId = camp.Id,
            OrderNo = 0,
            Name = "Crit",
            Weight = 1.0m,
            MaxScore = 5,
            Source = source,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        return await Record.ExceptionAsync(() => tdb.Db.SaveChangesAsync());
    }

    // Giá trị MỚI qua được CHECK — vế chính của B1.
    [Fact]
    public async Task SystemDefault_Passes_Check()
    {
        using var tdb = new CampaignTestDb();

        var ex = await InsertCriterionAsync(tdb, CriterionSource.SystemDefault);

        Assert.Null(ex);
    }

    // Hai giá trị CŨ vẫn hợp lệ ⇒ migration là thuần additive, hàng đang có không phải backfill.
    [Theory]
    [InlineData(CriterionSource.AiSuggested)]
    [InlineData(CriterionSource.HrEdited)]
    public async Task GiaTriCu_VanHopLe(CriterionSource source)
    {
        using var tdb = new CampaignTestDb();

        var ex = await InsertCriterionAsync(tdb, source);

        Assert.Null(ex);
    }

    // CHECK là danh sách ĐÓNG: chuỗi ngoài tập bị chặn ở tầng DB, không phải cột varchar tự do.
    // Ghi bằng SQL thô — đây là đường duy nhất tạo được giá trị mà enum C# không diễn đạt nổi.
    //
    // ⚠ Kèm ĐỐI CHỨNG DƯƠNG trong CÙNG một test: chạy y hệt câu INSERT đó với 'SystemDefault' và đòi
    // nó THÀNH CÔNG. Thiếu vế này thì Assert.NotNull(ex) xanh kể cả khi SQL thô của chính test sai cú
    // pháp/sai tên cột — tức test "chứng minh" CHECK hoạt động bằng một lỗi hoàn toàn khác.
    [Fact]
    public async Task GiaTriLa_ViPhamCheck()
    {
        using var tdb = new CampaignTestDb();
        var camp = CampaignTestDb.NewCampaign(Guid.NewGuid());
        tdb.Db.Campaigns.Add(camp);
        await tdb.Db.SaveChangesAsync();

        // 🔴 Đọc LẠI id campaign ĐÚNG NHƯ SQLite đang lưu, không dùng `camp.Id.ToString()`. EF ghi Guid
        // vào SQLite dạng TEXT chữ HOA, còn `Guid.ToString()` cho chữ THƯỜNG — mà so sánh TEXT của
        // SQLite phân biệt hoa/thường ⇒ hàng chèn kiểu đó KHÔNG khớp FK, KHÔNG đụng UNIQUE và không
        // câu WHERE nào của EF thấy. Lần viết đầu của chính test này dính đúng bẫy đó: nó "xanh" vì
        // `FOREIGN KEY constraint failed`, tức chứng minh CHECK bằng một lỗi hoàn toàn khác.
        var campIdText = await tdb.Db.Database
            .SqlQueryRaw<string>("SELECT id AS Value FROM campaigns").SingleAsync();

        var bad = await Record.ExceptionAsync(() => InsertRawAsync(tdb, campIdText, "KhongPhaiNguonHopLe"));
        var good = await Record.ExceptionAsync(() => InsertRawAsync(tdb, campIdText, "SystemDefault"));

        Assert.NotNull(bad);
        Assert.Null(good);   // đối chứng: câu INSERT tự nó hợp lệ ⇒ `bad` đỏ vì CHECK, không vì cú pháp/FK
    }

    private static Task InsertRawAsync(CampaignTestDb tdb, string campaignIdText, string source)
        => tdb.Db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO campaign_criteria
                (id, campaign_id, order_no, name, weight, max_score, source, created_at, updated_at)
            VALUES ({0}, {1}, {2}, {3}, 1.0, 5, {4}, {5}, {5})
            """,
            Guid.NewGuid().ToString().ToUpperInvariant(), campaignIdText,
            // order_no/name unique theo campaign → mỗi lần chèn phải khác, kẻo `good` đỏ vì UNIQUE.
            Random.Shared.Next(1, 100_000), $"Crit-{Guid.NewGuid():N}",
            source, DateTime.UtcNow);

    // Enum lưu dạng CHUỖI (GEN-2), và chuỗi đó phải khớp NGUYÊN VĂN vế CHECK + giá trị hàng cũ trên
    // DB thật. Đổi tên hằng số C# mà quên migration ⇒ 23514 trên Postgres, ở đây thì im lặng.
    [Fact]
    public async Task LuuDangChuoi_TenKhopCheck()
    {
        using var tdb = new CampaignTestDb();
        await InsertCriterionAsync(tdb, CriterionSource.SystemDefault);

        using var check = tdb.NewContext();
        var stored = await check.Database
            .SqlQueryRaw<string>("SELECT source AS Value FROM campaign_criteria")
            .SingleAsync();

        Assert.Equal("SystemDefault", stored);
    }
}
