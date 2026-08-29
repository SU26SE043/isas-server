using Isas.CampaignService.Controllers;
using Isas.CampaignService.DTOs;
using Isas.CampaignService.Models;
using Isas.CampaignService.Services;
using Isas.Shared.Scoring;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Isas.CampaignService.Tests;

/// <summary>
/// SCP1-B2 — bảng <c>scoring_policies</c> + 5 mẫu hệ thống (HĐ-3) + endpoint GET danh sách mẫu.
/// Bất biến trường ngữ nghĩa sau INSERT · hai partial unique RIÊNG · con trỏ trên campaigns.
/// </summary>
public class ScoringPolicySeedTests
{
    [Fact]
    public void Seed_dung_5_mau_he_thong_3_phong_van_2_sang_cv()
    {
        using var tdb = new CampaignTestDb();

        var templates = tdb.Db.ScoringPolicies.AsNoTracking()
            .Where(p => p.CampaignId == null).ToList();

        Assert.Equal(5, templates.Count);
        Assert.Equal(3, templates.Count(p => p.Kind == ScoringExpressionKind.Interview));
        Assert.Equal(2, templates.Count(p => p.Kind == ScoringExpressionKind.CvScreening));
        Assert.All(templates, p =>
        {
            Assert.Null(p.CampaignId);          // MẪU
            Assert.Null(p.CreatedBy);           // không có người tạo
            Assert.Null(p.SourceTemplateId);    // chính nó là mẫu
            Assert.Equal(1, p.Version);
            Assert.Equal(ScoringEngine.Version, p.EngineVersion);
        });
    }

    [Theory]
    [InlineData("Interview", "Như hiện nay", "weighted_avg_pct", 60)]
    [InlineData("Interview", "Phạt bỏ câu", "weighted_avg_pct * completeness", 60)]
    [InlineData("Interview", "Không bù trừ", "if(min_pct < 40, min_pct, weighted_avg_pct)", 60)]
    [InlineData("CvScreening", "Như hiện nay", "100 * (strong_count + 0.5 * partial_count) / need_count", 50)]
    [InlineData("CvScreening", "Bắt buộc must-have",
        "if(must_have_met < must_have_total, 0, 100 * (strong_count + 0.5 * partial_count) / need_count)", 50)]
    public void Seed_bieu_thuc_va_nguong_dung_verbatim(string kind, string name, string expr, int pass)
    {
        using var tdb = new CampaignTestDb();
        var k = Enum.Parse<ScoringExpressionKind>(kind);

        var p = tdb.Db.ScoringPolicies.AsNoTracking()
            .Single(x => x.CampaignId == null && x.Kind == k && x.Name == name);

        Assert.Equal(expr, p.Expression);
        Assert.Equal(pass, p.PassScorePct);
    }

    [Fact]
    public void Seed_moi_bieu_thuc_deu_hop_le_theo_B1()
    {
        using var tdb = new CampaignTestDb();

        foreach (var p in tdb.Db.ScoringPolicies.AsNoTracking().Where(x => x.CampaignId == null).ToList())
        {
            var r = ScoringExpression.Validate(p.Kind, p.Expression);
            Assert.True(r.Valid,
                $"seed '{p.Name}' ({p.Kind}) không hợp lệ: {(r.Errors.Count > 0 ? r.Errors[0].Code : "?")}");
            Assert.NotNull(r.SampleScore);
        }
    }
}

public class ScoringPolicyImmutabilityTests
{
    private static ScoringPolicy NewCampaignPolicy(Guid campaignId, int version = 1) => new()
    {
        Id = Guid.NewGuid(),
        CampaignId = campaignId,
        Kind = ScoringExpressionKind.Interview,
        Version = version,
        EngineVersion = ScoringEngine.Version,
        Name = "Bản HR",
        Description = "mô tả cũ",
        Expression = "weighted_avg_pct",
        PassScorePct = 60,
        CreatedAt = DateTime.UtcNow,
        CreatedBy = Guid.NewGuid(),
    };

    [Fact]
    public async Task Sua_Expression_sau_INSERT_bi_chan()
    {
        using var tdb = new CampaignTestDb();
        var p = NewCampaignPolicy(Guid.NewGuid());
        {
            using var w = tdb.NewContext();
            w.ScoringPolicies.Add(p);
            await w.SaveChangesAsync();
        }

        using var db = tdb.NewContext();
        var loaded = await db.ScoringPolicies.SingleAsync(x => x.Id == p.Id);
        loaded.Expression = "avg_pct";

        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }

    [Theory]
    [InlineData("pass_score_pct")]
    [InlineData("engine_version")]
    [InlineData("version")]
    [InlineData("kind")]
    [InlineData("campaign_id")]
    [InlineData("created_at")]
    [InlineData("created_by")]
    [InlineData("source_template_id")]
    public async Task Sua_truong_ngu_nghia_hoac_dinh_danh_sau_INSERT_bi_chan(string column)
    {
        using var tdb = new CampaignTestDb();
        var p = NewCampaignPolicy(Guid.NewGuid());
        {
            using var w = tdb.NewContext();
            w.ScoringPolicies.Add(p);
            await w.SaveChangesAsync();
        }

        using var db = tdb.NewContext();
        var loaded = await db.ScoringPolicies.SingleAsync(x => x.Id == p.Id);
        switch (column)
        {
            case "pass_score_pct": loaded.PassScorePct = 90; break;
            case "engine_version": loaded.EngineVersion = "2"; break;
            case "version": loaded.Version = 2; break;
            case "kind": loaded.Kind = ScoringExpressionKind.CvScreening; break;
            case "campaign_id": loaded.CampaignId = Guid.NewGuid(); break;
            case "created_at": loaded.CreatedAt = DateTime.UtcNow.AddDays(1); break;
            case "created_by": loaded.CreatedBy = Guid.NewGuid(); break;
            case "source_template_id": loaded.SourceTemplateId = Guid.NewGuid(); break;
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Sua_Name_va_Description_sau_INSERT_duoc_phep()
    {
        using var tdb = new CampaignTestDb();
        var p = NewCampaignPolicy(Guid.NewGuid());
        {
            using var w = tdb.NewContext();
            w.ScoringPolicies.Add(p);
            await w.SaveChangesAsync();
        }

        using var db = tdb.NewContext();
        var loaded = await db.ScoringPolicies.SingleAsync(x => x.Id == p.Id);
        loaded.Name = "Tên mới";
        loaded.Description = "mô tả mới";
        await db.SaveChangesAsync();

        using var check = tdb.NewContext();
        var after = await check.ScoringPolicies.SingleAsync(x => x.Id == p.Id);
        Assert.Equal("Tên mới", after.Name);
        Assert.Equal("mô tả mới", after.Description);
    }
}

public class ScoringPolicyConstraintTests
{
    private static ScoringPolicy Row(Guid? campaignId, ScoringExpressionKind kind, int version, string name,
        int? passScorePct = 60) => new()
    {
        Id = Guid.NewGuid(),
        CampaignId = campaignId,
        Kind = kind,
        Version = version,
        EngineVersion = ScoringEngine.Version,
        Name = name,
        Expression = "weighted_avg_pct",
        PassScorePct = passScorePct,
        CreatedAt = DateTime.UtcNow,
    };

    [Fact]
    public async Task Hai_mau_trung_kind_va_name_bi_chan_du_campaign_id_deu_NULL()
    {
        using var tdb = new CampaignTestDb();
        // Seed đã có (NULL, Interview, "Như hiện nay"). Thêm cái nữa cùng (kind, name) → vi phạm
        // ux_scoring_policies_template. Một UNIQUE chung (campaign_id, kind, version) sẽ KHÔNG bắt
        // được (Postgres/SQLite coi NULL distinct) — đây là lý do phải có partial unique riêng.
        using var db = tdb.NewContext();
        db.ScoringPolicies.Add(Row(null, ScoringExpressionKind.Interview, 2, "Như hiện nay"));
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Hai_ban_campaign_trung_campaign_kind_version_bi_chan()
    {
        using var tdb = new CampaignTestDb();
        var campaignId = Guid.NewGuid();
        using var db = tdb.NewContext();
        db.ScoringPolicies.Add(Row(campaignId, ScoringExpressionKind.Interview, 1, "A"));
        db.ScoringPolicies.Add(Row(campaignId, ScoringExpressionKind.Interview, 1, "B"));
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Ban_campaign_version_1_KHONG_dung_do_voi_mau_version_1()
    {
        using var tdb = new CampaignTestDb();
        var campaignId = Guid.NewGuid();
        using var db = tdb.NewContext();
        // (campaignId, Interview, 1) sống chung với mẫu (NULL, Interview, 1) — hai partial index tách nhau.
        db.ScoringPolicies.Add(Row(campaignId, ScoringExpressionKind.Interview, 1, "Bản riêng"));
        await db.SaveChangesAsync();

        using var check = tdb.NewContext();
        Assert.Equal(1, await check.ScoringPolicies.CountAsync(p => p.CampaignId == campaignId));
        Assert.Equal(5, await check.ScoringPolicies.CountAsync(p => p.CampaignId == null));
    }

    [Theory]
    [InlineData(150)]
    [InlineData(-1)]
    public async Task pass_score_pct_ngoai_0_100_bi_CHECK_chan(int bad)
    {
        using var tdb = new CampaignTestDb();
        using var db = tdb.NewContext();
        db.ScoringPolicies.Add(Row(Guid.NewGuid(), ScoringExpressionKind.Interview, 1, "X", passScorePct: bad));
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task pass_score_pct_NULL_duoc_phep()
    {
        using var tdb = new CampaignTestDb();
        using var db = tdb.NewContext();
        db.ScoringPolicies.Add(Row(Guid.NewGuid(), ScoringExpressionKind.Interview, 1, "X", passScorePct: null));
        await db.SaveChangesAsync();   // không ném
    }

    [Fact]
    public async Task version_duoi_1_bi_CHECK_chan()
    {
        using var tdb = new CampaignTestDb();
        using var db = tdb.NewContext();
        db.ScoringPolicies.Add(Row(Guid.NewGuid(), ScoringExpressionKind.Interview, 0, "X"));
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Con_tro_chinh_sach_tren_campaigns_mac_dinh_null_va_set_duoc()
    {
        using var tdb = new CampaignTestDb();
        var c = CampaignTestDb.NewCampaign(Guid.NewGuid());
        {
            using var w = tdb.NewContext();
            w.Campaigns.Add(c);
            await w.SaveChangesAsync();
        }

        using var db = tdb.NewContext();
        var loaded = await db.Campaigns.SingleAsync(x => x.Id == c.Id);
        Assert.Null(loaded.InterviewPolicyVersion);
        Assert.Null(loaded.CvPolicyVersion);

        loaded.InterviewPolicyVersion = 3;
        loaded.CvPolicyVersion = 1;
        await db.SaveChangesAsync();

        using var check = tdb.NewContext();
        var after = await check.Campaigns.SingleAsync(x => x.Id == c.Id);
        Assert.Equal(3, after.InterviewPolicyVersion);
        Assert.Equal(1, after.CvPolicyVersion);
    }
}

public class ScoringPolicyTemplatesEndpointTests
{
    [Fact]
    public async Task GET_scoring_policy_templates_tra_5_dung_shape_HĐ3()
    {
        using var tdb = new CampaignTestDb();
        var controller = new CampaignController(
            Mock.Of<ICampaignService>(),
            Mock.Of<ICvScreeningService>(),
            Mock.Of<ILogger<CampaignController>>(),
            policies: new ScoringPolicyService(tdb.NewContext()));

        var action = await controller.GetScoringPolicyTemplates(default);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var list = Assert.IsAssignableFrom<IReadOnlyList<ScoringPolicyResponse>>(ok.Value);
        Assert.Equal(5, list.Count);

        // HĐ-3 shape + sắp Kind (enum) rồi Name (ordinal): Interview trước, "Không.." < "Như.." < "Phạt..".
        Assert.Equal(
            new[]
            {
                ("Interview", "Không bù trừ"),
                ("Interview", "Như hiện nay"),
                ("Interview", "Phạt bỏ câu"),
                ("CvScreening", "Bắt buộc must-have"),
                ("CvScreening", "Như hiện nay"),
            },
            list.Select(p => (p.Kind, p.Name)).ToArray());

        var first = list[0];
        Assert.NotEqual(Guid.Empty, first.Id);
        Assert.Equal("1", first.EngineVersion);
        Assert.Equal(1, first.Version);
        Assert.Null(first.SourceTemplateId);
        Assert.Null(first.CreatedBy);
        Assert.NotNull(first.Expression);
    }

    [Fact]
    public async Task Service_GetTemplates_bo_qua_ban_cua_campaign()
    {
        using var tdb = new CampaignTestDb();
        {
            using var w = tdb.NewContext();
            w.ScoringPolicies.Add(new ScoringPolicy
            {
                Id = Guid.NewGuid(),
                CampaignId = Guid.NewGuid(),   // KHÔNG phải mẫu
                Kind = ScoringExpressionKind.Interview,
                Version = 1,
                EngineVersion = ScoringEngine.Version,
                Name = "Bản campaign",
                Expression = "avg_pct",
                PassScorePct = 70,
                CreatedAt = DateTime.UtcNow,
                CreatedBy = Guid.NewGuid(),
            });
            await w.SaveChangesAsync();
        }

        var list = await new ScoringPolicyService(tdb.NewContext()).GetTemplatesAsync();
        Assert.Equal(5, list.Count);
        Assert.DoesNotContain(list, p => p.Name == "Bản campaign");
    }
}
