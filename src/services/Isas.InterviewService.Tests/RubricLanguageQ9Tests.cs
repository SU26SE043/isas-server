using System.Security.Claims;
using Isas.InterviewService.ApplicationDbContext;
using Isas.InterviewService.Controllers;
using Isas.InterviewService.Data;
using Isas.InterviewService.DTOs;
using Isas.InterviewService.Enums;
using Isas.InterviewService.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace Isas.InterviewService.Tests;

/// <summary>
/// Q9 — rubric CÁ NHÂN B2C phải scope theo NGÔN NGỮ.
///
/// <para><b>Bug thật đã đo trên prod:</b> <c>GET /practice/rubrics/BE</c> trả <b>14 tiêu chí Σweight=2.0</b>
/// (gộp cả seed vi lẫn en), rồi <c>PUT</c> đúng payload đó → <b>400</b> "Tổng weight phải xấp xỉ 1 (hiện 2)".
/// API tự từ chối chính mình ⇒ ứng viên KHÔNG có đường nào khai rubric riêng.</para>
///
/// <para><b>Vì sao nhánh SEED quan trọng hơn nhánh CUSTOM:</b> toàn DB prod chỉ có 8 row rubric riêng,
/// còn seed là 7 tiêu chí × 3 nghề × 2 ngôn ngữ ⇒ gần như mọi ứng viên đi nhánh seed. Nên các test
/// dưới nạp seed THẬT (<see cref="B2CRubricSeed.Build"/>) chứ không dựng tiêu chí giả.</para>
///
/// <para><b>Hợp đồng additive:</b> client KHÔNG gửi <c>language</c> vẫn nhận đúng rubric <c>vi</c> —
/// đó là lời hứa "FE 0 thay đổi" của cả task và là thứ dễ vỡ nhất nếu ai đó đổi mặc định.</para>
/// </summary>
public class RubricLanguageQ9Tests
{
    private static RubricLibraryService Svc(InterviewDbContext db, bool bilingual = false)
        => new(db, new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Interview:Bilingual:Enabled"] = bilingual ? "true" : "false"
        }).Build());

    private static async Task SeedRealAsync(InterviewDbContext db)
    {
        db.RubricCriteria.AddRange(B2CRubricSeed.Build());
        await db.SaveChangesAsync();
    }

    // Số tiêu chí seed KỲ VỌNG lấy từ chính seed, không hardcode — thêm tiêu chí sau này (mẫu F11/F12)
    // không làm test này đỏ oan, nhưng vẫn bắt được lỗi gộp hai ngôn ngữ.
    private static int SeedCount(JobCategory cat, string language)
        => B2CRubricSeed.Build().Count(c => c.JobCategory == cat && c.Language == language && c.IsActive);

    private static UpsertRubricRequest TwoCriteria(string a = "My-A", string b = "My-B")
        => new([
            new RubricCriterionInput(a, "desc a", 0.6m, 5),
            new RubricCriterionInput(b, "desc b", 0.4m, 10)
        ]);

    // ── (1) Chính triệu chứng prod ────────────────────────────────────────────

    [Fact]
    public async Task GetEffective_KhongGuiLanguage_ChiTraSeedVi_SumWeightBang1()
    {
        using var t = new TestDb();
        await SeedRealAsync(t.Db);

        var got = await Svc(t.Db).GetEffectiveAsync(Guid.NewGuid(), JobCategory.BE);

        Assert.False(got.IsCustom);
        // Trước vá: 14 tiêu chí (7 vi + 7 en) và Σ = 2.0 → PUT lại chính payload này thì 400.
        Assert.Equal(SeedCount(JobCategory.BE, "vi"), got.Criteria.Count);
        Assert.InRange(got.Criteria.Sum(c => c.Weight), 0.99m, 1.01m);
        Assert.Contains(got.Criteria, c => c.Name == B2CRubricSeed.LanguageName);   // tên tiếng Việt
    }

    // 🔑 Đúng câu chuyện người dùng: "FE clone rồi sửa" — GET rồi PUT lại chính payload đó.
    // Trước vá đường này NÉM (Σ=2.0); sau vá phải trơn tru.
    [Fact]
    public async Task RoundTrip_GetRoiPutLaiChinhPayloadDo_KhongNem()
    {
        using var t = new TestDb();
        await SeedRealAsync(t.Db);
        var me = Guid.NewGuid();

        var template = await Svc(t.Db).GetEffectiveAsync(me, JobCategory.BE);
        var echoed = new UpsertRubricRequest(template.Criteria
            .Select(c => new RubricCriterionInput(c.Name, c.Description, c.Weight, c.MaxScore))
            .ToList());

        var saved = await Svc(t.Db).ReplaceAsync(me, JobCategory.BE, echoed);

        Assert.True(saved.IsCustom);
        Assert.Equal(template.Criteria.Count, saved.Criteria.Count);
    }

    [Fact]
    public async Task GetEffective_LanguageEn_ChiTraSeedEn()
    {
        using var t = new TestDb();
        await SeedRealAsync(t.Db);

        var got = await Svc(t.Db, bilingual: true).GetEffectiveAsync(Guid.NewGuid(), JobCategory.BE, "en");

        Assert.False(got.IsCustom);
        Assert.Equal(SeedCount(JobCategory.BE, "en"), got.Criteria.Count);
        Assert.InRange(got.Criteria.Sum(c => c.Weight), 0.99m, 1.01m);
        Assert.DoesNotContain(got.Criteria, c => c.Name == B2CRubricSeed.LanguageName);
    }

    // ── (2) Rubric riêng scope theo ngôn ngữ ──────────────────────────────────

    [Fact]
    public async Task Replace_KhongGuiLanguage_RowMangLanguageVi()
    {
        using var t = new TestDb();
        var me = Guid.NewGuid();

        await Svc(t.Db).ReplaceAsync(me, JobCategory.BE, TwoCriteria());

        var rows = await t.Db.RubricCriteria.AsNoTracking()
            .Where(c => c.CandidateId == me && c.IsActive).ToListAsync();
        Assert.NotEmpty(rows);
        Assert.All(rows, r => Assert.Equal("vi", r.Language));
    }

    [Fact]
    public async Task Replace_LanguageEn_RowMangLanguageEn_VaGetEnThayNo()
    {
        using var t = new TestDb();
        await SeedRealAsync(t.Db);
        var me = Guid.NewGuid();
        var svc = Svc(t.Db, bilingual: true);

        await svc.ReplaceAsync(me, JobCategory.BE, TwoCriteria("EN-A", "EN-B"), "en");

        var rows = await t.Db.RubricCriteria.AsNoTracking()
            .Where(c => c.CandidateId == me && c.IsActive).ToListAsync();
        Assert.All(rows, r => Assert.Equal("en", r.Language));

        var en = await svc.GetEffectiveAsync(me, JobCategory.BE, "en");
        Assert.True(en.IsCustom);
        Assert.Equal(["EN-A", "EN-B"], en.Criteria.Select(c => c.Name).OrderBy(x => x));

        // Cùng ứng viên, ngôn ngữ khác → vẫn là seed, KHÔNG rò rubric riêng EN sang VI.
        var vi = await svc.GetEffectiveAsync(me, JobCategory.BE, "vi");
        Assert.False(vi.IsCustom);
        Assert.Equal(SeedCount(JobCategory.BE, "vi"), vi.Criteria.Count);
    }

    // 🔑 Ứng viên khai rubric riêng cho CẢ HAI ngôn ngữ — đây là năng lực mà chính Q9 mở ra, và là ca
    // DUY NHẤT phân biệt được nhánh custom có lọc ngôn ngữ hay không: mọi ca khác đều được
    // B2CRubricScope.ResolveOwnerAsync (vốn đã lọc theo ngôn ngữ) che hộ. Thiếu vế lọc ở nhánh custom
    // thì người dùng này nhận 4 tiêu chí Σweight=2.0 — đúng nguyên con bug Q9, chỉ đổi chỗ.
    [Fact]
    public async Task GetEffective_CoRubricRiengCaHaiNgonNgu_MoiBenChiTraBoCuaMinh()
    {
        using var t = new TestDb();
        var me = Guid.NewGuid();
        var svc = Svc(t.Db, bilingual: true);
        await svc.ReplaceAsync(me, JobCategory.BE, TwoCriteria("VI-A", "VI-B"));
        await svc.ReplaceAsync(me, JobCategory.BE, TwoCriteria("EN-A", "EN-B"), "en");

        var vi = await svc.GetEffectiveAsync(me, JobCategory.BE);
        var en = await svc.GetEffectiveAsync(me, JobCategory.BE, "en");

        Assert.True(vi.IsCustom);
        Assert.True(en.IsCustom);
        Assert.Equal(["VI-A", "VI-B"], vi.Criteria.Select(c => c.Name).OrderBy(x => x));
        Assert.Equal(["EN-A", "EN-B"], en.Criteria.Select(c => c.Name).OrderBy(x => x));
        // Không trộn hai bộ: mỗi bên đúng 2 tiêu chí, Σweight ≈ 1 (không phải 4 và Σ=2.0).
        Assert.InRange(vi.Criteria.Sum(c => c.Weight), 0.99m, 1.01m);
        Assert.InRange(en.Criteria.Sum(c => c.Weight), 0.99m, 1.01m);
    }

    // 🔑 Bẫy nặng nhất: soft-versioning deactivate MỌI row active của (candidate, nghề).
    // Không scope thêm ngôn ngữ thì lưu rubric EN sẽ GIẾT rubric VI đang dùng — im lặng, không lỗi nào báo.
    [Fact]
    public async Task Replace_LuuRubricEn_KhongGietRubricVi()
    {
        using var t = new TestDb();
        var me = Guid.NewGuid();
        var svc = Svc(t.Db, bilingual: true);

        await svc.ReplaceAsync(me, JobCategory.BE, TwoCriteria("VI-A", "VI-B"));          // vi
        await svc.ReplaceAsync(me, JobCategory.BE, TwoCriteria("EN-A", "EN-B"), "en");    // en

        var activeVi = await t.Db.RubricCriteria.AsNoTracking()
            .Where(c => c.CandidateId == me && c.Language == "vi" && c.IsActive).ToListAsync();
        var activeEn = await t.Db.RubricCriteria.AsNoTracking()
            .Where(c => c.CandidateId == me && c.Language == "en" && c.IsActive).ToListAsync();

        Assert.Equal(2, activeVi.Count);   // rubric tiếng Việt SỐNG SÓT
        Assert.Equal(2, activeEn.Count);
        Assert.Equal(["VI-A", "VI-B"], activeVi.Select(c => c.Name).OrderBy(x => x));
    }

    [Fact]
    public async Task Reset_LanguageEn_KhongXoaRubricVi()
    {
        using var t = new TestDb();
        var me = Guid.NewGuid();
        var svc = Svc(t.Db, bilingual: true);
        await svc.ReplaceAsync(me, JobCategory.BE, TwoCriteria("VI-A", "VI-B"));
        await svc.ReplaceAsync(me, JobCategory.BE, TwoCriteria("EN-A", "EN-B"), "en");

        await svc.ResetAsync(me, JobCategory.BE, "en");

        Assert.Empty(await t.Db.RubricCriteria.AsNoTracking()
            .Where(c => c.CandidateId == me && c.Language == "en" && c.IsActive).ToListAsync());
        Assert.Equal(2, await t.Db.RubricCriteria.AsNoTracking()
            .CountAsync(c => c.CandidateId == me && c.Language == "vi" && c.IsActive));
    }

    // Phiên bản đánh số RIÊNG theo ngôn ngữ: rubric EN đầu tiên là v1, không phải v3 chỉ vì
    // ứng viên đã sửa rubric VI hai lần.
    [Fact]
    public async Task Version_DanhSoRiengTheoTungNgonNgu()
    {
        using var t = new TestDb();
        var me = Guid.NewGuid();
        var svc = Svc(t.Db, bilingual: true);

        await svc.ReplaceAsync(me, JobCategory.BE, TwoCriteria("v1-A", "v1-B"));
        await svc.ReplaceAsync(me, JobCategory.BE, TwoCriteria("v2-A", "v2-B"));
        await svc.ReplaceAsync(me, JobCategory.BE, TwoCriteria("EN-A", "EN-B"), "en");

        var vi = await t.Db.RubricCriteria.AsNoTracking()
            .Where(c => c.CandidateId == me && c.Language == "vi" && c.IsActive).ToListAsync();
        var en = await t.Db.RubricCriteria.AsNoTracking()
            .Where(c => c.CandidateId == me && c.Language == "en" && c.IsActive).ToListAsync();

        Assert.All(vi, c => Assert.Equal(2, c.Version));
        Assert.All(en, c => Assert.Equal(1, c.Version));
    }

    // ── (3) Validate ngôn ngữ ─────────────────────────────────────────────────

    [Fact]
    public async Task BilingualTat_LanguageEn_Nem400()
    {
        using var t = new TestDb();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Svc(t.Db).GetEffectiveAsync(Guid.NewGuid(), JobCategory.BE, "en"));
        Assert.Contains("Bilingual", ex.Message);
    }

    [Theory]
    [InlineData("VI")]      // hoa/thường không phân biệt
    [InlineData(" vi ")]    // trim
    public async Task Language_ChuanHoaHoaThuongVaKhoangTrang(string language)
    {
        using var t = new TestDb();
        await SeedRealAsync(t.Db);

        var got = await Svc(t.Db).GetEffectiveAsync(Guid.NewGuid(), JobCategory.BE, language);

        Assert.Equal(SeedCount(JobCategory.BE, "vi"), got.Criteria.Count);
    }

    // ── (4) Tầng controller — mã lỗi, và hợp đồng "FE 0 thay đổi" ─────────────

    private static RubricController Controller(IRubricLibraryService service, Guid? candidateId = null)
        => new(service, NullLogger<RubricController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        [new Claim(ClaimTypes.NameIdentifier, (candidateId ?? Guid.NewGuid()).ToString())], "test"))
                }
            }
        };

    // 🔑 Bẫy F2b: nếu ValidateLanguage ném ArgumentException, hoặc controller không bắt
    // InvalidOperationException ở Get/Delete, thì mọi `?language=` sai thành 500 chứ không phải 400
    // (Interview KHÔNG có exception handler toàn cục).
    [Fact]
    public async Task Controller_Get_LanguageKhongHopLe_Tra400()
    {
        using var t = new TestDb();
        var result = await Controller(Svc(t.Db)).Get(JobCategory.BE, "fr", default);
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Controller_Replace_LanguageKhongHopLe_Tra400()
    {
        using var t = new TestDb();
        var result = await Controller(Svc(t.Db)).Replace(JobCategory.BE, TwoCriteria(), "fr", default);
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Controller_Reset_LanguageKhongHopLe_Tra400()
    {
        using var t = new TestDb();
        var result = await Controller(Svc(t.Db)).Reset(JobCategory.BE, "fr", default);
        Assert.IsType<BadRequestObjectResult>(result);
    }

    // 🔑 Hợp đồng "FE 0 thay đổi": FE hiện KHÔNG gửi `language`. Khoá cả hai vế —
    // controller truyền null xuống service, và service dịch null thành rubric "vi".
    [Fact]
    public async Task Controller_KhongGuiLanguage_TruyenNullXuongService()
    {
        var service = new Mock<IRubricLibraryService>();
        var me = Guid.NewGuid();
        service.Setup(s => s.GetEffectiveAsync(me, JobCategory.BE, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RubricResponse(JobCategory.BE, false, []));

        var result = await Controller(service.Object, me).Get(JobCategory.BE, language: null, default);

        Assert.IsType<OkObjectResult>(result);
        service.Verify(
            s => s.GetEffectiveAsync(me, JobCategory.BE, null, It.IsAny<CancellationToken>()), Times.Once);
        service.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Controller_KhongGuiLanguage_NhanDungRubricVi_XuyenSuot()
    {
        using var t = new TestDb();
        await SeedRealAsync(t.Db);

        var result = await Controller(Svc(t.Db)).Get(JobCategory.BE, language: null, default);

        var ok = Assert.IsType<OkObjectResult>(result);
        var body = Assert.IsType<RubricResponse>(ok.Value);
        Assert.Equal(SeedCount(JobCategory.BE, "vi"), body.Criteria.Count);
        Assert.Contains(body.Criteria, c => c.Name == B2CRubricSeed.LanguageName);
    }
}
