using Isas.CampaignService.DTOs;
using Isas.CampaignService.Models;
using Isas.CampaignService.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;

using CampaignSvc = Isas.CampaignService.Services.CampaignService;

namespace Isas.CampaignService.Tests;

/// <summary>
/// CAMP-20 — <c>POST /campaign/{id}/criteria/from-system-default</c>: Employer chép bộ chuẩn B2C
/// (admin soạn) vào chiến dịch của mình.
///
/// <para>Hai nhóm test đắt nhất ở đây:</para>
/// <list type="number">
/// <item><b>Nghề phải do HR chọn, KHÔNG suy từ <c>campaigns.domain</c>.</b> Cột đó là chuỗi tự do
/// đang chứa cả "Fullstack"/"QA"/null; chép nhầm bộ chuẩn của nghề khác KHÔNG có triệu chứng nào —
/// ứng viên vẫn thi, AI vẫn chấm, bảng xếp hạng vẫn ra số, chỉ là số đó đo bằng thước của nghề khác.</item>
/// <item><b>Interview hỏng thì KHÔNG được ghi gì.</b> Ném giữa chừng replace-all sẽ để lại một chiến
/// dịch không tiêu chí nào.</item>
/// </list>
/// </summary>
public class SystemDefaultCriteriaTests
{
    private static IConfiguration Config(bool bilingual = true) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Campaign:Bilingual:Enabled"] = bilingual ? "true" : "false"
        }).Build();

    private static CampaignSvc NewService(
        CampaignDbContext db, ICampaignSessionClient session, bool bilingual = true) =>
        new(db, Mock.Of<IFileService>(), Mock.Of<ILogger<CampaignSvc>>(), Mock.Of<IParserService>(),
            Mock.Of<ICriteriaSuggester>(), Mock.Of<IInvitationEmailPublisher>(),
            sessionClient: session, config: Config(bilingual));

    // Mô tả mốc phải dài ≥20 ký tự (CriterionLevelRules.DescriptorMin) — bộ chuẩn của admin đi qua
    // ĐÚNG luật dùng chung này bên Interview, nên fixture cũng phải thoả, kẻo test đo nhầm sang
    // nhánh "bộ chuẩn không hợp lệ".
    private const string D0 = "CÓ: không nêu được ý nào | CÒN THIẾU: toàn bộ nội dung câu hỏi";
    private const string D3 = "CÓ: nêu đúng khái niệm | CÒN THIẾU: chưa nói được đánh đổi";
    private const string D5 = "CÓ: nêu khái niệm, ví dụ và đánh đổi | CÒN THIẾU: chưa nói giới hạn";

    /// <summary>Bộ chuẩn 7 tiêu chí Σweight = 1, hai tiêu chí đầu có mốc, phần còn lại chưa khai.</summary>
    private static B2CRubricResponse Rubric7(string jobCategory = "BE", string language = "vi", int version = 3)
        => new(jobCategory, language, version, new List<B2CRubricCriterion>
        {
            new("Giao tiếp & trình bày", "Diễn đạt rõ ràng", 0.10m, 5, new List<B2CRubricLevel>
            {
                new(0, D0), new(5, D5)
            }),
            new("Chiều sâu kỹ thuật", "Hiểu bản chất", 0.30m, 5, new List<B2CRubricLevel>
            {
                new(0, D0), new(3, D3), new(5, D5)
            }),
            new("Thiết kế hệ thống & CSDL", null, 0.20m, 5, Array.Empty<B2CRubricLevel>()),
            new("Giải quyết vấn đề & thuật toán", null, 0.15m, 5, Array.Empty<B2CRubricLevel>()),
            new("Trôi chảy", null, 0.10m, 5, Array.Empty<B2CRubricLevel>()),
            new("Ngữ pháp & dùng từ", null, 0.10m, 5, Array.Empty<B2CRubricLevel>()),
            new("Thuật ngữ chuyên ngành", null, 0.05m, 5, Array.Empty<B2CRubricLevel>()),
        });

    private static Mock<ICampaignSessionClient> StubRubric(B2CRubricResponse rubric)
    {
        var m = new Mock<ICampaignSessionClient>();
        m.Setup(x => x.GetB2CRubricAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(rubric);
        return m;
    }

    private static Mock<ICampaignSessionClient> StubThrows(Exception ex)
    {
        var m = new Mock<ICampaignSessionClient>();
        m.Setup(x => x.GetB2CRubricAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(ex);
        return m;
    }

    private static async Task<Campaign> SeedAsync(
        CampaignTestDb tdb, Guid org, CampaignStatus status = CampaignStatus.Draft,
        string? domain = null, params CampaignCriterion[] criteria)
    {
        var camp = CampaignTestDb.NewCampaign(org, status);
        camp.Domain = domain;
        tdb.Db.Campaigns.Add(camp);
        foreach (var c in criteria) { c.CampaignId = camp.Id; tdb.Db.CampaignCriteria.Add(c); }
        await tdb.Db.SaveChangesAsync();
        return camp;
    }

    private static CampaignCriterion Crit(string name, decimal weight, int order = 0, int maxScore = 5)
        => new()
        {
            Id = Guid.NewGuid(), OrderNo = order, Name = name, Weight = weight, MaxScore = maxScore,
            Source = CriterionSource.HrEdited, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        };

    private static ApplySystemDefaultCriteriaRequest Req(string? job = "BE", string? lang = "vi")
        => new() { JobCategory = job, Language = lang };

    // ── Đường thành công ────────────────────────────────────────────────

    // Chép về đủ 7 tiêu chí + mốc, nhãn SystemDefault, Σweight = 1.
    [Fact]
    public async Task ChepVe_Du7TieuChi_KemMoc_NhanSystemDefault_SumWeight1()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var camp = await SeedAsync(tdb, org);
        var svc = NewService(tdb.NewContext(), StubRubric(Rubric7()).Object);

        await svc.ApplySystemDefaultCriteriaAsync(org, org, camp.Id, Req(), default);

        using var check = tdb.NewContext();
        var rows = await check.CampaignCriteria
            .Include(c => c.Levels)
            .Where(c => c.CampaignId == camp.Id)
            .OrderBy(c => c.OrderNo).ToListAsync();

        Assert.Equal(7, rows.Count);
        Assert.All(rows, r => Assert.Equal(CriterionSource.SystemDefault, r.Source));
        Assert.Equal(1.0m, rows.Sum(r => r.Weight));

        // OrderNo theo weight giảm dần → tiêu chí nặng nhất (0.30) đứng đầu.
        Assert.Equal("Chiều sâu kỹ thuật", rows[0].Name);
        Assert.Equal(3, rows[0].Levels.Count);
        Assert.Equal(new[] { 0, 3, 5 }, rows[0].Levels.OrderBy(l => l.Score).Select(l => l.Score));
        Assert.Equal(D5, rows[0].Levels.Single(l => l.Score == 5).Descriptor);

        // Mô tả đi theo; tiêu chí chưa khai mốc thì rỗng (hợp lệ — Interview dùng dải mặc định).
        Assert.Equal("Hiểu bản chất", rows[0].Description);
        Assert.Empty(rows.Single(r => r.Name == "Trôi chảy").Levels);
    }

    // Σweight LỆCH (hai service dùng scale numeric khác nhau) → CHUẨN HOÁ, không tin số nhận về.
    //
    // 🔴 Assert vào PHÂN BỐ chứ không chỉ vào tổng. Dòng "sửa sai số làm tròn"
    // (`criteria[0].Weight += 1 - Σ`) ép Σ = 1 BẰNG CẤU TRÚC, nên `Sum == 1.0m` đúng ở cả bản có
    // chuẩn hoá lẫn bản không — bản đầu của test này chỉ assert tổng và vì thế mutation "bỏ chuẩn
    // hoá" chạy qua XANH. Thứ thật sự vỡ khi bỏ chuẩn hoá là phân bố: toàn bộ sai lệch dồn hết vào
    // tiêu chí ĐẦU, nên hai tiêu chí admin khai BẰNG NHAU lại được lưu khác nhau — mà B2B xếp hạng
    // bằng Σ(điểm × weight), tức thứ hạng đổi trong im lặng.
    [Fact]
    public async Task SumWeightLech_ChuanHoaTheoTyLe_KhongDonHetVaoTieuChiDau()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var camp = await SeedAsync(tdb, org);
        // Tổng 1.01 — vẫn trong dải [0.99, 1.01] mà BuildStructuredCriteria chấp nhận.
        // A và B được khai BẰNG NHAU; nếu bỏ chuẩn hoá thì A gánh trọn −0.01 và thành 0.49 ≠ B 0.50.
        var lech = new B2CRubricResponse("BE", "vi", 1, new List<B2CRubricCriterion>
        {
            new("A", null, 0.50m, 5, Array.Empty<B2CRubricLevel>()),
            new("B", null, 0.50m, 5, Array.Empty<B2CRubricLevel>()),
            new("C", null, 0.01m, 5, Array.Empty<B2CRubricLevel>()),
        });
        var svc = NewService(tdb.NewContext(), StubRubric(lech).Object);

        await svc.ApplySystemDefaultCriteriaAsync(org, org, camp.Id, Req(), default);

        using var check = tdb.NewContext();
        var rows = await check.CampaignCriteria.Where(c => c.CampaignId == camp.Id)
            .ToDictionaryAsync(c => c.Name, c => c.Weight);

        decimal a = rows["A"], b = rows["B"], c = rows["C"];
        Assert.Equal(1.0m, rows.Values.Sum());
        // Bằng nhau lúc khai ⇒ bằng nhau lúc lưu, sai lệch chỉ ở mức làm tròn numeric(5,4).
        Assert.True(Math.Abs(a - b) <= 0.0001m,
            $"A={a} và B={b} được khai bằng nhau nhưng lưu khác nhau");
        // Và tiêu chí bé không bị nuốt: giữ đúng tỉ lệ ~0.01/1.01.
        Assert.True(c > 0.0090m && c < 0.0110m, $"C={c}");
    }

    // Campaign ĐÃ có tiêu chí → THAY THẾ, không nhân đôi (replace-all như PUT criteria).
    [Fact]
    public async Task DaCoTieuChi_ThayThe_KhongNhanDoi()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var camp = await SeedAsync(tdb, org, CampaignStatus.Draft, null,
            Crit("Cũ 1", 0.5m, 0), Crit("Cũ 2", 0.5m, 1));
        var svc = NewService(tdb.NewContext(), StubRubric(Rubric7()).Object);

        await svc.ApplySystemDefaultCriteriaAsync(org, org, camp.Id, Req(), default);

        using var check = tdb.NewContext();
        var rows = await check.CampaignCriteria.Where(c => c.CampaignId == camp.Id).ToListAsync();
        Assert.Equal(7, rows.Count);
        Assert.DoesNotContain(rows, r => r.Name.StartsWith("Cũ "));
    }

    // 🔴 Mốc CŨ của chiến dịch KHÔNG được carry-over sang bộ vừa chép, kể cả khi TRÙNG TÊN tiêu chí.
    // Carry-over (CAMP-16) tồn tại cho ca "HR lưu mà không gửi levels"; ở đây HR đang đổi hẳn thước
    // đo, nên mốc do họ viết cho thước cũ mà đội lên bộ chuẩn = trộn hai bộ, không triệu chứng.
    [Fact]
    public async Task KhongCarryOver_MocCu_DuTrungTenTieuChi()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        const string mocCuaHr = "MỐC CŨ CỦA HR: mô tả này thuộc về một thước đo khác hẳn";
        var cu = Crit("Trôi chảy", 1.0m, 0);   // trùng tên với một tiêu chí của bộ chuẩn (bộ đó KHÔNG có mốc)
        var camp = await SeedAsync(tdb, org, CampaignStatus.Draft, null, cu);
        tdb.Db.CampaignCriterionLevels.Add(new CampaignCriterionLevel
        {
            Id = Guid.NewGuid(), CriterionId = cu.Id, Score = 5, Descriptor = mocCuaHr,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
        await tdb.Db.SaveChangesAsync();

        var svc = NewService(tdb.NewContext(), StubRubric(Rubric7()).Object);
        await svc.ApplySystemDefaultCriteriaAsync(org, org, camp.Id, Req(), default);

        using var check = tdb.NewContext();
        var troiChay = await check.CampaignCriteria.Include(c => c.Levels)
            .SingleAsync(c => c.CampaignId == camp.Id && c.Name == "Trôi chảy");
        Assert.Empty(troiChay.Levels);

        var moiMoc = await check.CampaignCriterionLevels
            .Where(l => l.Criterion.CampaignId == camp.Id).ToListAsync();
        Assert.DoesNotContain(moiMoc, l => l.Descriptor == mocCuaHr);
    }

    // ── Version (CAMP-18) ───────────────────────────────────────────────

    // Active vẫn chép được (ngoại lệ có chủ đích với CAMP-2) và PHẢI bump version — ứng viên đã thi
    // được chấm bằng thước cũ, người thi sau bằng thước mới, bảng xếp hạng phân biệt được hai nhóm.
    [Fact]
    public async Task Active_ChepDuoc_VaBumpVersion()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var camp = await SeedAsync(tdb, org, CampaignStatus.Active, null, Crit("Cũ", 1.0m, 0));
        var truoc = camp.RubricVersion;
        var svc = NewService(tdb.NewContext(), StubRubric(Rubric7()).Object);

        await svc.ApplySystemDefaultCriteriaAsync(org, org, camp.Id, Req(), default);

        using var check = tdb.NewContext();
        var sau = await check.Campaigns.SingleAsync(c => c.Id == camp.Id);
        Assert.Equal(truoc + 1, sau.RubricVersion);
        Assert.Equal(7, await check.CampaignCriteria.CountAsync(c => c.CampaignId == camp.Id));
    }

    // Draft KHÔNG bump: chưa ai bị chấm bằng bộ này, đánh số ở đó chỉ đẻ lỗ số vô nghĩa.
    [Fact]
    public async Task Draft_KhongBumpVersion()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var camp = await SeedAsync(tdb, org, CampaignStatus.Draft);
        var truoc = camp.RubricVersion;
        var svc = NewService(tdb.NewContext(), StubRubric(Rubric7()).Object);

        await svc.ApplySystemDefaultCriteriaAsync(org, org, camp.Id, Req(), default);

        using var check = tdb.NewContext();
        Assert.Equal(truoc, (await check.Campaigns.SingleAsync(c => c.Id == camp.Id)).RubricVersion);
    }

    // Audit nói ra ĐÃ CHÉP TỪ ĐÂU (nghề/ngôn ngữ/bản mấy) — thông tin duy nhất cho phép truy ngược
    // vì sao thước đo của chiến dịch này trông như vậy.
    [Fact]
    public async Task GhiAudit_KemNguonVaPhienBan()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var actor = Guid.NewGuid();
        var camp = await SeedAsync(tdb, org);
        var svc = NewService(tdb.NewContext(), StubRubric(Rubric7("BE", "vi", 3)).Object);

        await svc.ApplySystemDefaultCriteriaAsync(org, actor, camp.Id, Req(), default);

        using var check = tdb.NewContext();
        var audit = await check.AuditLogs
            .SingleAsync(a => a.EntityId == camp.Id && a.Action == AuditAction.EditCriteria);
        Assert.Equal(actor, audit.ActorUserId);
        Assert.Contains("BE", audit.Summary);
        Assert.Contains("v3", audit.Summary);
    }

    // ── Nghề: HR chọn, server KHÔNG đoán ────────────────────────────────

    // 🔴 `campaigns.domain` = "BE" mà request thiếu jobCategory ⇒ vẫn 400. Đây là phép phân biệt dứt
    // khoát giữa "bắt buộc" và "có mặc định lấy từ Domain": nếu server đoán thì test này XANH sai.
    [Fact]
    public async Task ThieuJobCategory_400_DuDomainDaCoGiaTriHopLe()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var camp = await SeedAsync(tdb, org, CampaignStatus.Draft, domain: "BE");
        var session = StubRubric(Rubric7());
        var svc = NewService(tdb.NewContext(), session.Object);

        await Assert.ThrowsAsync<ArgumentException>(
            () => svc.ApplySystemDefaultCriteriaAsync(org, org, camp.Id, Req(job: null), default));

        // Không được gọi Interview: lỗi của HR không được đội lốt lỗi hệ thống.
        session.Verify(x => x.GetB2CRubricAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // Domain rác ("Fullstack") + HR chọn BE ⇒ dùng ĐÚNG lựa chọn của HR, Domain bị bỏ qua hoàn toàn.
    [Fact]
    public async Task DomainRac_VanChepDuoc_TheoLuaChonCuaHr()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var camp = await SeedAsync(tdb, org, CampaignStatus.Draft, domain: "Fullstack");
        var session = StubRubric(Rubric7());
        var svc = NewService(tdb.NewContext(), session.Object);

        await svc.ApplySystemDefaultCriteriaAsync(org, org, camp.Id, Req(job: "BE"), default);

        session.Verify(x => x.GetB2CRubricAsync("BE", "vi", It.IsAny<CancellationToken>()), Times.Once);
    }

    // Nghề ngoài tập → 400 (không im lặng rơi về BE).
    [Theory]
    [InlineData("Fullstack")]
    [InlineData("QA")]
    [InlineData("")]
    [InlineData("   ")]
    public async Task JobCategoryNgoaiTap_400(string job)
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var camp = await SeedAsync(tdb, org);
        var svc = NewService(tdb.NewContext(), StubRubric(Rubric7()).Object);

        await Assert.ThrowsAsync<ArgumentException>(
            () => svc.ApplySystemDefaultCriteriaAsync(org, org, camp.Id, Req(job: job), default));
    }

    // HOA/thường tuỳ ý → chuẩn hoá về dạng chính tắc trước khi vào query string. "be" gửi nguyên sang
    // Interview sẽ quay về 404 "chưa có bộ chuẩn" — một thông điệp sai hoàn toàn về nguyên nhân.
    [Theory]
    [InlineData("be")]
    [InlineData("Be")]
    [InlineData(" BE ")]
    public async Task JobCategory_ChuanHoaHoaThuong(string job)
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var camp = await SeedAsync(tdb, org);
        var session = StubRubric(Rubric7());
        var svc = NewService(tdb.NewContext(), session.Object);

        await svc.ApplySystemDefaultCriteriaAsync(org, org, camp.Id, Req(job: job), default);

        session.Verify(x => x.GetB2CRubricAsync("BE", "vi", It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── Ngôn ngữ ────────────────────────────────────────────────────────

    // `language` BẮT BUỘC — khác đường tạo/sửa campaign nơi null = "không khai" → "vi". Ở đây lấy "vi"
    // ngầm nghĩa là HR định chép bộ EN mà FE quên gửi field sẽ nhận mô tả tiếng Việt, im lặng và sai.
    [Fact]
    public async Task ThieuLanguage_400()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var camp = await SeedAsync(tdb, org);
        var session = StubRubric(Rubric7());
        var svc = NewService(tdb.NewContext(), session.Object);

        await Assert.ThrowsAsync<ArgumentException>(
            () => svc.ApplySystemDefaultCriteriaAsync(org, org, camp.Id, Req(lang: null), default));

        session.Verify(x => x.GetB2CRubricAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // Luật vi/en + cổng song ngữ dùng chung ValidateLanguage (một bản luật duy nhất).
    [Fact]
    public async Task BilingualTat_XinEn_400()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var camp = await SeedAsync(tdb, org);
        var svc = NewService(tdb.NewContext(), StubRubric(Rubric7()).Object, bilingual: false);

        await Assert.ThrowsAsync<ArgumentException>(
            () => svc.ApplySystemDefaultCriteriaAsync(org, org, camp.Id, Req(lang: "en"), default));
    }

    [Fact]
    public async Task Language_ChuanHoaVeChuThuong()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var camp = await SeedAsync(tdb, org);
        var session = StubRubric(Rubric7("BE", "en"));
        var svc = NewService(tdb.NewContext(), session.Object);

        await svc.ApplySystemDefaultCriteriaAsync(org, org, camp.Id, Req(lang: "EN"), default);

        session.Verify(x => x.GetB2CRubricAsync("BE", "en", It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── Guard vòng đời + quyền ──────────────────────────────────────────

    [Theory]
    [InlineData(CampaignStatus.Closed)]
    [InlineData(CampaignStatus.Archived)]
    public async Task DaDong_409_VaKhongGoiInterview(CampaignStatus status)
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var camp = await SeedAsync(tdb, org, status, null, Crit("Cũ", 1.0m, 0));
        var session = StubRubric(Rubric7());
        var svc = NewService(tdb.NewContext(), session.Object);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.ApplySystemDefaultCriteriaAsync(org, org, camp.Id, Req(), default));

        session.Verify(x => x.GetB2CRubricAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);

        using var check = tdb.NewContext();
        var rows = await check.CampaignCriteria.Where(c => c.CampaignId == camp.Id).ToListAsync();
        Assert.Equal("Cũ", Assert.Single(rows).Name);   // thước đo lịch sử còn nguyên
    }

    [Fact]
    public async Task NgoaiOrg_404()
    {
        using var tdb = new CampaignTestDb();
        var camp = await SeedAsync(tdb, Guid.NewGuid());
        var svc = NewService(tdb.NewContext(), StubRubric(Rubric7()).Object);

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => svc.ApplySystemDefaultCriteriaAsync(Guid.NewGuid(), Guid.NewGuid(), camp.Id, Req(), default));
    }

    // ── Interview hỏng ──────────────────────────────────────────────────

    // 🔴 Interview lỗi/chưa có bộ chuẩn → ném VÀ chiến dịch giữ nguyên tiêu chí đang có. Ném GIỮA
    // CHỪNG replace-all sẽ để lại một chiến dịch không tiêu chí nào — mà Interview vẫn chấm ra điểm.
    [Fact]
    public async Task InterviewLoi_502_VaKhongGhiGi()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var camp = await SeedAsync(tdb, org, CampaignStatus.Draft, null,
            Crit("Giữ nguyên", 1.0m, 0));
        var svc = NewService(tdb.NewContext(),
            StubThrows(new DownstreamServiceException("Chưa có bộ chuẩn cho (BE, vi)")).Object);

        await Assert.ThrowsAsync<DownstreamServiceException>(
            () => svc.ApplySystemDefaultCriteriaAsync(org, org, camp.Id, Req(), default));

        using var check = tdb.NewContext();
        var rows = await check.CampaignCriteria.Where(c => c.CampaignId == camp.Id).ToListAsync();
        Assert.Equal("Giữ nguyên", Assert.Single(rows).Name);
        Assert.Empty(await check.AuditLogs.Where(a => a.EntityId == camp.Id).ToListAsync());
    }

    // Bộ chuẩn KHÔNG thoả CriterionLevelRules (admin soạn thiếu mốc 0, mô tả quá ngắn…) → 502 chứ
    // KHÔNG 400. Ở điểm này mọi thứ vào builder đều đến từ Interview — hai trường HR gửi đã validate
    // xong từ đầu hàm — nên 400 sẽ bắt HR đi tìm mình gõ sai chỗ nào trên một request có đúng hai
    // trường: ngõ cụt. Thông điệp phải nêu đích danh bộ chuẩn nào hỏng để họ báo quản trị viên.
    [Fact]
    public async Task BoChuanKhongThoaLuatMoc_502_NeuDichDanhBoNao()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var camp = await SeedAsync(tdb, org, CampaignStatus.Draft, null, Crit("Giữ nguyên", 1.0m, 0));
        // Thiếu mốc 0 → câu trả lời trống bị chấm về mốc thấp nhất đang có (CriterionLevelRules).
        var hong = new B2CRubricResponse("BE", "vi", 4, new List<B2CRubricCriterion>
        {
            new("A", null, 1.0m, 5, new List<B2CRubricLevel> { new(3, D3), new(5, D5) })
        });
        var svc = NewService(tdb.NewContext(), StubRubric(hong).Object);

        var ex = await Assert.ThrowsAsync<DownstreamServiceException>(
            () => svc.ApplySystemDefaultCriteriaAsync(org, org, camp.Id, Req(), default));

        Assert.Contains("BE", ex.Message);
        Assert.Contains("v4", ex.Message);

        using var check = tdb.NewContext();
        var rows = await check.CampaignCriteria.Where(c => c.CampaignId == camp.Id).ToListAsync();
        Assert.Equal("Giữ nguyên", Assert.Single(rows).Name);   // không ghi gì
    }

    // ── B4: hai đường ghi, hai nhãn KHÁC NHAU ───────────────────────────

    // Đường AI THẬT (suggester trả về tiêu chí) vẫn đóng nhãn `AiSuggested`. Cặp với
    // `CampaignServiceTests.Publish_tao_criteria_Sum1_va_audit` (đường dự phòng → `SystemDefault`):
    // nếu chỉ khoá một vế thì một thay đổi gộp cả hai đường về CÙNG một nhãn sẽ lọt, và lúc đó nhãn
    // hết phân biệt được "AI đã cân nhắc theo JD của bạn" với "ba hằng số giống nhau ở mọi chiến dịch".
    [Fact]
    public async Task DuongAiThat_VanDongNhan_AiSuggested()
    {
        using var tdb = new CampaignTestDb();
        var org = Guid.NewGuid();
        var camp = await SeedAsync(tdb, org);
        tdb.Db.CampaignQuestions.Add(new CampaignQuestion
        {
            Id = Guid.NewGuid(), CampaignId = camp.Id, OrgId = org, QuestionText = "Q1",
            Source = QuestionSource.CustomHr, IsRequired = true, CreatedAt = DateTime.UtcNow
        });
        await tdb.Db.SaveChangesAsync();

        var suggester = new Mock<ICriteriaSuggester>();
        suggester.Setup(x => x.SuggestAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<SuggestedCriterion>
            {
                new("Chuyên môn", "AI viết ra", 0.6m, 5),
                new("Giao tiếp", "AI viết ra", 0.4m, 5),
            });

        var svc = new CampaignSvc(tdb.NewContext(), Mock.Of<IFileService>(),
            Mock.Of<ILogger<CampaignSvc>>(), Mock.Of<IParserService>(), suggester.Object,
            Mock.Of<IInvitationEmailPublisher>(), config: Config());

        await svc.PublishCampaignAsync(org, org, camp.Id, default);

        using var check = tdb.NewContext();
        var rows = await check.CampaignCriteria.Where(c => c.CampaignId == camp.Id).ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal(CriterionSource.AiSuggested, r.Source));
    }

    // ── Hợp đồng: KHÔNG mang scoringScope ───────────────────────────────

    // Bịt bằng CẤU TRÚC (mẫu CAMP-15): không kiểu nào trên đường chép có chỗ chứa `scoringScope`, nên
    // "chép nhầm" là lỗi BIÊN DỊCH chứ không phải một cột lặng lẽ được thêm rồi không ai đọc.
    // Campaign không có cột tương ứng và đường chấm B2B không đọc field đó ⇒ mang về chỉ để lưu là
    // dựng một cột nói dối. (Cùng lý do với `id` — id của Interview vô nghĩa bên này.)
    [Fact]
    public void HopDong_KhongCoScoringScope_VaKhongCoId()
    {
        var cam = new[]
        {
            typeof(B2CRubricCriterion), typeof(B2CRubricResponse), typeof(B2CRubricLevel),
            typeof(CampaignCriterion), typeof(CriterionItem)
        };

        foreach (var t in cam)
        {
            Assert.DoesNotContain(t.GetProperties(),
                p => p.Name.Contains("ScoringScope", StringComparison.OrdinalIgnoreCase));
        }

        // `id` thì chỉ cấm ở phía NHẬN từ Interview — CampaignCriterion tất nhiên có Id của chính nó.
        Assert.DoesNotContain(typeof(B2CRubricCriterion).GetProperties(),
            p => p.Name.Equals("Id", StringComparison.OrdinalIgnoreCase));
    }
}
