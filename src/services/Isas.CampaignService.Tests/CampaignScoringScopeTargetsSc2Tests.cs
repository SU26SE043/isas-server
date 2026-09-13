using System.Text.Json;
using System.Text.Json.Serialization;
using Isas.CampaignService.DTOs;
using Isas.CampaignService.Models;
using Isas.CampaignService.Services;
using Isas.Shared.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;

using CampaignSvc = Isas.CampaignService.Services.CampaignService;

namespace Isas.CampaignService.Tests;

/// <summary>
/// SC2 · W1 — Campaign biết "tiêu chí này chấm ở mọi câu hay chỉ khi câu nhắm tới"
/// (<c>campaign_criteria.scoring_scope</c>) và "câu này nhắm tiêu chí nào"
/// (<c>campaign_questions.target_criterion_ids</c>).
///
/// <para>Ba bất biến đắt nhất ở đây, mỗi cái có phép mutation khoá riêng:</para>
/// <list type="number">
/// <item><b>I2 — <c>null</c> ≠ <c>[]</c>.</b> null = chưa gắn (Interview chấm đủ bộ), [] = đã xét không
/// nhắm (chỉ Always). Gộp hai ca là vô hiệu tính năng đúng ở nhóm câu cần nó nhất.</item>
/// <item><b>Vắng = GIỮ.</b> FE cũ không biết field ⇒ PUT không được xoá hộ nhãn (mẫu sampleAnswer/levels).</item>
/// <item><b>Cắt nhãn chỉ cắt id ĐÃ CHẾT.</b> Xoá tiêu chí B không được làm câu mất nhãn A còn sống.</item>
/// </list>
/// </summary>
public class CampaignScoringScopeTargetsSc2Tests
{
    private static CampaignSvc NewService(CampaignDbContext db, ICampaignSessionClient? session = null) =>
        new(db, Mock.Of<IFileService>(), Mock.Of<ILogger<CampaignSvc>>(),
            Mock.Of<IParserService>(), Mock.Of<ICriteriaSuggester>(),
            Mock.Of<IInvitationEmailPublisher>(),
            sessionClient: session,
            config: new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Campaign:Bilingual:Enabled"] = "true"
            }).Build());

    private static CampaignCriterion Crit(Guid campaignId, int order, string name, decimal weight,
        CriterionScoringScope scope = CriterionScoringScope.Always)
        => new()
        {
            Id = Guid.NewGuid(), CampaignId = campaignId, OrderNo = order, Name = name,
            Weight = weight, MaxScore = 5, Source = CriterionSource.HrEdited, ScoringScope = scope,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        };

    private static CampaignQuestion Q(Guid campaignId, Guid org, string text, List<Guid>? targets)
        => new()
        {
            Id = Guid.NewGuid(), CampaignId = campaignId, OrgId = org, QuestionText = text,
            Source = QuestionSource.CustomHr, IsRequired = true, TargetCriterionIds = targets,
            CreatedAt = DateTime.UtcNow
        };

    private static CriterionItem Echo(
        CampaignCriterion c, string? scope = null, decimal? weight = null, string? description = null,
        string? name = null)
        => new()
        {
            Id = c.Id, Name = name ?? c.Name, Weight = weight ?? c.Weight, MaxScore = c.MaxScore,
            Description = description ?? c.Description,
            ScoringScope = scope, Levels = new List<CriterionLevelItem>()
        };

    private static async Task<Campaign> SeedAsync(
        CampaignTestDb tdb, Guid org, CampaignStatus status = CampaignStatus.Draft,
        CampaignCriterion[]? criteria = null, CampaignQuestion[]? questions = null)
    {
        var camp = CampaignTestDb.NewCampaign(org, status);
        tdb.Db.Campaigns.Add(camp);
        foreach (var c in criteria ?? []) { c.CampaignId = camp.Id; tdb.Db.CampaignCriteria.Add(c); }
        foreach (var q in questions ?? []) { q.CampaignId = camp.Id; tdb.Db.CampaignQuestions.Add(q); }
        await tdb.Db.SaveChangesAsync();
        return camp;
    }

    private static async Task<List<CampaignQuestion>> QuestionsAsync(CampaignTestDb tdb, Guid campaignId)
    {
        using var check = tdb.NewContext();
        return await check.CampaignQuestions.Where(q => q.CampaignId == campaignId)
            .OrderBy(q => q.CreatedAt).ThenBy(q => q.Id).ToListAsync();
    }

    // Mirror của Program.cs AddJsonOptions (camelCase + JsonStringEnumConverter + Never-ignore).
    private static JsonSerializerOptions RuntimeJson()
    {
        var o = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            ReferenceHandler = ReferenceHandler.IgnoreCycles,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        };
        o.Converters.Add(new JsonStringEnumConverter());
        o.Converters.Add(new UtcDateTimeConverter());
        return o;
    }

    // ═══════════════ A. scoringScope round-trip ═══════════════

    // PUT với scoringScope ⇒ lưu; vắng ⇒ Always (hành vi cũ, không phải "xoá").
    [Fact]
    public async Task Scope_PutCoGiaTri_LuuDung_VangThiAlways()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var camp = await SeedAsync(tdb, org);

        var res = await NewService(tdb.NewContext()).UpdateCampaignAsync(org, org, camp.Id, new UpdateCampaignRequest
        {
            Criteria = new List<CriterionItem>
            {
                new() { Name = "Chiều sâu kỹ thuật", Weight = 0.5m, MaxScore = 5, ScoringScope = "WhenTargeted" },
                new() { Name = "Giao tiếp", Weight = 0.5m, MaxScore = 5 },   // vắng
            }
        }, default);

        Assert.Equal("WhenTargeted", res.Criteria.Single(c => c.Name == "Chiều sâu kỹ thuật").ScoringScope);
        Assert.Equal("Always", res.Criteria.Single(c => c.Name == "Giao tiếp").ScoringScope);

        using var check = tdb.NewContext();
        var rows = await check.CampaignCriteria.Where(c => c.CampaignId == camp.Id).ToListAsync();
        Assert.Equal(CriterionScoringScope.WhenTargeted, rows.Single(c => c.Name == "Chiều sâu kỹ thuật").ScoringScope);
        Assert.Equal(CriterionScoringScope.Always, rows.Single(c => c.Name == "Giao tiếp").ScoringScope);
    }

    // Không phân biệt hoa/thường theo TÊN; số ("1") là lạ.
    [Theory]
    [InlineData("whentargeted", CriterionScoringScope.WhenTargeted)]
    [InlineData("ALWAYS", CriterionScoringScope.Always)]
    [InlineData("  WhenTargeted ", CriterionScoringScope.WhenTargeted)]
    [InlineData(null, CriterionScoringScope.Always)]
    [InlineData("", CriterionScoringScope.Always)]
    public void Scope_Parse_TheoTen_KhongPhanBietHoaThuong(string? raw, CriterionScoringScope expected)
        => Assert.Equal(expected, CampaignSvc.ParseScoringScope(raw, "X"));

    [Theory]
    [InlineData("Sometimes")]
    [InlineData("1")]
    [InlineData("0")]
    public async Task Scope_GiaTriLa_400_NeuTenTieuChi_KhongGhi(string raw)
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var camp = await SeedAsync(tdb, org, criteria: [Crit(Guid.Empty, 0, "Giữ", 1.0m)]);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            NewService(tdb.NewContext()).UpdateCampaignAsync(org, org, camp.Id, new UpdateCampaignRequest
            {
                Criteria = new List<CriterionItem>
                {
                    new() { Name = "Tiêu chí lạ", Weight = 1.0m, MaxScore = 5, ScoringScope = raw },
                }
            }, default));

        Assert.Contains("Tiêu chí lạ", ex.Message);
        Assert.Contains("scoringScope", ex.Message);

        using var check = tdb.NewContext();
        var rows = await check.CampaignCriteria.Where(c => c.CampaignId == camp.Id).ToListAsync();
        Assert.Equal("Giữ", Assert.Single(rows).Name);   // 0 row ghi
    }

    // Create campaign mang scope ⇒ lưu đúng từ đầu.
    [Fact]
    public async Task Scope_CreateCampaign_LuuDung()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();

        var res = await NewService(tdb.NewContext()).CreateCampaignAsync(org, org, new CreateCampaignRequest
        {
            Title = "T", Domain = "BE", TimeLimitMinutes = 30,
            StartsAt = DateTime.UtcNow.AddDays(1), ExpiresAt = DateTime.UtcNow.AddDays(10),
            Questions = new List<QuestionItem>(),
            Criteria = new List<CriterionItem>
            {
                new() { Name = "A", Weight = 0.5m, MaxScore = 5, ScoringScope = "WhenTargeted" },
                new() { Name = "B", Weight = 0.5m, MaxScore = 5 },
            }
        }, default);

        Assert.Equal("WhenTargeted", res.Criteria.Single(c => c.Name == "A").ScoringScope);
        Assert.Equal("Always", res.Criteria.Single(c => c.Name == "B").ScoringScope);
    }

    // I5 — hàng cũ (entity không set scope) ⇒ DB lưu chuỗi 'Always' qua DEFAULT, đọc ra Always.
    [Fact]
    public async Task Scope_HangCu_MacDinhAlways_LuuDangChuoi()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var camp = CampaignTestDb.NewCampaign(org);
        tdb.Db.Campaigns.Add(camp);
        tdb.Db.CampaignCriteria.Add(new CampaignCriterion
        {
            Id = Guid.NewGuid(), CampaignId = camp.Id, OrderNo = 0, Name = "Cũ", Weight = 1.0m, MaxScore = 5,
            Source = CriterionSource.HrEdited, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
            // KHÔNG set ScoringScope
        });
        await tdb.Db.SaveChangesAsync();

        using var check = tdb.NewContext();
        var stored = await check.Database
            .SqlQueryRaw<string>("SELECT scoring_scope AS Value FROM campaign_criteria").SingleAsync();
        Assert.Equal("Always", stored);

        var entity = await check.CampaignCriteria.SingleAsync(c => c.CampaignId == camp.Id);
        Assert.Equal(CriterionScoringScope.Always, entity.ScoringScope);
        Assert.Equal("Always", CampaignResponse.FromEntity(await check.Campaigns
            .Include(c => c.Criteria).ThenInclude(c => c.Levels).SingleAsync(c => c.Id == camp.Id))
            .Criteria.Single().ScoringScope);
    }

    // CHECK là danh sách ĐÓNG — kèm đối chứng dương trong cùng test (tiền lệ CriterionSourceSystemDefaultTests).
    [Fact]
    public async Task Scope_Check_DanhSachDong()
    {
        using var tdb = new CampaignTestDb();
        var camp = CampaignTestDb.NewCampaign(Guid.NewGuid());
        tdb.Db.Campaigns.Add(camp);
        await tdb.Db.SaveChangesAsync();
        var campIdText = await tdb.Db.Database
            .SqlQueryRaw<string>("SELECT id AS Value FROM campaigns").SingleAsync();

        Task Insert(string scope) => tdb.Db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO campaign_criteria
                (id, campaign_id, order_no, name, weight, max_score, source, scoring_scope, created_at, updated_at)
            VALUES ({0}, {1}, {2}, {3}, 1.0, 5, 'HrEdited', {4}, {5}, {5})
            """,
            Guid.NewGuid().ToString().ToUpperInvariant(), campIdText,
            Random.Shared.Next(1, 100_000), $"Crit-{Guid.NewGuid():N}", scope, DateTime.UtcNow);

        var bad = await Record.ExceptionAsync(() => Insert("Sometimes"));
        var good = await Record.ExceptionAsync(() => Insert("WhenTargeted"));

        Assert.NotNull(bad);
        Assert.Null(good);
    }

    // ═══════════════ B. targetCriterionIds — ba trạng thái ═══════════════

    // Vắng (null) ⇒ GIỮ NGUYÊN nhãn đang có. Đây là vế FE-cũ: PUT không được xoá hộ.
    [Fact]
    public async Task Targets_Vang_GiuNguyen()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var a = Crit(Guid.Empty, 0, "A", 1.0m, CriterionScoringScope.WhenTargeted);
        var q = Q(Guid.Empty, org, "câu", [a.Id]);
        var camp = await SeedAsync(tdb, org, criteria: [a], questions: [q]);

        await NewService(tdb.NewContext()).UpdateCampaignQuestionsAsync(org, org, camp.Id, new List<QuestionItem>
        {
            new() { Id = q.Id, QuestionText = "câu (sửa chữ)" }   // không nhắc tới targetCriterionIds
        }, default);

        var rows = await QuestionsAsync(tdb, camp.Id);
        Assert.Equal(new[] { a.Id }, Assert.Single(rows).TargetCriterionIds);
    }

    // [] ⇒ XOÁ nhãn, nhưng lưu [] (đã xét, không nhắm) — KHÔNG phải NULL.
    [Fact]
    public async Task Targets_MangRong_LuuMangRong_KhongPhaiNull()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var a = Crit(Guid.Empty, 0, "A", 1.0m, CriterionScoringScope.WhenTargeted);
        var q = Q(Guid.Empty, org, "câu", [a.Id]);
        var camp = await SeedAsync(tdb, org, criteria: [a], questions: [q]);

        var res = await NewService(tdb.NewContext()).UpdateCampaignQuestionsAsync(org, org, camp.Id, new List<QuestionItem>
        {
            new() { Id = q.Id, QuestionText = "câu", TargetCriterionIds = new List<Guid>() }
        }, default);

        Assert.NotNull(res.Questions.Single().TargetCriterionIds);
        Assert.Empty(res.Questions.Single().TargetCriterionIds!);

        var rows = await QuestionsAsync(tdb, camp.Id);
        Assert.NotNull(rows.Single().TargetCriterionIds);
        Assert.Empty(rows.Single().TargetCriterionIds!);

        // Tận cột: chuỗi "[]" chứ không phải NULL — phân biệt sống ở TẦNG LƯU, không chỉ ở C#.
        using var check = tdb.NewContext();
        var raw = await check.Database
            .SqlQueryRaw<string>("SELECT target_criterion_ids AS Value FROM campaign_questions").SingleAsync();
        Assert.Equal("[]", raw);
    }

    // null → [] trên câu ĐANG CÓ phải GHI THẬT. Đây là phép đo nhắm vào ValueComparer của EF: comparer
    // nào coi null và [] là "bằng nhau" sẽ khiến DetectChanges thấy row không đổi ⇒ không UPDATE, không
    // lỗi ⇒ nhãn "xã giao" không bao giờ tới DB. Test [a]→[] ở trên KHÔNG bắt được ca này.
    [Fact]
    public async Task Targets_NullSangRong_GhiThatXuongDb()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var q = Q(Guid.Empty, org, "câu", null);
        var camp = await SeedAsync(tdb, org, questions: [q]);

        await NewService(tdb.NewContext()).UpdateCampaignQuestionsAsync(org, org, camp.Id, new List<QuestionItem>
        {
            new() { Id = q.Id, QuestionText = "câu", TargetCriterionIds = new List<Guid>() }
        }, default);

        using var check = tdb.NewContext();
        var raw = await check.Database
            .SqlQueryRaw<string>("SELECT target_criterion_ids AS Value FROM campaign_questions").SingleAsync();
        Assert.Equal("[]", raw);
    }

    // [ids] ⇒ THAY (dedup, giữ thứ tự). Câu MỚI: null ⇒ null, [ids] ⇒ lưu.
    [Fact]
    public async Task Targets_MangId_Thay_DedupGiuThuTu_CauMoiNullVaIds()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var a = Crit(Guid.Empty, 0, "A", 0.5m, CriterionScoringScope.WhenTargeted);
        var b = Crit(Guid.Empty, 1, "B", 0.5m, CriterionScoringScope.WhenTargeted);
        var q = Q(Guid.Empty, org, "câu cũ", [a.Id]);
        var camp = await SeedAsync(tdb, org, criteria: [a, b], questions: [q]);

        await NewService(tdb.NewContext()).UpdateCampaignQuestionsAsync(org, org, camp.Id, new List<QuestionItem>
        {
            new() { Id = q.Id, QuestionText = "câu cũ", TargetCriterionIds = [b.Id, a.Id, b.Id] },
            new() { QuestionText = "câu mới không nhãn" },
            new() { QuestionText = "câu mới có nhãn", TargetCriterionIds = [a.Id] },
        }, default);

        var rows = await QuestionsAsync(tdb, camp.Id);
        Assert.Equal(3, rows.Count);
        Assert.Equal(new[] { b.Id, a.Id }, rows.Single(x => x.QuestionText == "câu cũ").TargetCriterionIds);
        Assert.Null(rows.Single(x => x.QuestionText == "câu mới không nhãn").TargetCriterionIds);
        Assert.Equal(new[] { a.Id }, rows.Single(x => x.QuestionText == "câu mới có nhãn").TargetCriterionIds);
    }

    // id ∉ campaign_criteria ⇒ 400 NÊU id, KHÔNG ghi gì (kể cả text sửa kèm).
    [Fact]
    public async Task Targets_IdLa_400_NeuId_KhongGhi()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var a = Crit(Guid.Empty, 0, "A", 1.0m, CriterionScoringScope.WhenTargeted);
        var q = Q(Guid.Empty, org, "câu", null);
        var camp = await SeedAsync(tdb, org, criteria: [a], questions: [q]);
        var la = Guid.NewGuid();

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            NewService(tdb.NewContext()).UpdateCampaignQuestionsAsync(org, org, camp.Id, new List<QuestionItem>
            {
                new() { Id = q.Id, QuestionText = "câu ĐÃ SỬA", TargetCriterionIds = [a.Id, la] }
            }, default));

        Assert.Contains(la.ToString(), ex.Message);
        Assert.Contains("targetCriterionIds", ex.Message);

        var rows = await QuestionsAsync(tdb, camp.Id);
        Assert.Equal("câu", Assert.Single(rows).QuestionText);
        Assert.Null(rows.Single().TargetCriterionIds);
    }

    // Create: id tiêu chí mint trong cùng request ⇒ [ids] client bịa → 400; [] và null vẫn đi qua đúng nghĩa.
    [Fact]
    public async Task Targets_CreateCampaign_IdsLa400_RongVaNullGiuNghia()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var svc = NewService(tdb.NewContext());

        CreateCampaignRequest Req(List<QuestionItem> qs) => new()
        {
            Title = "T", Domain = "BE", TimeLimitMinutes = 30,
            StartsAt = DateTime.UtcNow.AddDays(1), ExpiresAt = DateTime.UtcNow.AddDays(10),
            Criteria = new List<CriterionItem> { new() { Name = "A", Weight = 1.0m, MaxScore = 5, ScoringScope = "WhenTargeted" } },
            Questions = qs
        };

        await Assert.ThrowsAsync<ArgumentException>(() => svc.CreateCampaignAsync(org, org, Req(new()
        {
            new() { QuestionText = "q", TargetCriterionIds = [Guid.NewGuid()] }
        }), default));

        var res = await svc.CreateCampaignAsync(org, org, Req(new()
        {
            new() { QuestionText = "null" },
            new() { QuestionText = "rỗng", TargetCriterionIds = new List<Guid>() },
        }), default);

        var rows = await QuestionsAsync(tdb, res.Id);
        Assert.Null(rows.Single(x => x.QuestionText == "null").TargetCriterionIds);
        Assert.NotNull(rows.Single(x => x.QuestionText == "rỗng").TargetCriterionIds);
        Assert.Empty(rows.Single(x => x.QuestionText == "rỗng").TargetCriterionIds!);
    }

    // GET luôn trả targetCriterionIds — null giữ null, [] giữ [].
    [Fact]
    public async Task Targets_Get_TraNguyen_NullVaRongKhacNhau()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var a = Crit(Guid.Empty, 0, "A", 1.0m, CriterionScoringScope.WhenTargeted);
        var camp = await SeedAsync(tdb, org, criteria: [a], questions:
        [
            Q(Guid.Empty, org, "null", null),
            Q(Guid.Empty, org, "rỗng", new List<Guid>()),
            Q(Guid.Empty, org, "nhắm", [a.Id]),
        ]);

        var res = await NewService(tdb.NewContext()).GetCampaignAsync(org, camp.Id, default);

        Assert.Null(res.Questions.Single(q => q.QuestionText == "null").TargetCriterionIds);
        Assert.Empty(res.Questions.Single(q => q.QuestionText == "rỗng").TargetCriterionIds!);
        Assert.Equal(new[] { a.Id }, res.Questions.Single(q => q.QuestionText == "nhắm").TargetCriterionIds);
    }

    // ═══════════════ C. Xoá tiêu chí ⇒ nhãn tự cắt (chỉ id chết) ═══════════════

    [Fact]
    public async Task XoaTieuChi_CatNhan_ChiIdChet_IdSongGiu_NullGiuNull_HetIdThanhRong()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var a = Crit(Guid.Empty, 0, "A", 0.5m, CriterionScoringScope.WhenTargeted);
        var b = Crit(Guid.Empty, 1, "B", 0.5m, CriterionScoringScope.WhenTargeted);
        var qAB = Q(Guid.Empty, org, "AB", [a.Id, b.Id]);
        var qB = Q(Guid.Empty, org, "B", [b.Id]);
        var qNull = Q(Guid.Empty, org, "null", null);
        var qA = Q(Guid.Empty, org, "A", [a.Id]);
        var camp = await SeedAsync(tdb, org, criteria: [a, b], questions: [qAB, qB, qNull, qA]);

        // PUT criteria echo A (giữ id), bỏ B.
        await NewService(tdb.NewContext()).UpdateCampaignAsync(org, org, camp.Id, new UpdateCampaignRequest
        {
            Criteria = new List<CriterionItem> { Echo(a, "WhenTargeted", weight: 1.0m) }
        }, default);

        var rows = await QuestionsAsync(tdb, camp.Id);
        Assert.Equal(new[] { a.Id }, rows.Single(q => q.QuestionText == "AB").TargetCriterionIds);   // B cắt, A giữ
        Assert.Equal(new[] { a.Id }, rows.Single(q => q.QuestionText == "A").TargetCriterionIds);    // không đụng
        Assert.Null(rows.Single(q => q.QuestionText == "null").TargetCriterionIds);                   // null giữ null
        var onlyB = rows.Single(q => q.QuestionText == "B").TargetCriterionIds;
        Assert.NotNull(onlyB);
        Assert.Empty(onlyB!);                                                                          // hết id ⇒ [] (đã xét), KHÔNG null

        using var check = tdb.NewContext();
        var audit = await check.AuditLogs.Where(x => x.EntityId == camp.Id && x.Action == AuditAction.EditCriteria)
            .OrderByDescending(x => x.At).FirstAsync();
        Assert.Contains("cắt nhãn", audit.Summary);
        Assert.Contains("2 câu hỏi", audit.Summary);   // AB + B; A và null không tính
    }

    // Không tiêu chí nào bị xoá (sửa mô tả VÀ ĐỔI TÊN, echo đủ id) ⇒ nhãn KHÔNG nhúc nhích, audit không
    // nói "cắt". Đổi tên là ca đáng test nhất: RNK1·HĐ-5 ghép theo ID chứ không theo tên — nếu ai đó
    // "tối ưu" merge sang ghép theo tên thì đổi tên = tiêu chí mới ⇒ nhãn bị cắt im lặng. (T1 tester gap.)
    [Fact]
    public async Task SuaTieuChi_KhongXoa_NhanKhongDoi()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var a = Crit(Guid.Empty, 0, "A", 0.5m, CriterionScoringScope.WhenTargeted);
        var b = Crit(Guid.Empty, 1, "B", 0.5m);
        var q = Q(Guid.Empty, org, "AB", [a.Id, b.Id]);
        var camp = await SeedAsync(tdb, org, criteria: [a, b], questions: [q]);

        await NewService(tdb.NewContext()).UpdateCampaignAsync(org, org, camp.Id, new UpdateCampaignRequest
        {
            Criteria = new List<CriterionItem>
            {
                Echo(a, "WhenTargeted", description: "mô tả mới", name: "A đổi tên"),
                Echo(b, "Always"),
            }
        }, default);

        var rows = await QuestionsAsync(tdb, camp.Id);
        Assert.Equal(new[] { a.Id, b.Id }, Assert.Single(rows).TargetCriterionIds);

        using var renamed = tdb.NewContext();
        var aRow = await renamed.CampaignCriteria.SingleAsync(x => x.Id == a.Id);
        Assert.Equal("A đổi tên", aRow.Name);   // đổi tên đã thật sự chạm DB, giữ ĐÚNG id

        using var check = tdb.NewContext();
        var audit = await check.AuditLogs.Where(x => x.EntityId == camp.Id && x.Action == AuditAction.EditCriteria)
            .OrderByDescending(x => x.At).FirstAsync();
        Assert.DoesNotContain("cắt nhãn", audit.Summary);
    }

    // Helper thuần: cắt đúng id chết, giữ thứ tự id sống, trả số câu bị cắt.
    [Fact]
    public void TrimDanglingQuestionTargets_ChiCatIdChet()
    {
        var keep = Guid.NewGuid(); var keep2 = Guid.NewGuid(); var dead = Guid.NewGuid();
        var qs = new[]
        {
            new CampaignQuestion { QuestionText = "1", TargetCriterionIds = [keep2, dead, keep] },
            new CampaignQuestion { QuestionText = "2", TargetCriterionIds = [keep] },
            new CampaignQuestion { QuestionText = "3", TargetCriterionIds = null },
            new CampaignQuestion { QuestionText = "4", TargetCriterionIds = [dead] },
        };

        var cut = CampaignSvc.TrimDanglingQuestionTargets(qs, new HashSet<Guid> { keep, keep2 });

        Assert.Equal(2, cut);
        Assert.Equal(new[] { keep2, keep }, qs[0].TargetCriterionIds);
        Assert.Equal(new[] { keep }, qs[1].TargetCriterionIds);
        Assert.Null(qs[2].TargetCriterionIds);
        Assert.Empty(qs[3].TargetCriterionIds!);
    }

    // ═══════════════ D. from-system-default ═══════════════

    private const string D0 = "CÓ: không nêu được ý nào | CÒN THIẾU: toàn bộ nội dung câu hỏi";
    private const string D5 = "CÓ: nêu khái niệm, ví dụ và đánh đổi | CÒN THIẾU: chưa nói giới hạn";

    private static Mock<ICampaignSessionClient> StubRubric()
    {
        var rubric = new B2CRubricResponse("BE", "vi", 3, new List<B2CRubricCriterion>
        {
            new("Chiều sâu kỹ thuật", "Hiểu bản chất", 0.6m, 5, new List<B2CRubricLevel> { new(0, D0), new(5, D5) })
                { ScoringScope = "WhenTargeted" },
            new("Giao tiếp", null, 0.4m, 5, Array.Empty<B2CRubricLevel>()),   // mặc định Always
        });
        var m = new Mock<ICampaignSessionClient>();
        m.Setup(x => x.GetB2CRubricAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(rubric);
        return m;
    }

    [Fact]
    public async Task FromSystemDefault_ChepScope_XoaNhanVeNull_AuditClearQuestionTargets()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var old = Crit(Guid.Empty, 0, "Cũ", 1.0m, CriterionScoringScope.WhenTargeted);
        var camp = await SeedAsync(tdb, org, criteria: [old], questions:
        [
            Q(Guid.Empty, org, "nhắm cũ", [old.Id]),
            Q(Guid.Empty, org, "rỗng", new List<Guid>()),
            Q(Guid.Empty, org, "null", null),
        ]);

        var res = await NewService(tdb.NewContext(), StubRubric().Object).ApplySystemDefaultCriteriaAsync(
            org, org, camp.Id, new ApplySystemDefaultCriteriaRequest { JobCategory = "BE", Language = "vi" }, default);

        // Scope đi theo bộ chuẩn.
        Assert.Equal("WhenTargeted", res.Criteria.Single(c => c.Name == "Chiều sâu kỹ thuật").ScoringScope);
        Assert.Equal("Always", res.Criteria.Single(c => c.Name == "Giao tiếp").ScoringScope);

        // Nhãn MỌI câu về null (kể cả [] — bộ mới chưa ai xét), null giữ null.
        var rows = await QuestionsAsync(tdb, camp.Id);
        Assert.Equal(3, rows.Count);
        Assert.All(rows, q => Assert.Null(q.TargetCriterionIds));
        Assert.All(res.Questions, q => Assert.Null(q.TargetCriterionIds));

        // Audit riêng, đếm đúng 2 câu thật sự bị xoá nhãn ("null" không tính). CHECK ck_audit_logs_action
        // của SQLite (EF10 enforce) đã nhận giá trị mới — nếu không SaveChanges đã ném.
        using var check = tdb.NewContext();
        var audit = await check.AuditLogs
            .SingleAsync(x => x.EntityId == camp.Id && x.Action == AuditAction.ClearQuestionTargets);
        Assert.Contains("2 câu hỏi", audit.Summary);
        var stored = await check.Database
            .SqlQueryRaw<string>("SELECT action AS Value FROM audit_logs WHERE action = 'ClearQuestionTargets'")
            .SingleAsync();
        Assert.Equal("ClearQuestionTargets", stored);
    }

    // Không câu nào có nhãn ⇒ không có gì để xoá ⇒ KHÔNG ghi audit ClearQuestionTargets (audit là vết
    // của thay đổi, không phải vết của việc gọi endpoint).
    [Fact]
    public async Task FromSystemDefault_KhongCoNhan_KhongAuditClear()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var camp = await SeedAsync(tdb, org, questions: [Q(Guid.Empty, org, "null", null)]);

        await NewService(tdb.NewContext(), StubRubric().Object).ApplySystemDefaultCriteriaAsync(
            org, org, camp.Id, new ApplySystemDefaultCriteriaRequest { JobCategory = "BE", Language = "vi" }, default);

        using var check = tdb.NewContext();
        Assert.False(await check.AuditLogs.AnyAsync(x => x.Action == AuditAction.ClearQuestionTargets));
        Assert.True(await check.AuditLogs.AnyAsync(x => x.EntityId == camp.Id && x.Action == AuditAction.EditCriteria));
    }

    // ═══════════════ E. Scope đổi khi Active ⇒ bump rubric_version ═══════════════

    [Fact]
    public async Task Active_DoiScope_BumpVersion()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var a = Crit(Guid.Empty, 0, "A", 1.0m, CriterionScoringScope.Always);
        var camp = await SeedAsync(tdb, org, CampaignStatus.Active, criteria: [a]);

        await NewService(tdb.NewContext()).UpdateCampaignAsync(org, org, camp.Id, new UpdateCampaignRequest
        {
            Criteria = new List<CriterionItem> { Echo(a, "WhenTargeted") }
        }, default);

        using var check = tdb.NewContext();
        var after = await check.Campaigns.FirstAsync(c => c.Id == camp.Id);
        Assert.Equal(2, after.RubricVersion);
        Assert.Equal(org, after.RubricVersionUpdatedBy);
        Assert.Equal(CriterionScoringScope.WhenTargeted,
            (await check.CampaignCriteria.SingleAsync(c => c.Id == a.Id)).ScoringScope);   // id GIỮ
    }

    [Fact]
    public async Task Active_ScopeKhongDoi_KhongBump()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var a = Crit(Guid.Empty, 0, "A", 1.0m, CriterionScoringScope.WhenTargeted);
        var camp = await SeedAsync(tdb, org, CampaignStatus.Active, criteria: [a]);

        await NewService(tdb.NewContext()).UpdateCampaignAsync(org, org, camp.Id, new UpdateCampaignRequest
        {
            Criteria = new List<CriterionItem> { Echo(a, "whentargeted") }   // cùng giá trị, khác hoa/thường
        }, default);

        using var check = tdb.NewContext();
        Assert.Equal(1, (await check.Campaigns.FirstAsync(c => c.Id == camp.Id)).RubricVersion);
    }

    // ═══════════════ F. Vân tay có scope ═══════════════

    [Fact]
    public void Vantay_CoScope_ChiKhacScope_LaKhacVanTay()
    {
        var cid = Guid.NewGuid();
        var always = Crit(cid, 0, "A", 1.0m, CriterionScoringScope.Always);
        var targeted = Crit(cid, 0, "A", 1.0m, CriterionScoringScope.WhenTargeted);

        Assert.NotEqual(RubricFingerprint.Compute([always]), RubricFingerprint.Compute([targeted]));
        Assert.Contains("\"Scope\":\"WhenTargeted\"", RubricFingerprint.Canonicalize([targeted]));
        Assert.Contains("\"Scope\":\"Always\"", RubricFingerprint.Canonicalize([always]));
    }

    // Snapshot Shared: caller 6-tham-số (Interview) mặc định Scope="Always" — cùng vân tay với Always tường minh.
    [Fact]
    public void Snapshot_MacDinhAlways_BangVanTayAlwaysTuongMinh()
    {
        var levels = new List<Isas.Shared.Rubric.RubricLevelSnapshot>();
        var macDinh = new Isas.Shared.Rubric.RubricCriterionSnapshot(0, "A", null, 1.0m, 5, levels);
        var tuongMinh = new Isas.Shared.Rubric.RubricCriterionSnapshot(0, "A", null, 1.0m, 5, levels, "Always");
        var khac = new Isas.Shared.Rubric.RubricCriterionSnapshot(0, "A", null, 1.0m, 5, levels, "WhenTargeted");

        Assert.Equal(Isas.Shared.Rubric.RubricFingerprint.Compute([macDinh]),
            Isas.Shared.Rubric.RubricFingerprint.Compute([tuongMinh]));
        Assert.NotEqual(Isas.Shared.Rubric.RubricFingerprint.Compute([macDinh]),
            Isas.Shared.Rubric.RubricFingerprint.Compute([khac]));
    }

    // ═══════════════ G. Hợp đồng JSON (khoá camelCase, serialize THẬT) ═══════════════

    [Fact]
    public void Json_Response_KhoaCamelCase_NullVaRongKhacNhau()
    {
        var org = Guid.NewGuid();
        var camp = CampaignTestDb.NewCampaign(org);
        var a = Crit(camp.Id, 0, "A", 1.0m, CriterionScoringScope.WhenTargeted);
        camp.Criteria.Add(a);
        camp.Questions.Add(Q(camp.Id, org, "nhắm", [a.Id]));
        camp.Questions.Add(Q(camp.Id, org, "rỗng", new List<Guid>()));
        camp.Questions.Add(Q(camp.Id, org, "null", null));

        var json = JsonSerializer.Serialize(CampaignResponse.FromEntity(camp), RuntimeJson());

        Assert.Contains("\"scoringScope\":\"WhenTargeted\"", json);
        Assert.Contains($"\"targetCriterionIds\":[\"{a.Id}\"]", json);
        Assert.Contains("\"targetCriterionIds\":[]", json);
        Assert.Contains("\"targetCriterionIds\":null", json);
        Assert.DoesNotContain("\"ScoringScope\"", json);
        Assert.DoesNotContain("\"TargetCriterionIds\"", json);
    }

    // Deserialize THẬT phía nhận: vắng ⇒ null, [] ⇒ [] (không gộp), khoá đúng tên.
    [Fact]
    public void Json_Request_VangLaNull_RongLaRong()
    {
        var o = RuntimeJson();
        var vang = JsonSerializer.Deserialize<QuestionItem>("""{"questionText":"q"}""", o)!;
        var rong = JsonSerializer.Deserialize<QuestionItem>("""{"questionText":"q","targetCriterionIds":[]}""", o)!;
        var id = Guid.NewGuid();
        var co = JsonSerializer.Deserialize<QuestionItem>($$"""{"questionText":"q","targetCriterionIds":["{{id}}"]}""", o)!;
        var scope = JsonSerializer.Deserialize<CriterionItem>("""{"name":"A","weight":1,"maxScore":5,"scoringScope":"WhenTargeted"}""", o)!;

        Assert.Null(vang.TargetCriterionIds);
        Assert.NotNull(rong.TargetCriterionIds);
        Assert.Empty(rong.TargetCriterionIds!);
        Assert.Equal(new[] { id }, co.TargetCriterionIds);
        Assert.Equal("WhenTargeted", scope.ScoringScope);
    }
}
