using System.Reflection;
using System.Text.Json;
using Isas.CampaignService.DTOs;
using Isas.CampaignService.Models;
using Isas.CampaignService.Services;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Moq;

using CampaignSvc = Isas.CampaignService.Services.CampaignService;

namespace Isas.CampaignService.Tests;

/// <summary>
/// CMP1-B3 — 3 số đếm cho <c>GET /campaign</c> (danh sách) + tách hình dạng khỏi
/// <c>GET /campaign/{id}</c> (chi tiết).
///
/// <para>Đã đo trên dev: danh sách trả 37 trường, KHÔNG trường nào là số đếm ⇒ FE đọc
/// "applicants"/"capacity" ra null/0 dù DB có CV + lời mời thật. Đồng thời
/// <c>jdText + questions + criteria</c> chiếm 69% payload của một trang campaign, mà bảng danh
/// sách không hiển thị chúng.</para>
/// </summary>
public class ThreeCountsForListCmp1B3Tests
{
    private static CampaignSvc NewService(CampaignDbContext db) =>
        new(db, Mock.Of<IFileService>(), Mock.Of<ILogger<CampaignSvc>>(),
            Mock.Of<IParserService>(), Mock.Of<ICriteriaSuggester>(),
            Mock.Of<IInvitationEmailPublisher>());

    private static CampaignInvitation NewInvitation(
        Guid campaignId, string email, DateTime? revokedAt = null)
    {
        var raw = Guid.NewGuid().ToString("N");
        return new()
        {
            Id = Guid.NewGuid(),
            CampaignId = campaignId,
            TokenHash = InvitationTokens.Hash(raw),
            Email = email,
            ExpiresAt = DateTime.UtcNow.AddDays(7),
            RevokedAt = revokedAt,
            CreatedAt = DateTime.UtcNow,
        };
    }

    private static CvSubmission NewCv(Guid campaignId, string email) => new()
    {
        Id = Guid.NewGuid(),
        CampaignId = campaignId,
        Email = email,
        Status = CvSubmissionStatus.Pending,
        ParseStatus = CvParseStatus.Pending,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    private static CampaignRanking NewRanking(Guid campaignId, Guid candidateId, Guid sessionId) => new()
    {
        Id = Guid.NewGuid(),
        CampaignId = campaignId,
        CandidateId = candidateId,
        SessionId = sessionId,
        TotalScore = 80m,
        UpdatedAt = DateTime.UtcNow,
    };

    // 1. Ca chính: 3 CV + 2 lời mời (còn hiệu lực) + 0 bài thi ⇒ đúng 3/2/0.
    [Fact]
    public async Task List_dung_3_so_dem_theo_du_lieu_seed()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = CampaignTestDb.NewCampaign(owner, CampaignStatus.Active);
        tdb.Db.Campaigns.Add(camp);
        tdb.Db.CvSubmissions.AddRange(
            NewCv(camp.Id, "a@x.com"), NewCv(camp.Id, "b@x.com"), NewCv(camp.Id, "c@x.com"));
        tdb.Db.CampaignInvitations.AddRange(
            NewInvitation(camp.Id, "a@x.com"), NewInvitation(camp.Id, "b@x.com"));
        await tdb.Db.SaveChangesAsync();

        var page = await NewService(tdb.NewContext()).GetCampaignsAsync(owner, null, null, default);
        var item = Assert.Single(page.Items);

        Assert.Equal(3, item.CvCount);
        Assert.Equal(2, item.InvitedCount);
        Assert.Equal(0, item.CompletedCount);
    }

    // 2. Lời mời ĐÃ REVOKE không tính vào invitedCount.
    [Fact]
    public async Task List_loimoi_da_revoke_khong_tinh()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = CampaignTestDb.NewCampaign(owner, CampaignStatus.Active);
        tdb.Db.Campaigns.Add(camp);
        tdb.Db.CampaignInvitations.AddRange(
            NewInvitation(camp.Id, "con-hieu-luc@x.com"),
            NewInvitation(camp.Id, "da-revoke-1@x.com", revokedAt: DateTime.UtcNow.AddMinutes(-1)),
            NewInvitation(camp.Id, "da-revoke-2@x.com", revokedAt: DateTime.UtcNow.AddDays(-1)));
        await tdb.Db.SaveChangesAsync();

        var page = await NewService(tdb.NewContext()).GetCampaignsAsync(owner, null, null, default);

        Assert.Equal(1, Assert.Single(page.Items).InvitedCount);   // 3 dòng DB, chỉ 1 còn hiệu lực
    }

    // 3. CV thuộc campaign ĐÃ soft-delete không rò vào cvCount của campaign khác (query filter DB13
    // qua Campaign.DeletedAt — cùng org, cùng owner, khác campaign_id).
    [Fact]
    public async Task List_cv_cua_campaign_da_xoa_mem_khong_tinh_vao_campaign_khac()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();

        var visible = CampaignTestDb.NewCampaign(owner, CampaignStatus.Active);
        var deleted = CampaignTestDb.NewCampaign(owner, CampaignStatus.Active);
        deleted.DeletedAt = DateTime.UtcNow;
        tdb.Db.Campaigns.AddRange(visible, deleted);
        tdb.Db.CvSubmissions.AddRange(
            NewCv(visible.Id, "v1@x.com"), NewCv(visible.Id, "v2@x.com"), NewCv(visible.Id, "v3@x.com"),
            NewCv(deleted.Id, "d1@x.com"), NewCv(deleted.Id, "d2@x.com"),
            NewCv(deleted.Id, "d3@x.com"), NewCv(deleted.Id, "d4@x.com"), NewCv(deleted.Id, "d5@x.com"));
        await tdb.Db.SaveChangesAsync();

        var page = await NewService(tdb.NewContext()).GetCampaignsAsync(owner, null, null, default);

        var item = Assert.Single(page.Items);   // campaign đã xoá không hiện trong danh sách
        Assert.Equal(visible.Id, item.Id);
        Assert.Equal(3, item.CvCount);          // KHÔNG bị 5 CV của campaign đã xoá cộng dồn vào
    }

    // 4. completedCount = số dòng campaign_rankings (mỗi dòng = 1 buổi đã chấm).
    [Fact]
    public async Task List_completedCount_dem_so_dong_campaign_rankings()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = CampaignTestDb.NewCampaign(owner, CampaignStatus.Active);
        tdb.Db.Campaigns.Add(camp);
        tdb.Db.CampaignRankings.AddRange(
            NewRanking(camp.Id, Guid.NewGuid(), Guid.NewGuid()),
            NewRanking(camp.Id, Guid.NewGuid(), Guid.NewGuid()));
        await tdb.Db.SaveChangesAsync();

        var page = await NewService(tdb.NewContext()).GetCampaignsAsync(owner, null, null, default);

        Assert.Equal(2, Assert.Single(page.Items).CompletedCount);
    }

    // 5. Danh sách KHÔNG có khoá jdText/questions/criteria trong JSON (khác giá trị null — khoá phải
    // VẮNG MẶT, vì FE dựa vào sự vắng mặt này để biết payload đã được cắt).
    [Fact]
    public async Task List_JSON_KHONG_co_khoa_jdText_questions_criteria()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = CampaignTestDb.NewCampaign(owner, CampaignStatus.Active);
        camp.JDText = "JD dài dòng";
        tdb.Db.Campaigns.Add(camp);
        tdb.Db.CampaignQuestions.Add(new CampaignQuestion
        {
            Id = Guid.NewGuid(), CampaignId = camp.Id, QuestionText = "Câu hỏi 1",
            Source = QuestionSource.CustomHr, IsRequired = true, CreatedAt = DateTime.UtcNow,
        });
        tdb.Db.CampaignCriteria.Add(new CampaignCriterion
        {
            Id = Guid.NewGuid(), CampaignId = camp.Id, OrderNo = 0, Name = "Kỹ thuật",
            Weight = 1m, MaxScore = 10, Source = CriterionSource.HrEdited,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        await tdb.Db.SaveChangesAsync();

        var page = await NewService(tdb.NewContext()).GetCampaignsAsync(owner, null, null, default);
        var json = JsonSerializer.Serialize(
            Assert.Single(page.Items), new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.DoesNotContain("\"jdText\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"questions\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"criteria\"", json, StringComparison.OrdinalIgnoreCase);
        // Đối chứng dương: 3 số đếm mới + questionBank PHẢI có mặt — nếu không thì phép so trên
        // đo một payload rỗng vô nghĩa chứ không phải payload đã cắt đúng chỗ.
        Assert.Contains("\"cvCount\"", json);
        Assert.Contains("\"invitedCount\"", json);
        Assert.Contains("\"completedCount\"", json);
        Assert.Contains("\"questionBank\"", json);
    }

    // 6. GET /campaign/{id} (chi tiết) VẪN có đủ jdText/questions/criteria — CẤM đụng detail.
    [Fact]
    public async Task Detail_VAN_co_du_jdText_questions_criteria()
    {
        using var tdb = new CampaignTestDb();
        var owner = Guid.NewGuid();
        var camp = CampaignTestDb.NewCampaign(owner, CampaignStatus.Active);
        camp.JDText = "JD dài dòng";
        tdb.Db.Campaigns.Add(camp);
        tdb.Db.CampaignQuestions.Add(new CampaignQuestion
        {
            Id = Guid.NewGuid(), CampaignId = camp.Id, QuestionText = "Câu hỏi 1",
            Source = QuestionSource.CustomHr, IsRequired = true, CreatedAt = DateTime.UtcNow,
        });
        tdb.Db.CampaignCriteria.Add(new CampaignCriterion
        {
            Id = Guid.NewGuid(), CampaignId = camp.Id, OrderNo = 0, Name = "Kỹ thuật",
            Weight = 1m, MaxScore = 10, Source = CriterionSource.HrEdited,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        await tdb.Db.SaveChangesAsync();

        var detail = await NewService(tdb.NewContext()).GetCampaignAsync(owner, camp.Id, default);

        Assert.Equal("JD dài dòng", detail.JDText);
        Assert.Single(detail.Questions);
        Assert.Single(detail.Criteria);

        var json = JsonSerializer.Serialize(detail, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Contains("\"jdText\"", json);
        Assert.Contains("\"questions\"", json);
        Assert.Contains("\"criteria\"", json);
    }

    // 7. KHÔNG N+1 — 1 trang = số truy vấn CỐ ĐỊNH, không tăng theo số campaign trên trang.
    [Fact]
    public void List_KhongN1_SoTruyVanCoDinh_KhongTangTheoSoCampaign()
    {
        int QueriesFor(int campaignCount)
        {
            using var tdb = new CampaignTestDb();
            var owner = Guid.NewGuid();
            for (var i = 0; i < campaignCount; i++)
            {
                var camp = CampaignTestDb.NewCampaign(owner, CampaignStatus.Active);
                tdb.Db.Campaigns.Add(camp);
                tdb.Db.SaveChanges();
                tdb.Db.CvSubmissions.AddRange(NewCv(camp.Id, $"c{i}-1@x.com"), NewCv(camp.Id, $"c{i}-2@x.com"));
                tdb.Db.CampaignInvitations.Add(NewInvitation(camp.Id, $"i{i}@x.com"));
                tdb.Db.CampaignRankings.Add(NewRanking(camp.Id, Guid.NewGuid(), Guid.NewGuid()));
                tdb.Db.SaveChanges();
            }

            var spy = new SqlSpy();
            NewService(tdb.NewContext(spy)).GetCampaignsAsync(owner, null, null, default)
                .GetAwaiter().GetResult();
            return spy.SelectCount;
        }

        Assert.Equal(QueriesFor(1), QueriesFor(5));   // 1 campaign hay 5 campaign đều cùng số SELECT
    }

    // 8. HỢP ĐỒNG SHAPE — chống rò bằng cấu trúc (mẫu CandidateCriterionResponse/F17): mọi trường của
    // CampaignResponse, TRỪ JDText/Questions/Criteria, phải có mặt trên CampaignListItemResponse cùng
    // tên + kiểu; CampaignListItemResponse không được có field lạ ngoài 3 số đếm mới. Một field thêm
    // vào CampaignResponse mà quên thêm ở đây (hoặc ngược lại) sẽ làm test này ĐỎ, không phải trôi
    // trong im lặng.
    [Fact]
    public void ListShape_KhopTungTruongVoiCampaignResponse_TruParent3TruongVaCong3SoDem()
    {
        var dropped = new HashSet<string> { "JDText", "Questions", "Criteria" };
        var added = new HashSet<string> { "CvCount", "InvitedCount", "CompletedCount" };

        var flags = BindingFlags.Public | BindingFlags.Instance;
        var full = typeof(CampaignResponse).GetProperties(flags)
            .ToDictionary(p => p.Name, p => p.PropertyType);
        var list = typeof(CampaignListItemResponse).GetProperties(flags)
            .ToDictionary(p => p.Name, p => p.PropertyType);

        var expectedList = full.Where(kv => !dropped.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);

        foreach (var (name, type) in expectedList)
            Assert.True(list.TryGetValue(name, out var listType) && listType == type,
                $"CampaignListItemResponse thiếu hoặc lệch kiểu trường '{name}' (kỳ vọng {type}).");

        var listOnly = list.Keys.Except(expectedList.Keys).ToHashSet();
        Assert.True(added.SetEquals(listOnly),
            $"CampaignListItemResponse có trường lạ ngoài 3 số đếm: {string.Join(",", listOnly.Except(added))}");

        foreach (var name in dropped)
            Assert.False(list.ContainsKey(name), $"'{name}' PHẢI bị bỏ khỏi danh sách (CMP1-B3).");
    }

    private sealed class SqlSpy : DbCommandInterceptor
    {
        private int _selects;
        public int SelectCount => _selects;

        public override InterceptionResult<System.Data.Common.DbDataReader> ReaderExecuting(
            System.Data.Common.DbCommand command, CommandEventData eventData,
            InterceptionResult<System.Data.Common.DbDataReader> result)
        {
            System.Threading.Interlocked.Increment(ref _selects);
            return result;
        }

        public override ValueTask<InterceptionResult<System.Data.Common.DbDataReader>> ReaderExecutingAsync(
            System.Data.Common.DbCommand command, CommandEventData eventData,
            InterceptionResult<System.Data.Common.DbDataReader> result, CancellationToken cancellationToken = default)
        {
            System.Threading.Interlocked.Increment(ref _selects);
            return ValueTask.FromResult(result);
        }
    }
}
