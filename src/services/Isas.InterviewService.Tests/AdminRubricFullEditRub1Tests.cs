using Isas.InterviewService.Data;
using Isas.InterviewService.DTOs;
using Isas.InterviewService.Enums;
using Isas.InterviewService.Services;
using Microsoft.EntityFrameworkCore;

namespace Isas.InterviewService.Tests;

/// <summary>
/// RUB1 · hợp đồng A — admin sửa ĐỦ bộ chuẩn B2C: thêm/xoá/đổi tên/trọng số/phạm vi, KHÔNG tự chuẩn hoá
/// Σweight, tiêu chí đo-bằng-số-đo (F11) chỉ được giữ/bỏ/sửa mô tả-trọng số-mốc.
///
/// <para>Vì sao từng vế phải khoá riêng: B2C buổi mới tính điểm CÓ TRỌNG SỐ (INT-10) ⇒ một trọng số sai
/// không làm lỗi nào nổ, nó chỉ làm điểm của mọi người luyện lệch đi. Và vân tay thiếu một trường là
/// sửa trường đó KHÔNG bump phiên bản ⇒ buổi đang ghim bản cũ bị chấm bằng thước mới.</para>
/// </summary>
public class AdminRubricFullEditRub1Tests
{
    private static AdminB2CRubricService Service(TestDb t) => new(t.Db);

    private static async Task<AdminRubricResponse> SeedAndGetAsync(TestDb t, AdminB2CRubricService svc)
    {
        t.Db.RubricCriteria.AddRange(B2CRubricSeed.Build());
        await t.Db.SaveChangesAsync();
        return (await svc.GetAsync(JobCategory.BE, "vi"))!;
    }

    /// <summary>Echo NGUYÊN trạng (name/weight/scope = null ⇒ giữ nguyên), cho phép sửa từng tiêu chí.</summary>
    private static List<AdminRubricCriterionInput> Echo(
        AdminRubricResponse v, Func<AdminRubricCriterionItem, AdminRubricCriterionInput, AdminRubricCriterionInput?>? edit = null)
        => v.Criteria
            .Select(c =>
            {
                var input = new AdminRubricCriterionInput(
                    c.Id, c.Description,
                    c.Levels.Select(l => new AdminRubricLevelInput(l.Score, l.Descriptor)).ToList());
                return edit is null ? input : edit(c, input);
            })
            .Where(i => i is not null)
            .Select(i => i!)
            .ToList();

    private static List<AdminRubricLevelInput> Levels5() =>
    [
        new(0, "Không nêu được ý nào liên quan tới câu hỏi, hoặc bỏ trống."),
        new(3, "Nêu được ý chính nhưng thiếu ví dụ cụ thể và chưa nói tới đánh đổi."),
        new(5, "Nêu ý chính, có ví dụ từ dự án thật và chỉ ra được đánh đổi của phương án.")
    ];

    private static AdminRubricCriterionItem Delivery(AdminRubricResponse v)
        => v.Criteria.Single(c => c.ScoringMethod == nameof(CriterionScoringMethod.DeliveryMetrics));

    private static AdminRubricCriterionItem HeaviestTargeted(AdminRubricResponse v)
        => v.Criteria.Where(c => c.ScoringScope == nameof(ScoringScope.WhenTargeted))
            .OrderByDescending(c => c.Weight).ThenBy(c => c.Name, StringComparer.Ordinal).First();

    private static async Task<int> ActiveVersionAsync(TestDb t)
        => await t.Db.RubricCriteria.AsNoTracking()
            .Where(c => c.CampaignId == null && c.CandidateId == null && c.JobCategory == JobCategory.BE
                        && c.Language == "vi" && c.IsActive)
            .Select(c => c.Version).Distinct().SingleAsync();

    // ── Thêm tiêu chí ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ThemTieuChi_BumpVersion_TieuChiMoiLaAiThang5()
    {
        using var t = new TestDb();
        var svc = Service(t);
        var v1 = await SeedAndGetAsync(t, svc);
        var heaviest = HeaviestTargeted(v1);

        var body = Echo(v1, (c, i) => c.Id == heaviest.Id ? i with { Weight = c.Weight - 0.05m } : i);
        body.Add(new AdminRubricCriterionInput(
            null, "Bảo mật ứng dụng web.", Levels5(),
            Name: "  Bảo mật  ", Weight: 0.05m, ScoringScope: "WhenTargeted"));

        var v2 = (await svc.ReplaceAsync(JobCategory.BE, new(body), "vi"))!;

        Assert.True(v2.Changed);
        Assert.Equal(v1.Version + 1, v2.Version);
        Assert.Equal(v1.Criteria.Count + 1, v2.Criteria.Count);
        var added = v2.Criteria.Single(c => c.Name == "Bảo mật");   // đã trim
        Assert.Equal(nameof(CriterionScoringMethod.Ai), added.ScoringMethod);
        Assert.Equal(5, added.MaxScore);
        Assert.Equal(nameof(ScoringScope.WhenTargeted), added.ScoringScope);
        Assert.Equal(0.05m, added.Weight);
        Assert.Equal(3, added.Levels.Count);
        Assert.Equal(heaviest.Weight - 0.05m, v2.Criteria.Single(c => c.Name == heaviest.Name).Weight);
    }

    [Fact]
    public async Task ThemTieuChi_ThieuTen_ThieuTrongSo_ThieuPhamVi_DeuLa400()
    {
        using var t = new TestDb();
        var svc = Service(t);
        var v1 = await SeedAndGetAsync(t, svc);

        AdminRubricCriterionInput[] bad =
        [
            new(null, null, null, Name: null, Weight: 0.01m, ScoringScope: "Always"),
            new(null, null, null, Name: "Mới", Weight: null, ScoringScope: "Always"),
            new(null, null, null, Name: "Mới", Weight: 0.01m, ScoringScope: null),
        ];
        foreach (var item in bad)
        {
            var body = Echo(v1);
            body.Add(item);
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => svc.ReplaceAsync(JobCategory.BE, new(body), "vi"));
        }
        Assert.Equal(v1.Version, await ActiveVersionAsync(t));
    }

    // ── Trọng số / phạm vi / tên — mỗi thứ PHẢI bump phiên bản ─────────────────────────────

    /// <summary>
    /// Chỉ đổi trọng số (Σ giữ = 1) ⇒ bump. Vân tay bỏ trọng số thì lần Lưu này trả <c>changed = false</c>
    /// và KHÔNG ghi gì — admin tin đã đổi thước đo mà điểm vẫn tính bằng trọng số cũ.
    /// </summary>
    [Fact]
    public async Task ChiDoiTrongSo_BumpVersion_VaLuuDungGiaTri()
    {
        using var t = new TestDb();
        var svc = Service(t);
        var v1 = await SeedAndGetAsync(t, svc);
        var heaviest = HeaviestTargeted(v1);
        var delivery = Delivery(v1);

        var body = Echo(v1, (c, i) =>
            c.Id == heaviest.Id ? i with { Weight = c.Weight - 0.02m }
            : c.Id == delivery.Id ? i with { Weight = c.Weight + 0.02m }
            : i);

        var v2 = (await svc.ReplaceAsync(JobCategory.BE, new(body), "vi"))!;

        Assert.True(v2.Changed);
        Assert.Equal(v1.Version + 1, v2.Version);
        Assert.Equal(heaviest.Weight - 0.02m, v2.Criteria.Single(c => c.Name == heaviest.Name).Weight);
        Assert.Equal(delivery.Weight + 0.02m, v2.Criteria.Single(c => c.Name == delivery.Name).Weight);
    }

    /// <summary>Chỉ đổi phạm vi Always → WhenTargeted ⇒ bump (đổi mẫu số điểm, INT-18).</summary>
    [Fact]
    public async Task ChiDoiPhamVi_BumpVersion()
    {
        using var t = new TestDb();
        var svc = Service(t);
        var v1 = await SeedAndGetAsync(t, svc);
        var target = v1.Criteria.First(c => c.ScoringScope == nameof(ScoringScope.Always)
                                            && c.ScoringMethod == nameof(CriterionScoringMethod.Ai));

        var body = Echo(v1, (c, i) => c.Id == target.Id ? i with { ScoringScope = "WhenTargeted" } : i);
        var v2 = (await svc.ReplaceAsync(JobCategory.BE, new(body), "vi"))!;

        Assert.True(v2.Changed);
        Assert.Equal(nameof(ScoringScope.WhenTargeted), v2.Criteria.Single(c => c.Name == target.Name).ScoringScope);
    }

    [Fact]
    public async Task DoiTen_BumpVersion_GiuNguonDiem()
    {
        using var t = new TestDb();
        var svc = Service(t);
        var v1 = await SeedAndGetAsync(t, svc);
        var target = HeaviestTargeted(v1);

        var body = Echo(v1, (c, i) => c.Id == target.Id ? i with { Name = "Kỹ thuật cốt lõi" } : i);
        var v2 = (await svc.ReplaceAsync(JobCategory.BE, new(body), "vi"))!;

        Assert.True(v2.Changed);
        Assert.DoesNotContain(v2.Criteria, c => c.Name == target.Name);
        var renamed = v2.Criteria.Single(c => c.Name == "Kỹ thuật cốt lõi");
        Assert.Equal(target.Weight, renamed.Weight);
        Assert.Equal(target.ScoringScope, renamed.ScoringScope);
        Assert.Equal(target.ScoringMethod, renamed.ScoringMethod);
    }

    /// <summary>
    /// <c>name: null</c> = GIỮ NGUYÊN, không phải "ghi đè thành rỗng". Sửa mô tả một tiêu chí (để có lý
    /// do bump) trong khi mọi tiêu chí gửi <c>name: null</c> ⇒ bộ mới mang đúng tập tên cũ.
    /// </summary>
    [Fact]
    public async Task TenNull_GiuNguyenTen()
    {
        using var t = new TestDb();
        var svc = Service(t);
        var v1 = await SeedAndGetAsync(t, svc);
        var first = v1.Criteria[0];

        var body = Echo(v1, (c, i) => c.Id == first.Id ? i with { Description = "Mô tả mới hoàn toàn." } : i);
        Assert.All(body, i => Assert.Null(i.Name));

        var v2 = (await svc.ReplaceAsync(JobCategory.BE, new(body), "vi"))!;

        Assert.True(v2.Changed);
        Assert.Equal(v1.Criteria.Select(c => c.Name).OrderBy(n => n, StringComparer.Ordinal),
                     v2.Criteria.Select(c => c.Name).OrderBy(n => n, StringComparer.Ordinal));
    }

    // ── Xoá ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task XoaTieuChi_CanLaiTrongSo_PhienBanMoiKhongCoTieuChiDo_BanCuVanCon()
    {
        using var t = new TestDb();
        var svc = Service(t);
        var v1 = await SeedAndGetAsync(t, svc);
        var removed = v1.Criteria.Where(c => c.ScoringScope == nameof(ScoringScope.WhenTargeted))
            .OrderBy(c => c.Weight).ThenBy(c => c.Name, StringComparer.Ordinal).First();
        var heaviest = HeaviestTargeted(v1);

        var body = Echo(v1, (c, i) =>
            c.Id == removed.Id ? null
            : c.Id == heaviest.Id ? i with { Weight = c.Weight + removed.Weight }
            : i);

        var v2 = (await svc.ReplaceAsync(JobCategory.BE, new(body), "vi"))!;

        Assert.Equal(v1.Criteria.Count - 1, v2.Criteria.Count);
        Assert.DoesNotContain(v2.Criteria, c => c.Name == removed.Name);
        // Bản cũ vẫn nằm đó (append-only) để chấm nốt buổi đã ghim v1.
        Assert.True(await t.Db.RubricCriteria.AnyAsync(c => c.Id == removed.Id && !c.IsActive));
    }

    // ── Tiêu chí đo bằng số đo giọng nói (F11) ──────────────────────────────────────────────

    [Fact]
    public async Task TieuChiDo_DuocBo_KhoiBody()
    {
        using var t = new TestDb();
        var svc = Service(t);
        var v1 = await SeedAndGetAsync(t, svc);
        var delivery = Delivery(v1);
        var heaviest = HeaviestTargeted(v1);

        var body = Echo(v1, (c, i) =>
            c.Id == delivery.Id ? null
            : c.Id == heaviest.Id ? i with { Weight = c.Weight + delivery.Weight }
            : i);

        var v2 = (await svc.ReplaceAsync(JobCategory.BE, new(body), "vi"))!;
        Assert.DoesNotContain(v2.Criteria, c => c.ScoringMethod == nameof(CriterionScoringMethod.DeliveryMetrics));
    }

    [Fact]
    public async Task TieuChiDo_DoiTen_Hoac_DoiPhamVi_400()
    {
        using var t = new TestDb();
        var svc = Service(t);
        var v1 = await SeedAndGetAsync(t, svc);
        var delivery = Delivery(v1);

        var rename = Echo(v1, (c, i) => c.Id == delivery.Id ? i with { Name = "Nói trôi chảy" } : i);
        var ex1 = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.ReplaceAsync(JobCategory.BE, new(rename), "vi"));
        Assert.Contains("không được đổi tên", ex1.Message);

        var rescope = Echo(v1, (c, i) => c.Id == delivery.Id ? i with { ScoringScope = "WhenTargeted" } : i);
        var ex2 = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.ReplaceAsync(JobCategory.BE, new(rescope), "vi"));
        Assert.Contains("phạm vi", ex2.Message);

        // Gửi lại ĐÚNG tên/phạm vi đang có (không đổi) ⇒ không bị coi là đổi.
        var same = Echo(v1, (c, i) => c.Id == delivery.Id
            ? i with { Name = c.Name, ScoringScope = c.ScoringScope } : i);
        var v2 = (await svc.ReplaceAsync(JobCategory.BE, new(same), "vi"))!;
        Assert.False(v2.Changed);
    }

    /// <summary>
    /// Sửa mô tả/trọng số/mốc của tiêu chí đo ⇒ bản mới VẪN <c>DeliveryMetrics</c>. Mất nguồn điểm ở
    /// bước chép bản mới là đúng lỗi prod 14/09: "Độ trôi chảy" chuyển sang cho LLM chấm, im lặng.
    /// </summary>
    [Fact]
    public async Task TieuChiDo_SuaMoTaTrongSoMoc_GiuNguonDiem()
    {
        using var t = new TestDb();
        var svc = Service(t);
        var v1 = await SeedAndGetAsync(t, svc);
        var delivery = Delivery(v1);
        var heaviest = HeaviestTargeted(v1);

        var body = Echo(v1, (c, i) =>
            c.Id == delivery.Id ? i with { Description = "Đo từ giọng nói.", Weight = c.Weight + 0.01m, Levels = Levels5() }
            : c.Id == heaviest.Id ? i with { Weight = c.Weight - 0.01m }
            : i);

        var v2 = (await svc.ReplaceAsync(JobCategory.BE, new(body), "vi"))!;

        var after = v2.Criteria.Single(c => c.Name == delivery.Name);
        Assert.Equal(nameof(CriterionScoringMethod.DeliveryMetrics), after.ScoringMethod);
        Assert.Equal(delivery.Weight + 0.01m, after.Weight);
        Assert.Equal(3, after.Levels.Count);
        // Mọi tiêu chí khác cũng giữ nguồn điểm của chúng.
        foreach (var before in v1.Criteria.Where(c => c.Id != delivery.Id))
            Assert.Equal(before.ScoringMethod, v2.Criteria.Single(c => c.Name == before.Name).ScoringMethod);
    }

    [Fact]
    public async Task KhongConTieuChiAi_400()
    {
        using var t = new TestDb();
        var svc = Service(t);
        var v1 = await SeedAndGetAsync(t, svc);
        var delivery = Delivery(v1);

        var body = Echo(v1, (c, i) => c.Id == delivery.Id ? i with { Weight = 1m } : null);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.ReplaceAsync(JobCategory.BE, new(body), "vi"));
        Assert.Contains("AI chấm", ex.Message);
    }

    // ── Tên ──────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("   ")]
    [InlineData("")]
    public async Task TenRong_400(string name)
    {
        using var t = new TestDb();
        var svc = Service(t);
        var v1 = await SeedAndGetAsync(t, svc);
        var body = Echo(v1, (c, i) => c.Id == v1.Criteria[0].Id ? i with { Name = name } : i);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.ReplaceAsync(JobCategory.BE, new(body), "vi"));
        Assert.Contains("rỗng", ex.Message);
    }

    [Fact]
    public async Task TenQua100KyTu_400_Dung100KyTu_Qua()
    {
        using var t = new TestDb();
        var svc = Service(t);
        var v1 = await SeedAndGetAsync(t, svc);
        var target = HeaviestTargeted(v1);

        var tooLong = Echo(v1, (c, i) => c.Id == target.Id ? i with { Name = new string('a', 101) } : i);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.ReplaceAsync(JobCategory.BE, new(tooLong), "vi"));
        Assert.Contains("tối đa 100", ex.Message);

        var exact = Echo(v1, (c, i) => c.Id == target.Id ? i with { Name = new string('a', 100) } : i);
        var v2 = (await svc.ReplaceAsync(JobCategory.BE, new(exact), "vi"))!;
        Assert.Contains(v2.Criteria, c => c.Name.Length == 100);
    }

    /// <summary>Trùng tên sau trim, không phân biệt hoa thường ⇒ 400 (chặt hơn unique index DB).</summary>
    [Fact]
    public async Task TenTrung_KhongPhanBietHoaThuong_400()
    {
        using var t = new TestDb();
        var svc = Service(t);
        var v1 = await SeedAndGetAsync(t, svc);
        var a = HeaviestTargeted(v1);
        var b = v1.Criteria.First(c => c.Id != a.Id && c.ScoringMethod == nameof(CriterionScoringMethod.Ai));

        var body = Echo(v1, (c, i) => c.Id == b.Id ? i with { Name = "  " + a.Name.ToUpperInvariant() + " " } : i);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.ReplaceAsync(JobCategory.BE, new(body), "vi"));
        Assert.Contains("trùng", ex.Message);
    }

    // ── Trọng số ─────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("0")]
    [InlineData("-0.1")]
    [InlineData("0.00004")]   // làm tròn 4 chữ số ra 0 ⇒ nổ CHECK weight > 0 nếu không chặn ⇒ 500
    public async Task TrongSoKhongDuong_400(string raw)
    {
        using var t = new TestDb();
        var svc = Service(t);
        var v1 = await SeedAndGetAsync(t, svc);
        var body = Echo(v1);
        body.Add(new AdminRubricCriterionInput(null, null, null, Name: "Mới", Weight: decimal.Parse(raw,
            System.Globalization.CultureInfo.InvariantCulture), ScoringScope: "Always"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.ReplaceAsync(JobCategory.BE, new(body), "vi"));
        Assert.Contains("Trọng số", ex.Message);
    }

    /// <summary>
    /// Σ lệch 1 quá 0.0001 ⇒ 400 và KHÔNG tự chuẩn hoá. Tổng 1.0002 bị từ chối; tổng 1.0001 (đúng biên)
    /// thì nhận — không chuẩn hoá nên giá trị lưu đúng như admin gửi.
    /// </summary>
    [Fact]
    public async Task TongTrongSo_LechQua0_0001_400_DungBien_Qua_KhongTuChuanHoa()
    {
        using var t = new TestDb();
        var svc = Service(t);
        var v1 = await SeedAndGetAsync(t, svc);
        var heaviest = HeaviestTargeted(v1);

        var over = Echo(v1, (c, i) => c.Id == heaviest.Id ? i with { Weight = c.Weight + 0.0002m } : i);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.ReplaceAsync(JobCategory.BE, new(over), "vi"));
        Assert.Contains("Tổng trọng số", ex.Message);
        Assert.Equal(v1.Version, await ActiveVersionAsync(t));

        var edge = Echo(v1, (c, i) => c.Id == heaviest.Id ? i with { Weight = c.Weight + 0.0001m } : i);
        var v2 = (await svc.ReplaceAsync(JobCategory.BE, new(edge), "vi"))!;
        Assert.Equal(heaviest.Weight + 0.0001m, v2.Criteria.Single(c => c.Name == heaviest.Name).Weight);
    }

    /// <summary>
    /// Σ kiểm trên giá trị ĐÃ làm tròn 4 chữ số: 7 × (1/7) thô ≈ 1, nhưng lưu ra 7 × 0.1429 = 1.0003 ⇒ 400.
    /// </summary>
    [Fact]
    public async Task TongTrongSo_KiemSauKhiLamTron4ChuSo()
    {
        using var t = new TestDb();
        var svc = Service(t);
        var v1 = await SeedAndGetAsync(t, svc);
        var seventh = 1m / 7m;

        var body = Echo(v1, (_, i) => i with { Weight = seventh });
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.ReplaceAsync(JobCategory.BE, new(body), "vi"));
    }

    // ── Phạm vi / id / mốc ─────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("0")]
    [InlineData("Sometimes")]
    public async Task PhamViLa_400(string scope)
    {
        using var t = new TestDb();
        var svc = Service(t);
        var v1 = await SeedAndGetAsync(t, svc);
        var body = Echo(v1, (c, i) => c.Id == HeaviestTargeted(v1).Id ? i with { ScoringScope = scope } : i);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.ReplaceAsync(JobCategory.BE, new(body), "vi"));
        Assert.Contains("Always hoặc WhenTargeted", ex.Message);
    }

    [Fact]
    public async Task IdGuiTrung_400()
    {
        using var t = new TestDb();
        var svc = Service(t);
        var v1 = await SeedAndGetAsync(t, svc);
        var body = Echo(v1);
        body.Add(body[0]);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.ReplaceAsync(JobCategory.BE, new(body), "vi"));
        Assert.Contains("gửi trùng", ex.Message);
    }

    /// <summary>
    /// Id của bộ chuẩn NGÔN NGỮ KHÁC (có thật trong DB) ⇒ 400. Nhận nó là đem tiêu chí tiếng Anh nhét vào
    /// bộ tiếng Việt — hoặc tệ hơn, coi nó như "tiêu chí có sẵn" rồi chép nguồn điểm của nó sang.
    /// </summary>
    [Fact]
    public async Task IdCuaBoKhac_400()
    {
        using var t = new TestDb();
        var svc = Service(t);
        var v1 = await SeedAndGetAsync(t, svc);
        var en = (await svc.GetAsync(JobCategory.BE, "en"))!;

        var body = Echo(v1, (c, i) => c.Id == v1.Criteria[0].Id ? i with { Id = en.Criteria[0].Id } : i);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.ReplaceAsync(JobCategory.BE, new(body), "vi"));
        Assert.Contains("không thuộc bộ chuẩn", ex.Message);
    }

    [Fact]
    public async Task MocSaiLuat_TieuChiMoi_400()
    {
        using var t = new TestDb();
        var svc = Service(t);
        var v1 = await SeedAndGetAsync(t, svc);
        var heaviest = HeaviestTargeted(v1);

        var body = Echo(v1, (c, i) => c.Id == heaviest.Id ? i with { Weight = c.Weight - 0.05m } : i);
        body.Add(new AdminRubricCriterionInput(
            null, null,
            [new(3, "Nêu được ý chính nhưng thiếu ví dụ cụ thể."), new(5, "Nêu ý chính, có ví dụ từ dự án thật.")],
            Name: "Mới", Weight: 0.05m, ScoringScope: "Always"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.ReplaceAsync(JobCategory.BE, new(body), "vi"));
        Assert.Contains("mốc 0", ex.Message);
    }

    // ── Không đổi gì ⇒ KHÔNG bump, kể cả khi gửi tường minh giá trị đang có ─────────────────

    [Fact]
    public async Task GuiTuongMinhGiaTriDangCo_KhongBump()
    {
        using var t = new TestDb();
        var svc = Service(t);
        var v1 = await SeedAndGetAsync(t, svc);

        var body = Echo(v1, (c, i) => i with { Name = c.Name, Weight = c.Weight, ScoringScope = c.ScoringScope });
        var v2 = (await svc.ReplaceAsync(JobCategory.BE, new(body), "vi"))!;

        Assert.False(v2.Changed);
        Assert.Equal(v1.Version, v2.Version);
    }
}
