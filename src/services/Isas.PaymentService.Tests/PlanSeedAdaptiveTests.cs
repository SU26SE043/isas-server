using Isas.PaymentService.Services;
using Microsoft.EntityFrameworkCore;
using PaymentService.Models;

namespace Isas.PaymentService.Tests;

/// <summary>
/// ADAPTIVE Ở MỌI TIER — khoá ở tầng DỮ LIỆU (catalog), tầng gần nguồn nhất.
///
/// Luật: một buổi phỏng vấn tiêu đúng 1 credit (B2C ví cá nhân · B2B ví org) BẤT KỂ gói, nên gói không
/// được lấy mất chính engine mà người dùng vừa trả tiền để chạy. Gói vẫn phân biệt bằng những thứ có
/// chi phí biên khác nhau THẬT: nguồn tiền, grounding, self-consistency, CV/repo, roadmap, trần B2B.
///
/// Đọc qua <c>PaymentTestDb</c> (tức qua <c>HasData</c>) chứ không đọc <c>PlanSeed.All</c> trực tiếp:
/// thứ chạy thật là hàng trong bảng <c>plans</c>, và đó cũng là thứ migration phải mang theo.
/// </summary>
public class PlanSeedAdaptiveTests
{
    [Fact]
    public async Task MoiGoiSeed_DeuBatAdaptive_CaB2CLanB2B()
    {
        using var t = new PaymentTestDb();

        var plans = await t.Db.Plans.AsNoTracking().OrderBy(p => p.Audience).ThenBy(p => p.Rank).ToListAsync();

        Assert.NotEmpty(plans);
        Assert.All(plans, p => Assert.True(p.AdaptiveEnabled,
            $"Gói '{p.Code}' ({p.Audience}) tắt adaptive — mọi tier đều tiêu 1 credit/buổi nên không tier "
            + "nào được mất engine phỏng vấn. Thêm gói mới thì bật adaptive, đừng nới test này."));
    }

    /// <summary>
    /// Gói mặc định (free/starter — cũng là gói người chưa mua gì rơi vào) phải cấp đủ trần để buổi
    /// adaptive chạy thật: trần <c>0</c> ở gói B2C sẽ đẩy <c>PracticeService</c> về trần cấu hình, còn
    /// một con số dương thì nó là trần THẬT của người dùng ⇒ đặt bằng trần hệ thống (20).
    /// </summary>
    [Fact]
    public async Task GoiMacDinh_CoTranCauDuDeChayAdaptive()
    {
        using var t = new PaymentTestDb();
        var db = t.NewContext();

        var free = await db.Plans.AsNoTracking().SingleAsync(p => p.Audience == PlanAudience.B2C && p.Code == "free");
        Assert.True(free.AdaptiveEnabled);
        Assert.Equal(20, free.AdaptiveMaxQuestions);
        Assert.Equal(3, free.AdaptiveMaxFollowups);

        var starter = await db.Plans.AsNoTracking().SingleAsync(p => p.Audience == PlanAudience.B2B && p.Code == "starter");
        Assert.True(starter.AdaptiveEnabled);
    }

    /// <summary>
    /// Snapshot gửi xuống Interview/Campaign phải mang theo đúng cờ đó — đây là mắt xích duy nhất giữa
    /// bảng <c>plans</c> và hai consumer, và lệch tên field ở đây thì consumer chỉ nhận <c>false</c>
    /// mặc định chứ KHÔNG lỗi (đúng lớp bug đã làm mọi user Plus/Pro nhận trần 0 câu).
    /// </summary>
    [Fact]
    public async Task SnapshotGoiFree_MangTheoAdaptive()
    {
        using var t = new PaymentTestDb();
        var free = await t.Db.Plans.AsNoTracking().SingleAsync(p => p.Audience == PlanAudience.B2C && p.Code == "free");

        var snapshot = EntitlementSnapshot.Create(free);

        Assert.True(snapshot.AdaptiveEnabled);
        Assert.Contains("\"adaptiveEnabled\":true", snapshot.Json);
        Assert.Contains("\"adaptiveMaxQuestions\":20", snapshot.Json);
    }
}
