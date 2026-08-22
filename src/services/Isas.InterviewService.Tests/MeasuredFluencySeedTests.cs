using Isas.InterviewService.Data;
using Isas.InterviewService.DTOs;
using Isas.InterviewService.Entities;
using Isas.InterviewService.Enums;
using Isas.InterviewService.Services;
using Microsoft.EntityFrameworkCore;

namespace Isas.InterviewService.Tests;

/// <summary>
/// Bộ chuẩn B2C + rubric riêng BC16: TIÊU CHÍ NÀO được chấm bằng số đo, và mô tả có nói thật không.
/// </summary>
public class MeasuredFluencySeedTests
{
    private static List<RubricCriterion> Seed() => B2CRubricSeed.Build();

    // ── (1) Đúng 6 row (3 nghề × 2 ngôn ngữ) chấm bằng số đo, và đều là tiêu chí trôi chảy ──
    [Fact]
    public void Seed_DungSauRowChamBangSoDo_DeuLaTieuChiTroiChay()
    {
        // ⚠ MỘT lần `Seed()` rồi chia đôi. Gọi `Seed()` hai lần rồi `Except` sẽ so bằng THAM CHIẾU
        // trên hai bộ instance khác nhau ⇒ không loại được gì ⇒ vế "mọi tiêu chí còn lại giữ Ai"
        // thành khẳng định trên cả 42 dòng và ĐỎ. (Đã vấp đúng lỗi này ở lượt đầu.)
        var all = Seed();
        var measured = all.Where(c => c.ScoringMethod == CriterionScoringMethod.DeliveryMetrics).ToList();

        Assert.Equal(6, measured.Count);
        Assert.Equal(
            [JobCategory.BA, JobCategory.BA, JobCategory.BE, JobCategory.BE, JobCategory.FE, JobCategory.FE],
            measured.Select(c => c.JobCategory).OrderBy(x => x));
        Assert.Equal(["en", "en", "en", "vi", "vi", "vi"], measured.Select(c => c.Language).OrderBy(x => x));
        Assert.All(measured, c => Assert.Contains(
            c.Name, new[] { B2CRubricSeed.FluencyName, "Fluency & confidence" }));

        // Mọi tiêu chí còn lại giữ `Ai` — chiều mặc định phải là "vẫn nhờ LLM chấm".
        Assert.All(
            all.Where(c => c.ScoringMethod != CriterionScoringMethod.DeliveryMetrics),
            c => Assert.Equal(CriterionScoringMethod.Ai, c.ScoringMethod));
    }

    [Fact]
    public void Seed_HaiNgonNgu_KhongLechNhauVeNguonDiem()
    {
        // Khai lại bằng tay cho bản `en` là mở đường cho hai ngôn ngữ chấm bằng hai thước mà không
        // lỗi nào nổ. Bản `en` PHẢI thừa kế từ `vi`.
        foreach (var group in Seed().GroupBy(c => (c.JobCategory, Order(c))))
            Assert.Single(group.Select(c => c.ScoringMethod).Distinct());

        static int Order(RubricCriterion c) => c.Id.ToByteArray()[15];   // hậu tố GUID = vị trí trong nghề
    }

    // ── (2) VIỆC 2 — từ đệm chỉ còn bị tính MỘT lần ─────────────────────────────────────────
    [Fact]
    public void TieuChiNguPhap_KhongCon_TinhTuDem()
    {
        // Trước đây CẢ HAI tiêu chí đều nhắc "ít từ đệm/lặp thừa" ⇒ một thói quen bị trừ điểm HAI
        // LẦN, rồi cả hai cùng vào trung bình cộng B2C (INT-10) nên phần trừ nặng gấp đôi mọi thứ khác.
        var grammar = Seed().Where(c => c.Name == B2CRubricSeed.LanguageName).ToList();
        Assert.Equal(3, grammar.Count);

        Assert.All(grammar, c =>
        {
            Assert.DoesNotContain("từ đệm/lặp thừa", c.Description!);
            Assert.DoesNotContain("lặp từ đệm liên tục", c.Description!);
            // Bỏ vế cũ là CHƯA ĐỦ: mô tả đi nguyên văn vào prompt chấm, nên phải NÓI THẲNG là không
            // xét — không thì LLM vẫn tự do trừ điểm ngập ngừng ở đây và việc gỡ chỉ có tác dụng trên giấy.
            Assert.Contains("KHÔNG xét từ đệm", c.Description!);
        });
    }

    [Fact]
    public void TieuChiNguPhapBanTiengAnh_CungKhongCon_TinhTuDem()
    {
        var grammarEn = Seed().Where(c => c.Name == "Grammar & word choice").ToList();
        Assert.Equal(3, grammarEn.Count);
        Assert.All(grammarEn, c =>
        {
            Assert.DoesNotContain("with few fillers", c.Description!);
            Assert.Contains("Do not assess fillers", c.Description!);
        });
    }

    // ── (3) Mô tả tiêu chí trôi chảy phải nói ĐÚNG phương pháp ─────────────────────────────
    [Fact]
    public void MoTaTieuChiTroiChay_NoiDungPhuongPhap_VaLuatLoaiKhoiDiem()
    {
        // Mô tả là thứ NGƯỜI LUYỆN đọc. Để nó mô tả một phương pháp không còn được dùng thì họ
        // không hiểu vì sao điểm ra như thế — đúng hạng "nhãn nói dối" của F14.
        foreach (var c in Seed().Where(x => x.ScoringMethod == CriterionScoringMethod.DeliveryMetrics))
        {
            var vi = c.Language == "vi";
            Assert.Contains(vi ? "tỉ lệ thời gian im lặng" : "proportion of silent time", c.Description!);
            Assert.Contains(vi ? "LOẠI khỏi điểm, không tính 0" : "excluded from the score", c.Description!);
        }
    }

    // ── (4) BC16 — rubric riêng thừa kế nguồn điểm theo TÊN, lúc GHI ───────────────────────
    [Fact]
    public async Task RubricRieng_TrungTenBoChuan_ThuaKeChamBangSoDo()
    {
        // Không thừa kế thì đúng người tự tuỳ chỉnh rubric lại là người KHÔNG được hưởng bản vá —
        // y hệt nghịch lý "tự tuỳ chỉnh xong thì chấm tệ đi" mà BC-8 đã phải đi sửa một lần.
        using var t = new TestDb();
        t.Db.RubricCriteria.AddRange(Seed());
        await t.Db.SaveChangesAsync();

        var candidate = Guid.NewGuid();
        await new RubricLibraryService(t.Db).ReplaceAsync(candidate, JobCategory.BE, new UpsertRubricRequest(
        [
            new RubricCriterionInput(B2CRubricSeed.FluencyName, "tự mô tả", 0.5m, 5, null),
            new RubricCriterionInput("Tiêu chí tôi tự nghĩ ra", "khác hẳn", 0.5m, 5, null),
        ]));

        var rows = await t.Db.RubricCriteria.AsNoTracking()
            .Where(c => c.CandidateId == candidate).ToListAsync();

        Assert.Equal(CriterionScoringMethod.DeliveryMetrics,
            rows.Single(c => c.Name == B2CRubricSeed.FluencyName).ScoringMethod);

        // Trượt khớp ⇒ `Ai` = ĐÚNG hành vi cũ. Gán nhầm sang số đo cho một tiêu chí ứng viên tự nghĩ
        // ra là thay điểm chuyên môn bằng một con số đo nhịp nói, sai mà không có triệu chứng.
        Assert.Equal(CriterionScoringMethod.Ai,
            rows.Single(c => c.Name == "Tiêu chí tôi tự nghĩ ra").ScoringMethod);
    }

    [Fact]
    public async Task RubricRieng_KhongCoBoChuan_ThiGiuNguyenAi()
    {
        // Không có bộ chuẩn để đối chiếu ⇒ mọi tiêu chí giữ `Ai` = hành vi cũ, không đoán bừa.
        using var t = new TestDb();
        var candidate = Guid.NewGuid();

        await new RubricLibraryService(t.Db).ReplaceAsync(candidate, JobCategory.BE, new UpsertRubricRequest(
            [new RubricCriterionInput(B2CRubricSeed.FluencyName, "mô tả", 1.0m, 5, null)]));

        Assert.All(
            await t.Db.RubricCriteria.AsNoTracking().Where(c => c.CandidateId == candidate).ToListAsync(),
            c => Assert.Equal(CriterionScoringMethod.Ai, c.ScoringMethod));
    }
}
