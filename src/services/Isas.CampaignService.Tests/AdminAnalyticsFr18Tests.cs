using Isas.CampaignService.Controllers;
using Isas.CampaignService.Models;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace Isas.CampaignService.Tests;

/// <summary>
/// Q5 — <c>GET /campaign/admin/analytics</c> phải sắp bucket được khi kỳ trải nhiều ngày.
///
/// Trước bản vá: <c>AnalyticsBucketKey</c> là <c>record struct</c> không <c>IComparable</c> ⇒
/// <c>.OrderBy(x =&gt; x.Key)</c> (<c>AdminController.cs:64</c>) ném ArgumentException ngay lần so ĐẦU TIÊN
/// ⇒ 0–1 bucket im lặng, ≥2 bucket nổ. Đo trên deploy 2026-08-07: endpoint này 500 với mọi dải ngày thật,
/// 200 khi ép về đúng 1 ngày. Campaign trước đó KHÔNG có test analytics nào nên không có gì bắt.
///
/// ⚠ Đây là nhánh LAZY: <c>buckets</c> là IEnumerable chưa ToList(), chỉ được duyệt lúc SERIALIZE —
/// tức SAU khi <c>Ok(...)</c> đã return. <c>Assert.IsType&lt;OkObjectResult&gt;</c> KHÔNG bắt được lỗi này;
/// bắt buộc phải serialize thật.
/// </summary>
public class AdminAnalyticsFr18Tests
{
    private static AdminController NewController() => new(Mock.Of<Isas.CampaignService.Services.ICampaignService>());

    [Fact]
    public async Task Analytics_NhieuBucket_SapTheoThoiGian_KhongNemLucSerialize()
    {
        using var t = new CampaignTestDb();
        var org = Guid.NewGuid();
        var at = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);

        // Ba campaign ở ba NGÀY khác nhau → 3 bucket. Thêm không theo thứ tự để phép sắp có việc thật.
        foreach (var offset in new[] { 2, 0, 1 })
        {
            var c = CampaignTestDb.NewCampaign(org, CampaignStatus.Active);
            c.CreatedAt = at.AddDays(offset);
            t.Db.Campaigns.Add(c);
        }
        await t.Db.SaveChangesAsync();

        var result = await NewController().Analytics(t.Db, at, at.AddDays(3), "day");
        var json = System.Text.Json.JsonSerializer.Serialize(Assert.IsType<OkObjectResult>(result).Value);

        // Assert THỨ TỰ chứ không chỉ "không ném" — mutation đảo dấu CompareTo phải đỏ ở đây.
        var mocs = System.Text.RegularExpressions.Regex.Matches(json, "\"periodStart\":\"(?<v>[^\"]+)\"")
            .Select(m => m.Groups["v"].Value).ToList();
        Assert.Equal(3, mocs.Count);
        Assert.Equal(mocs.OrderBy(v => v, StringComparer.Ordinal).ToList(), mocs);
    }

    [Fact]
    public async Task Analytics_MotBucket_VanChay_GiuHanhViCu()
    {
        // Đối chứng: đây chính là hình dạng fixture khiến bug lọt suốt — giữ lại để nếu ai đó "vá" bằng
        // cách bỏ OrderBy thì test trên vẫn đỏ, còn test này chứng minh không đổi hành vi ca 1 bucket.
        using var t = new CampaignTestDb();
        var org = Guid.NewGuid();
        var at = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);

        var c = CampaignTestDb.NewCampaign(org, CampaignStatus.Active);
        c.CreatedAt = at;
        t.Db.Campaigns.Add(c);
        await t.Db.SaveChangesAsync();

        var result = await NewController().Analytics(t.Db, at, at.AddDays(1), "day");
        var json = System.Text.Json.JsonSerializer.Serialize(Assert.IsType<OkObjectResult>(result).Value);

        Assert.Single(System.Text.RegularExpressions.Regex.Matches(json, "\"periodStart\":"));
    }
}
