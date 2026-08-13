using Isas.InterviewService.ApplicationDbContext;
using Isas.InterviewService.Data;
using Isas.InterviewService.DTOs;
using Isas.InterviewService.Entities;
using Isas.InterviewService.Enums;
using Isas.InterviewService.Services;
using Microsoft.EntityFrameworkCore;

namespace Isas.InterviewService.Tests;

/// <summary>
/// Admin quản BỘ CHUẨN rubric B2C — thước đo áp cho nhóm người dùng đông nhất và cho tới nay chưa
/// từng có một dòng mốc điểm nào.
/// </summary>
public class AdminB2CRubricTests
{
    private static AdminB2CRubricService Service(TestDb t) => new(t.Db);

    private static void SeedDefaults(InterviewDbContext db)
    {
        db.RubricCriteria.AddRange(B2CRubricSeed.Build());
        db.SaveChanges();
    }

    /// <summary>Ba mốc hợp lệ cho thang 5 (đủ mốc 0 và mốc maxScore, mô tả ≥ 20 ký tự).</summary>
    private static List<AdminRubricLevelInput> ValidLevels() =>
    [
        new(0, "Không nêu được ý nào liên quan tới câu hỏi, hoặc bỏ trống."),
        new(3, "Nêu được ý chính nhưng thiếu ví dụ cụ thể và chưa nói tới đánh đổi."),
        new(5, "Nêu ý chính, có ví dụ từ dự án thật và chỉ ra được đánh đổi của phương án.")
    ];

    private static UpsertAdminRubricRequest Echo(
        AdminRubricResponse current, Func<AdminRubricCriterionItem, List<AdminRubricLevelInput>?>? levels = null,
        Func<AdminRubricCriterionItem, string?>? description = null)
        => new(current.Criteria.Select(c => new AdminRubricCriterionInput(
            c.Id,
            description is null ? c.Description : description(c),
            levels is null
                ? c.Levels.Select(l => new AdminRubricLevelInput(l.Score, l.Descriptor)).ToList()
                : levels(c))).ToList());

    // ── (1) Không đổi gì ⇒ KHÔNG bump ────────────────────────────────────────────────────────

    /// <summary>
    /// Gửi lại đúng nội dung vừa nhận về ⇒ <c>changed = false</c>, không sinh phiên bản mới.
    ///
    /// Bump khi không ai sửa gì làm nhãn phiên bản mất nghĩa (mỗi lần bấm Lưu là một số mới) và cắt
    /// vụn quota chấm thử vốn tính theo (nghề, ngôn ngữ, phiên bản).
    /// </summary>
    [Fact]
    public async Task Replace_SamePayload_DoesNotBumpVersion()
    {
        using var t = new TestDb();
        SeedDefaults(t.Db);
        var svc = Service(t);
        var before = (await svc.GetAsync(JobCategory.BE, "vi"))!;

        var result = (await svc.ReplaceAsync(JobCategory.BE, Echo(before), "vi"))!;

        Assert.False(result.Changed);
        Assert.Equal(before.Version, result.Version);
        Assert.Equal(
            before.Criteria.Select(c => c.Id).OrderBy(x => x),
            result.Criteria.Select(c => c.Id).OrderBy(x => x));

        var versions = await t.Db.RubricCriteria.AsNoTracking()
            .Where(c => c.CampaignId == null && c.CandidateId == null
                        && c.JobCategory == JobCategory.BE && c.Language == "vi")
            .Select(c => c.Version).Distinct().ToListAsync();
        Assert.Single(versions);
    }

    // ── (2) Đổi mốc ⇒ bump, và bộ CŨ vẫn đọc được theo phiên bản ────────────────────────────

    /// <summary>
    /// Khai mốc ⇒ phiên bản mới active, bộ cũ bị hạ cờ NHƯNG vẫn nạp được qua loader theo con dấu —
    /// đó là điều kiện để buổi đang dở chấm nốt bằng thước cũ (PAY-13).
    /// </summary>
    [Fact]
    public async Task Replace_WithLevels_BumpsVersion_AndOldVersionStillLoadable()
    {
        using var t = new TestDb();
        SeedDefaults(t.Db);
        var svc = Service(t);
        var v1 = (await svc.GetAsync(JobCategory.BE, "vi"))!;
        Assert.All(v1.Criteria, c => Assert.Empty(c.Levels));   // bộ chuẩn hôm nay: KHÔNG có mốc nào

        var v2 = (await svc.ReplaceAsync(JobCategory.BE, Echo(v1, levels: _ => ValidLevels()), "vi"))!;

        Assert.True(v2.Changed);
        Assert.Equal(v1.Version + 1, v2.Version);
        Assert.Equal(7, v2.Criteria.Count);
        Assert.All(v2.Criteria, c => Assert.Equal(3, c.Levels.Count));
        // Id MỚI (append-only) — điểm cũ vẫn trỏ vào id v1 nhờ FK Restrict.
        Assert.Empty(v2.Criteria.Select(c => c.Id).Intersect(v1.Criteria.Select(c => c.Id)));

        // Bộ v1 đã hạ cờ mà vẫn nạp đủ qua con dấu.
        var pinnedV1 = await RubricCriteriaLoader.LoadAsync(t.Db,
            new RubricScopeKey(null, Guid.NewGuid(), JobCategory.BE, "vi", B2CRubricVersion: v1.Version));
        Assert.Equal(7, pinnedV1.Count);
        Assert.All(pinnedV1, c => Assert.False(c.IsActive));

        // Bộ v2 mang mốc thật xuống tới đường chấm.
        var pinnedV2 = await RubricCriteriaLoader.LoadAsync(t.Db,
            new RubricScopeKey(null, Guid.NewGuid(), JobCategory.BE, "vi", B2CRubricVersion: v2.Version));
        Assert.All(pinnedV2, c => Assert.Equal(3, c.Levels.Count));
    }

    /// <summary>Chỉ sửa MÔ TẢ (không đụng mốc) cũng là đổi thước đo ⇒ vẫn bump.</summary>
    [Fact]
    public async Task Replace_DescriptionOnly_BumpsVersion()
    {
        using var t = new TestDb();
        SeedDefaults(t.Db);
        var svc = Service(t);
        var v1 = (await svc.GetAsync(JobCategory.FE, "vi"))!;

        var v2 = (await svc.ReplaceAsync(
            JobCategory.FE, Echo(v1, description: c => c.Description + " (làm rõ)"), "vi"))!;

        Assert.True(v2.Changed);
        Assert.Equal(v1.Version + 1, v2.Version);
    }

    /// <summary>Lưu bộ tiếng Việt KHÔNG được đụng bộ tiếng Anh của cùng nghề.</summary>
    [Fact]
    public async Task Replace_OneLanguage_LeavesOtherLanguageUntouched()
    {
        using var t = new TestDb();
        SeedDefaults(t.Db);
        var svc = Service(t);
        var viV1 = (await svc.GetAsync(JobCategory.BE, "vi"))!;
        var enBefore = (await svc.GetAsync(JobCategory.BE, "en"))!;

        await svc.ReplaceAsync(JobCategory.BE, Echo(viV1, levels: _ => ValidLevels()), "vi");

        var enAfter = (await svc.GetAsync(JobCategory.BE, "en"))!;
        Assert.Equal(enBefore.Version, enAfter.Version);
        Assert.Equal(enBefore.Criteria.Select(c => c.Id).OrderBy(x => x),
                     enAfter.Criteria.Select(c => c.Id).OrderBy(x => x));
    }

    // ── (3) Biên độ quyền — bốn trường ngoài tầm với ─────────────────────────────────────────

    /// <summary>
    /// Bốn trường <c>Name</c>/<c>Weight</c>/<c>MaxScore</c>/<c>ScoringScope</c> KHÔNG được có mặt trên
    /// DTO admin. Bịt bằng cấu trúc (gán nhầm = lỗi biên dịch) chỉ đứng vững chừng nào không ai "tiện
    /// tay" thêm chúng vào — test này là thứ làm việc thêm đó chuyển thành ĐỎ.
    ///
    /// <para>Hậu quả nếu thêm <c>Name</c>: BC12/BC15/F14 gom nhóm theo TÊN ⇒ đổi tên cắt đôi chuỗi
    /// thời gian của mọi người dùng, im lặng.</para>
    /// </summary>
    [Theory]
    [InlineData("Name")]
    [InlineData("Weight")]
    [InlineData("MaxScore")]
    [InlineData("ScoringScope")]
    public void AdminInputDto_DoesNotExposeProtectedFields(string property)
        => Assert.Null(typeof(AdminRubricCriterionInput).GetProperty(property));

    /// <summary>Bốn trường đó phải được chép NGUYÊN từ bản cũ sang phiên bản mới.</summary>
    [Fact]
    public async Task Replace_CarriesOverNameWeightMaxScoreAndScope()
    {
        using var t = new TestDb();
        SeedDefaults(t.Db);
        var svc = Service(t);
        var v1 = (await svc.GetAsync(JobCategory.BA, "vi"))!;

        var v2 = (await svc.ReplaceAsync(JobCategory.BA, Echo(v1, levels: _ => ValidLevels()), "vi"))!;

        foreach (var before in v1.Criteria)
        {
            var after = v2.Criteria.Single(c => c.Name == before.Name);
            Assert.Equal(before.Weight, after.Weight);
            Assert.Equal(before.MaxScore, after.MaxScore);
            Assert.Equal(before.ScoringScope, after.ScoringScope);
        }
        // Bất biến 4 Always / 3 WhenTargeted mỗi nghề (INT-18) sống sót qua một vòng Lưu.
        Assert.Equal(4, v2.Criteria.Count(c => c.ScoringScope == nameof(ScoringScope.Always)));
        Assert.Equal(3, v2.Criteria.Count(c => c.ScoringScope == nameof(ScoringScope.WhenTargeted)));
    }

    // ── (4) Luật thang điểm — dùng chung CriterionLevelRules, không viết luật thứ hai ────────

    /// <summary>
    /// Thang thiếu mốc 0 → 400 (không phải 500). Thiếu mốc 0 thì câu trả lời TRỐNG bị snap về mốc
    /// thấp nhất đang có ⇒ người không nói gì vẫn có điểm, và không lỗi nào nổ.
    /// </summary>
    [Fact]
    public async Task Replace_LevelsMissingZero_Throws400Shaped()
    {
        using var t = new TestDb();
        SeedDefaults(t.Db);
        var svc = Service(t);
        var v1 = (await svc.GetAsync(JobCategory.BE, "vi"))!;

        var bad = Echo(v1, levels: _ =>
        [
            new(3, "Nêu được ý chính nhưng thiếu ví dụ cụ thể và chưa nói tới đánh đổi."),
            new(5, "Nêu ý chính, có ví dụ từ dự án thật và chỉ ra được đánh đổi của phương án.")
        ]);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.ReplaceAsync(JobCategory.BE, bad, "vi"));
        Assert.Contains("mốc 0", ex.Message);
    }

    /// <summary>Không gửi mốc nào = "chưa khai mốc" — hợp lệ, không phải lỗi (⇒ dải mặc định).</summary>
    [Fact]
    public async Task Replace_EmptyLevels_IsValid()
    {
        using var t = new TestDb();
        SeedDefaults(t.Db);
        var svc = Service(t);
        var v1 = (await svc.GetAsync(JobCategory.BE, "vi"))!;

        // Khai mốc rồi bỏ hết đi — vẫn phải chạy được.
        var v2 = (await svc.ReplaceAsync(JobCategory.BE, Echo(v1, levels: _ => ValidLevels()), "vi"))!;
        var v3 = (await svc.ReplaceAsync(JobCategory.BE, Echo(v2, levels: _ => []), "vi"))!;

        Assert.True(v3.Changed);
        Assert.All(v3.Criteria, c => Assert.Empty(c.Levels));
    }

    /// <summary>Payload thiếu một tiêu chí ⇒ 400, không âm thầm giữ nội dung cũ của tiêu chí đó.</summary>
    [Fact]
    public async Task Replace_MissingCriterion_Throws()
    {
        using var t = new TestDb();
        SeedDefaults(t.Db);
        var svc = Service(t);
        var v1 = (await svc.GetAsync(JobCategory.BE, "vi"))!;
        var partial = new UpsertAdminRubricRequest(
            Echo(v1).Criteria.Take(v1.Criteria.Count - 1).ToList());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.ReplaceAsync(JobCategory.BE, partial, "vi"));
    }

    /// <summary>Id không thuộc bộ chuẩn (vd id rubric riêng của một ứng viên) ⇒ 400.</summary>
    [Fact]
    public async Task Replace_UnknownCriterionId_Throws()
    {
        using var t = new TestDb();
        SeedDefaults(t.Db);
        var svc = Service(t);
        var v1 = (await svc.GetAsync(JobCategory.BE, "vi"))!;
        var tampered = new UpsertAdminRubricRequest(
            Echo(v1).Criteria.Select((c, i) => i == 0 ? c with { Id = Guid.NewGuid() } : c).ToList());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.ReplaceAsync(JobCategory.BE, tampered, "vi"));
    }

    // ── (5) Reset ⇒ về dải mặc định, KHÔNG bật lại bản cũ ───────────────────────────────────

    [Fact]
    public async Task Reset_AppendsSeedContentAsNewVersion_WithoutReactivatingOldOne()
    {
        using var t = new TestDb();
        SeedDefaults(t.Db);
        var svc = Service(t);
        var v1 = (await svc.GetAsync(JobCategory.BE, "vi"))!;
        var v2 = (await svc.ReplaceAsync(JobCategory.BE, Echo(v1, levels: _ => ValidLevels()), "vi"))!;

        var v3 = (await svc.ResetAsync(JobCategory.BE, "vi"))!;

        Assert.True(v3.Changed);
        Assert.Equal(v2.Version + 1, v3.Version);          // append-only, không quay lại v1
        Assert.All(v3.Criteria, c => Assert.Empty(c.Levels));   // về dải mặc định
        Assert.Equal(7, v3.Criteria.Count);

        // Đúng một bộ active tại mọi thời điểm.
        var active = await t.Db.RubricCriteria.AsNoTracking()
            .Where(c => c.CampaignId == null && c.CandidateId == null
                        && c.JobCategory == JobCategory.BE && c.Language == "vi" && c.IsActive)
            .Select(c => c.Version).Distinct().ToListAsync();
        Assert.Equal([v3.Version], active);
    }

    /// <summary>Reset khi đang ở đúng nội dung gốc ⇒ no-op, không đẻ phiên bản rác.</summary>
    [Fact]
    public async Task Reset_WhenAlreadyDefault_DoesNotBump()
    {
        using var t = new TestDb();
        SeedDefaults(t.Db);
        var svc = Service(t);
        var v1 = (await svc.GetAsync(JobCategory.BE, "vi"))!;

        var result = (await svc.ResetAsync(JobCategory.BE, "vi"))!;

        Assert.False(result.Changed);
        Assert.Equal(v1.Version, result.Version);
    }

    // ── (6) Ma trận + lịch sử ───────────────────────────────────────────────────────────────

    /// <summary>Ma trận phải đếm ĐÚNG số tiêu chí ĐÃ CÓ MỐC — đó là con số duy nhất chỉ ra chỗ bỏ sót.</summary>
    [Fact]
    public async Task Matrix_CountsCriteriaWithLevels_PerCategoryAndLanguage()
    {
        using var t = new TestDb();
        SeedDefaults(t.Db);
        var svc = Service(t);
        var v1 = (await svc.GetAsync(JobCategory.BE, "vi"))!;
        // Khai mốc cho ĐÚNG một tiêu chí.
        var target = v1.Criteria[0].Id;
        await svc.ReplaceAsync(JobCategory.BE,
            Echo(v1, levels: c => c.Id == target ? ValidLevels() : []), "vi");

        var matrix = await svc.GetMatrixAsync(null);

        Assert.Equal(6, matrix.Count);   // 3 nghề × 2 ngôn ngữ
        var beVi = matrix.Single(r => r.JobCategory == JobCategory.BE && r.Language == "vi");
        Assert.Equal(7, beVi.CriteriaCount);
        Assert.Equal(1, beVi.WithLevelsCount);
        var beEn = matrix.Single(r => r.JobCategory == JobCategory.BE && r.Language == "en");
        Assert.Equal(0, beEn.WithLevelsCount);
    }

    [Fact]
    public async Task History_ListsEveryVersion_NewestFirst()
    {
        using var t = new TestDb();
        SeedDefaults(t.Db);
        var svc = Service(t);
        var v1 = (await svc.GetAsync(JobCategory.BE, "vi"))!;
        await svc.ReplaceAsync(JobCategory.BE, Echo(v1, levels: _ => ValidLevels()), "vi");

        var history = await svc.HistoryAsync(JobCategory.BE, "vi");

        Assert.Equal(2, history.Count);
        Assert.Equal([2, 1], history.Select(h => h.Version).ToArray());
        Assert.True(history[0].IsActive);
        Assert.False(history[1].IsActive);
        Assert.Equal(7, history[0].WithLevelsCount);
        Assert.Equal(0, history[1].WithLevelsCount);
    }

    // ── (7) Chưa có bộ nào ⇒ null (controller → 404), không ném ─────────────────────────────

    [Fact]
    public async Task Get_NoSeedApplied_ReturnsNull()
    {
        using var t = new TestDb();
        Assert.Null(await Service(t).GetAsync(JobCategory.BE, "vi"));
    }

    /// <summary>
    /// Ngôn ngữ hợp lệ KHÔNG bị chặn bởi cờ song ngữ: admin phải soạn được bộ tiếng Anh TRƯỚC khi bật
    /// cờ, giống như nạp corpus trước khi bật grounding.
    /// </summary>
    [Fact]
    public async Task Get_EnglishSet_WorksWithoutBilingualFlag()
    {
        using var t = new TestDb();
        SeedDefaults(t.Db);
        var en = await Service(t).GetAsync(JobCategory.BE, "en");
        Assert.NotNull(en);
        Assert.Equal("en", en!.Language);
    }

    [Fact]
    public async Task Get_UnknownLanguage_Throws()
    {
        using var t = new TestDb();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Service(t).GetAsync(JobCategory.BE, "fr"));
    }

    /// <summary>Bộ chuẩn KHÔNG được đụng tới rubric riêng của ứng viên (BC16) dù cùng nghề/ngôn ngữ.</summary>
    [Fact]
    public async Task Replace_LeavesCandidateOwnedRubricUntouched()
    {
        using var t = new TestDb();
        SeedDefaults(t.Db);
        var candidate = Guid.NewGuid();
        var custom = TestDb.Criterion(JobCategory.BE, version: 1, active: true,
            name: "Tự đặt", candidateId: candidate);
        t.Db.RubricCriteria.Add(custom);
        await t.Db.SaveChangesAsync();

        var svc = Service(t);
        var v1 = (await svc.GetAsync(JobCategory.BE, "vi"))!;
        Assert.DoesNotContain(v1.Criteria, c => c.Id == custom.Id);

        await svc.ReplaceAsync(JobCategory.BE, Echo(v1, levels: _ => ValidLevels()), "vi");

        var stillActive = await t.Db.RubricCriteria.AsNoTracking().FirstAsync(c => c.Id == custom.Id);
        Assert.True(stillActive.IsActive);
    }
}
