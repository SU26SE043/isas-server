using System.Text.Json;
using Isas.CampaignService.DTOs;
using Isas.CampaignService.Models;
using Isas.CampaignService.Services;
using Microsoft.Extensions.Logging;
using Moq;

namespace Isas.CampaignService.Tests;

/// <summary>
/// Quyết định 7 — MỐC ĐIỂM CHỈ EMPLOYER THẤY. Lộ cho ứng viên thì họ viết bài bám đúng câu chữ của
/// mốc và thang đo mất hết giá trị phân biệt.
///
/// <para>Trước bản này hai đường ứng viên (<c>GET /invitations/{token}</c>,
/// <c>GET /my-campaigns/{id}</c>) an toàn chỉ vì query quên <c>ThenInclude(Levels)</c> — an toàn DO
/// TÌNH CỜ. Nay chặn bằng cấu trúc: DTO của ứng viên không khai trường mốc.</para>
/// </summary>
public class CandidateCriterionLeakTests
{
    // ⚠ Sentinel phải là ASCII: System.Text.Json escape non-ASCII (\u...), nên assert một chuỗi tiếng
    // Việt vào JSON đã serialize sẽ XANH một cách tầm thường KỂ CẢ KHI dữ liệu đã rò.
    private const string Sentinel = "LEVEL-DESCRIPTOR-SENTINEL-0001";

    private static ParticipationService NewService(CampaignDbContext db) =>
        new(db, Mock.Of<IAuthProvisionClient>(), Mock.Of<ICampaignSessionClient>(),
            Mock.Of<ILogger<ParticipationService>>());

    // Ràng buộc của TRÌNH BIÊN DỊCH, không phải của người đọc code: thêm lại trường mốc vào DTO ứng
    // viên (dù chỉ để "cho đồng bộ" với bản Employer) làm test này đỏ ngay.
    [Fact]
    public void DTO_tieu_chi_cua_ung_vien_KHONG_khai_truong_moc_diem()
    {
        var props = typeof(CandidateCriterionResponse).GetProperties().Select(p => p.Name).ToList();

        Assert.DoesNotContain("Levels", props);
        Assert.DoesNotContain("Descriptor", props);
        // Bản Employer thì PHẢI có — nếu không, đối chứng này vô nghĩa và mốc chẳng tới được ai.
        Assert.Contains("Levels", typeof(CampaignCriterionResponse).GetProperties().Select(p => p.Name));
    }

    [Fact]
    public async Task Metadata_loi_moi_KHONG_chua_mo_ta_moc_diem()
    {
        using var tdb = new CampaignTestDb();
        var (camp, token) = await SeedInvitedCampaignAsync(tdb);

        var res = await NewService(tdb.NewContext()).GetInvitationMetadataAsync(token, default);

        Assert.NotEmpty(res.Criteria);   // đối chứng: có tiêu chí thật, không phải rỗng nên "không rò"
        Assert.DoesNotContain(Sentinel, JsonSerializer.Serialize(res));
    }

    [Fact]
    public async Task Chi_tiet_campaign_cua_ung_vien_KHONG_chua_mo_ta_moc_diem()
    {
        using var tdb = new CampaignTestDb();
        var (camp, _) = await SeedInvitedCampaignAsync(tdb);
        var candidateId = Guid.NewGuid();
        tdb.Db.CampaignMemberships.Add(CampaignTestDb.NewMembership(camp.Id, candidateId));
        await tdb.Db.SaveChangesAsync();

        var res = await NewService(tdb.NewContext())
            .GetCandidateCampaignAsync(candidateId, camp.Id, default);

        Assert.NotEmpty(res.Criteria);
        Assert.DoesNotContain(Sentinel, JsonSerializer.Serialize(res));
    }

    /// <summary>Campaign Active + 1 tiêu chí CÓ mốc điểm + 1 lời mời còn hạn.</summary>
    private static async Task<(Campaign Camp, string RawToken)> SeedInvitedCampaignAsync(CampaignTestDb tdb)
    {
        var camp = CampaignTestDb.NewCampaign(Guid.NewGuid(), CampaignStatus.Active);
        tdb.Db.Campaigns.Add(camp);

        var cr = new CampaignCriterion
        {
            Id = Guid.NewGuid(), CampaignId = camp.Id, OrderNo = 0, Name = "Chuyen mon",
            Weight = 1.0m, MaxScore = 5, Source = CriterionSource.HrEdited,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        };
        tdb.Db.CampaignCriteria.Add(cr);
        tdb.Db.CampaignCriterionLevels.AddRange(
            new CampaignCriterionLevel
            {
                Id = Guid.NewGuid(), CriterionId = cr.Id, Score = 0, Descriptor = Sentinel + " muc 0",
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
            },
            new CampaignCriterionLevel
            {
                Id = Guid.NewGuid(), CriterionId = cr.Id, Score = 5, Descriptor = Sentinel + " muc 5",
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
            });

        var rawToken = "raw-token-" + Guid.NewGuid().ToString("N");
        tdb.Db.CampaignInvitations.Add(new CampaignInvitation
        {
            Id = Guid.NewGuid(),
            CampaignId = camp.Id,
            Email = "ungvien@example.com",
            TokenHash = InvitationTokens.Hash(rawToken),
            ExpiresAt = DateTime.UtcNow.AddDays(7),
            CreatedAt = DateTime.UtcNow
        });
        await tdb.Db.SaveChangesAsync();
        return (camp, rawToken);
    }
}
