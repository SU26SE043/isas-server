using Isas.CampaignService.DTOs;
using Isas.CampaignService.Models;
using Isas.CampaignService.Services;

namespace Isas.CampaignService.Tests;

/// <summary>
/// BUG-2 (D-5 mở rộng) — L3 dev campaign efd03037: K=2, câu bắt buộc q4 [C], optional q3 [], q1 [B], q2 [C].
/// Selector giữ q4, còn 1 khe chia cho 3 rổ theo tên khoá <c>"" &lt; B &lt; C</c> ⇒ quota [1,0,0] ⇒ rổ "" LUÔN thắng
/// ⇒ <b>B không bao giờ được hỏi với bất kỳ ứng viên nào</b> (thứ tự rổ không phụ thuộc ứng viên) trong khi
/// <c>K_BELOW_CRITERIA_GROUPS</c> (distinct nhãn[0] toàn bộ = {B,C} = 2 ≤ 2) và <c>coverageWarnings</c> (B có câu
/// nhắm) đều im.
///
/// <para>Luật chốt: (a) selector — rổ TIÊU CHÍ chưa được câu bắt buộc phủ nhận 1 khe TRƯỚC, phần còn lại rót
/// đầy dần trên mọi rổ (khe ≥ rổ ⇒ y như chia đều cũ); rổ ""/nhóm HR không được ưu tiên. (b) K-rule —
/// chặn khi <c>K − |required| &lt; |tiêu chí chính của câu KHÔNG bắt buộc chưa được câu bắt buộc phủ|</c>;
/// 0 required ⇒ công thức cũ.</para>
///
/// <para>GUID cố định để thứ tự khoá rổ tất định trong test: B = 1111…, C = 2222… ⇒ "" &lt; B &lt; C.</para>
/// </summary>
public class QuestionPoolCriterionPriorityBug2Tests
{
    private static readonly Guid CampaignId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid B = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly Guid C = Guid.Parse("22222222-2222-3333-4444-555555555555");

    private static PoolQuestion Pq(string text, List<Guid>? targets = null, bool required = false, string? group = null)
        => new(Guid.NewGuid(), text, null, required, group) { TargetCriterionIds = targets };

    private static IEnumerable<Guid> Candidates(int n) => Enumerable.Range(1, n).Select(i => Guid.Parse($"00000000-0000-0000-0000-{i:D12}"));

    private static CampaignQuestion Q(string text, List<Guid>? targets, bool required = false)
        => new() { Id = Guid.NewGuid(), QuestionText = text, IsRequired = required, TargetCriterionIds = targets, CreatedAt = DateTime.UtcNow };

    private static bool HasKRule(QuestionBankSummary s)
        => s.Warnings.Any(w => w.StartsWith(QuestionBankSummary.KBelowCriteriaGroupsCode + ":", StringComparison.Ordinal));

    // ═══════════════ (a) selector ═══════════════

    /// <summary>Tái hiện đúng ca đo: mọi ứng viên nhận q4 + q1 (B luôn có mặt), q3 (không nhãn) KHÔNG BAO GIỜ.</summary>
    [Fact]
    public void CaDo_K2_RequiredC_ThiBLuonDuocHoi_KhongNhanKhongBaoGio()
    {
        var q1 = Pq("q1", new() { B });
        var q2 = Pq("q2", new() { C });
        var q3 = Pq("q3", new());               // [] = không nhãn ⇒ rổ ""
        var q4 = Pq("q4", new() { C }, required: true);
        var pool = new List<PoolQuestion> { q1, q2, q3, q4 };

        foreach (var cand in Candidates(30))
        {
            var got = QuestionPoolSelector.Select(pool, 2, CampaignId, cand);
            Assert.Equal(2, got.Count);
            Assert.Contains(got, q => q.Id == q4.Id);   // bắt buộc
            Assert.Contains(got, q => q.Id == q1.Id);   // B — trước fix: không bao giờ
            Assert.DoesNotContain(got, q => q.Id == q3.Id);
        }
    }

    /// <summary>Câu bắt buộc PHỦ tiêu chí ⇒ rổ đó không tốn khe ưu tiên: required [B], khe 1 ⇒ khe về C, không về B.</summary>
    [Fact]
    public void RequiredPhuTieuChi_KhongTonKheUuTien()
    {
        var pool = new List<PoolQuestion>
        {
            Pq("rB", new() { B }, required: true),
            Pq("b1", new() { B }), Pq("b2", new() { B }),
            Pq("c1", new() { C }),
            Pq("x1", null, group: "X"),
        };

        foreach (var cand in Candidates(30))
        {
            var got = QuestionPoolSelector.Select(pool, 2, CampaignId, cand);
            Assert.Equal(2, got.Count);
            Assert.Contains(got, q => q.Text == "rB");
            Assert.Contains(got, q => q.Text == "c1");   // B đã được required phủ ⇒ khe duy nhất dành cho C (thứ tự khoá B < C — không phải "B trước")
        }
    }

    /// <summary>0 required, K=2, rổ {"", B, C} ⇒ B + C cho MỌI ứng viên; rổ "" không bao giờ khi khe chỉ đủ tiêu chí.</summary>
    [Fact]
    public void KhongRequired_K2_BaRo_RoKhongNhanKhongBaoGio()
    {
        var pool = new List<PoolQuestion>
        {
            Pq("b1", new() { B }), Pq("b2", new() { B }),
            Pq("c1", new() { C }), Pq("c2", new() { C }),
            Pq("x1", null), Pq("x2", null),
        };

        foreach (var cand in Candidates(30))
        {
            var got = QuestionPoolSelector.Select(pool, 2, CampaignId, cand);
            Assert.Equal(1, got.Count(q => q.Text.StartsWith('b')));
            Assert.Equal(1, got.Count(q => q.Text.StartsWith('c')));
            Assert.Equal(0, got.Count(q => q.Text.StartsWith('x')));
        }
    }

    /// <summary>Khe ≥ số rổ ⇒ phân phối Y NHƯ chia đều cũ (base + dư rải cho rổ đầu theo tên khoá): K=5 ⇒ ["" 2, B 2, C 1].</summary>
    [Fact]
    public void KheDuRo_PhanPhoiNhuChiaDeuCu()
    {
        var pool = new List<PoolQuestion>
        {
            Pq("b1", new() { B }), Pq("b2", new() { B }), Pq("b3", new() { B }),
            Pq("c1", new() { C }), Pq("c2", new() { C }), Pq("c3", new() { C }),
            Pq("x1", null), Pq("x2", null), Pq("x3", null),
        };

        foreach (var cand in Candidates(10))
        {
            var got = QuestionPoolSelector.Select(pool, 5, CampaignId, cand);
            Assert.Equal(2, got.Count(q => q.Text.StartsWith('x')));   // rổ "" đứng đầu ⇒ nhận phần dư như cũ
            Assert.Equal(2, got.Count(q => q.Text.StartsWith('b')));
            Assert.Equal(1, got.Count(q => q.Text.StartsWith('c')));
        }
    }

    // ═══════════════ test-gap BUG-1/2 (Tester probe) ═══════════════

    private static readonly Guid D = Guid.Parse("33333333-2222-3333-4444-555555555555");

    /// <summary>Required ĐA NHÃN [B,C]: "phủ" chỉ theo nhãn[0] = B ⇒ khe còn lại phải về C (không phải coi C đã phủ).</summary>
    [Fact]
    public void RequiredDaNhan_PhuTheoNhan0_KheVeC()
    {
        var pool = new List<PoolQuestion>
        {
            Pq("rBC", new() { B, C }, required: true),
            Pq("b1", new() { B }),
            Pq("c1", new() { C }),
        };
        foreach (var cand in Candidates(30))
        {
            var got = QuestionPoolSelector.Select(pool, 2, CampaignId, cand);
            Assert.Equal(2, got.Count);
            Assert.Contains(got, q => q.Text == "rBC");
            Assert.Contains(got, q => q.Text == "c1");   // C chưa được phủ (nhãn[0] của required là B)
        }
    }

    /// <summary>Required KHÔNG nhãn không phủ tiêu chí nào ⇒ rổ B vẫn được ưu tiên trước rổ "".</summary>
    [Fact]
    public void RequiredKhongNhan_KhongPhuGi_RoTieuChiVanDuocUuTien()
    {
        var pool = new List<PoolQuestion>
        {
            Pq("r", new(), required: true),
            Pq("b1", new() { B }),
            Pq("x1", null),
        };
        foreach (var cand in Candidates(30))
        {
            var got = QuestionPoolSelector.Select(pool, 2, CampaignId, cand);
            Assert.Equal(2, got.Count);
            Assert.Contains(got, q => q.Text == "r");
            Assert.Contains(got, q => q.Text == "b1");
        }
    }

    /// <summary>Selector KHÔNG BAO GIỜ vượt K: 3 rổ tiêu chí chưa phủ, 0 required, K=2 ⇒ đúng 2 câu = 2 rổ ĐẦU theo tên khoá (B, C).</summary>
    [Fact]
    public void KhongVuotK_KheItHonRo_LayRoDauTheoTen()
    {
        var pool = new List<PoolQuestion>
        {
            Pq("b1", new() { B }), Pq("c1", new() { C }), Pq("d1", new() { D }),
        };
        foreach (var cand in Candidates(30))
        {
            var got = QuestionPoolSelector.Select(pool, 2, CampaignId, cand);
            Assert.Equal(2, got.Count);
            Assert.Equal(new[] { "b1", "c1" }, got.Select(q => q.Text).OrderBy(t => t));
        }
    }

    /// <summary>K-rule: required [B,C] chỉ phủ B (nhãn[0]); optional {C, D} ⇒ uncovered {C,D} = 2 > K−1 = 1 ⇒ CHẶN.</summary>
    [Fact]
    public void KRule_RequiredDaNhan_ChiPhuNhan0_Chan()
    {
        var qs = new[] { Q("r", new() { B, C }, required: true), Q("c1", new() { C }), Q("d1", new() { D }) };
        Assert.True(HasKRule(QuestionBankSummary.Build(qs, 2, null, null)));
    }

    // ═══════════════ (b) K-rule ═══════════════

    [Fact]
    public void KRule_K2_1RequiredC_OptionalBC_KhongNhan_KhongChan()
    {
        // 2 − 1 = 1 khe ≥ 1 tiêu chí chưa phủ (B) ⇒ KHÔNG chặn (C đã được required phủ)
        var qs = new[] { Q("q4", new() { C }, required: true), Q("q1", new() { B }), Q("q2", new() { C }), Q("q3", new()) };
        Assert.False(HasKRule(QuestionBankSummary.Build(qs, 2, null, null)));
    }

    [Fact]
    public void KRule_K2_1RequiredKhongNhan_OptionalBC_Chan_ThongDiepNeu3So()
    {
        // 2 − 1 = 1 khe < 2 tiêu chí chưa phủ ({B, C}) ⇒ CHẶN
        var qs = new[] { Q("r", new(), required: true), Q("q1", new() { B }), Q("q2", new() { C }) };
        var s = QuestionBankSummary.Build(qs, 2, null, null);
        Assert.True(HasKRule(s));
        var w = s.Warnings.Single(x => x.StartsWith(QuestionBankSummary.KBelowCriteriaGroupsCode));
        Assert.Contains("(2)", w); Assert.Contains("1 câu bắt buộc", w); Assert.Contains("(2)", w[w.IndexOf("chưa được", StringComparison.Ordinal)..]);
    }

    [Fact]
    public void KRule_0Required_SuyBienVeCongThucCu()
    {
        var qs = new[] { Q("q1", new() { B }), Q("q2", new() { C }), Q("q3", new()) };
        Assert.False(HasKRule(QuestionBankSummary.Build(qs, 2, null, null)));   // 2 ≥ 2
        Assert.True(HasKRule(QuestionBankSummary.Build(qs, 1, null, null)));    // 1 < 2
        Assert.False(HasKRule(QuestionBankSummary.Build(qs, null, null, null)));
    }
}
