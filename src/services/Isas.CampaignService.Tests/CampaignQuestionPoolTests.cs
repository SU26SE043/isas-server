using Isas.CampaignService.Services;

namespace Isas.CampaignService.Tests;

/// <summary>
/// NGÂN HÀNG ĐỀ — <see cref="QuestionPoolSelector"/>: HR up 60 câu, mỗi ứng viên thi 20 câu rút ngẫu
/// nhiên, thứ tự cũng xáo.
///
/// <para>Trước tính năng này <c>ParticipationService</c> gửi TRỌN bộ câu hỏi sang Interview và
/// <c>PracticeService</c> lấy trọn làm đề ⇒ up 60 câu là ứng viên phải trả lời đủ 60. Trần
/// <c>MaxQuestions</c> không cứu được: nó chỉ giới hạn số câu ĐÀO SÂU do AI sinh thêm.</para>
///
/// Khoá các hành vi:
/// (a) không đặt <c>questionsPerSession</c> → lấy HẾT, đúng thứ tự HR soạn (chiến dịch cũ không đổi);
/// (b) đặt số → rút đúng ngần đó câu;
/// (c) câu <c>IsRequired</c> LUÔN có mặt;
/// (d) rút ĐỀU theo nhóm (INT-18: tiêu chí không ai hỏi bị loại khỏi điểm ⇒ rút mù là đo hai thước);
/// (e) cùng (chiến dịch, ứng viên) → ĐÚNG đề cũ (create-or-get: vào lại không được đổi đề);
/// (f) ứng viên khác → đề khác;
/// (g) thứ tự bị xáo, không theo thứ tự HR soạn.
/// </summary>
public class CampaignQuestionPoolTests
{
    private static readonly Guid Campaign = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Candidate = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static PoolQuestion Q(string text, bool required = false, string? group = null)
        => new(Guid.NewGuid(), text, null, required, group);

    /// <summary>N câu không bắt buộc, không nhóm — dùng cho các ca không quan tâm phân nhóm.</summary>
    private static List<PoolQuestion> Pool(int n, string prefix = "C")
        => Enumerable.Range(1, n).Select(i => Q($"{prefix}{i}")).ToList();

    // ───────────────── (a) không bật ngân hàng đề = hành vi cũ ─────────────────

    [Fact]
    public void Khong_dat_questions_per_session_thi_lay_HET_cau_dung_thu_tu()
    {
        var pool = Pool(60);

        var result = QuestionPoolSelector.Select(pool, null, Campaign, Candidate);

        Assert.Equal(60, result.Count);
        // Giữ NGUYÊN thứ tự HR soạn — không xáo, không rút.
        Assert.Equal(pool.Select(q => q.Id), result.Select(q => q.Id));
    }

    [Fact]
    public void Questions_per_session_lon_hon_ngan_hang_thi_lay_het_khong_loi()
    {
        var pool = Pool(5);

        var result = QuestionPoolSelector.Select(pool, 20, Campaign, Candidate);

        Assert.Equal(5, result.Count);
        Assert.Equal(pool.Select(q => q.Id), result.Select(q => q.Id));
    }

    [Fact]
    public void Select_khong_sua_danh_sach_goc_cua_caller()
    {
        var pool = Pool(10);
        var snapshot = pool.Select(q => q.Id).ToList();

        QuestionPoolSelector.Select(pool, 4, Campaign, Candidate);

        Assert.Equal(snapshot, pool.Select(q => q.Id));
    }

    // ───────────────── (b) rút đúng số ─────────────────

    [Fact]
    public void Rut_dung_so_cau_yeu_cau_tu_ngan_hang()
    {
        var result = QuestionPoolSelector.Select(Pool(60), 20, Campaign, Candidate);

        Assert.Equal(20, result.Count);
        Assert.Equal(20, result.Select(q => q.Id).Distinct().Count());   // không rút trùng
    }

    [Fact]
    public void Moi_cau_rut_ra_deu_thuoc_ngan_hang_goc()
    {
        var pool = Pool(60);

        var result = QuestionPoolSelector.Select(pool, 20, Campaign, Candidate);

        var poolIds = pool.Select(q => q.Id).ToHashSet();
        Assert.All(result, q => Assert.Contains(q.Id, poolIds));
    }

    // ───────────────── (c) câu bắt buộc ─────────────────

    [Fact]
    public void Cau_bat_buoc_LUON_co_trong_moi_de()
    {
        var required = new[] { Q("Bắt buộc 1", required: true), Q("Bắt buộc 2", required: true) };
        var pool = required.Concat(Pool(58)).ToList();

        // Thử nhiều ứng viên khác nhau: câu bắt buộc phải có mặt trong TẤT CẢ, không phải "thường là có".
        for (var i = 0; i < 20; i++)
        {
            var result = QuestionPoolSelector.Select(pool, 20, Campaign, Guid.NewGuid());
            Assert.Equal(20, result.Count);
            Assert.All(required, r => Assert.Contains(result, q => q.Id == r.Id));
        }
    }

    [Fact]
    public void So_cau_bat_buoc_vuot_qua_tran_thi_lay_het_va_ghi_canh_bao()
    {
        var pool = Enumerable.Range(1, 25).Select(i => Q($"BB{i}", required: true)).ToList();
        string? warning = null;

        var result = QuestionPoolSelector.Select(pool, 20, Campaign, Candidate, w => warning = w);

        // Cắt bớt câu HR đánh dấu "bắt buộc" mới là thứ phản bội đúng chữ đó → giữ hết.
        Assert.Equal(25, result.Count);
        Assert.NotNull(warning);
        Assert.Contains("25", warning);
        Assert.Contains("20", warning);
    }

    [Fact]
    public void So_cau_bat_buoc_bang_dung_tran_thi_KHONG_canh_bao()
    {
        var pool = Enumerable.Range(1, 20).Select(i => Q($"BB{i}", required: true))
            .Concat(Pool(40)).ToList();
        string? warning = null;

        var result = QuestionPoolSelector.Select(pool, 20, Campaign, Candidate, w => warning = w);

        Assert.Equal(20, result.Count);
        Assert.Null(warning);   // vừa khít, không có gì bất thường để báo
    }

    [Fact]
    public void Moi_cau_deu_bat_buoc_thi_ra_dung_ca_bo()
    {
        var pool = Enumerable.Range(1, 10).Select(i => Q($"BB{i}", required: true)).ToList();

        var result = QuestionPoolSelector.Select(pool, 5, Campaign, Candidate);

        Assert.Equal(10, result.Count);
    }

    // ───────────────── (d) rút đều theo nhóm ─────────────────

    [Fact]
    public void Rut_deu_theo_nhom_moi_nhom_du_phan()
    {
        // 4 nhóm × 15 câu = 60; rút 20 ⇒ mỗi nhóm đúng 5.
        var pool = new List<PoolQuestion>();
        foreach (var g in new[] { "Thuật toán", "Thiết kế hệ thống", "Kinh nghiệm", "Giao tiếp" })
            pool.AddRange(Enumerable.Range(1, 15).Select(i => Q($"{g}-{i}", group: g)));

        var result = QuestionPoolSelector.Select(pool, 20, Campaign, Candidate);

        Assert.Equal(20, result.Count);
        var byGroup = result.GroupBy(q => q.Group).ToDictionary(g => g.Key!, g => g.Count());
        Assert.Equal(4, byGroup.Count);
        Assert.All(byGroup.Values, c => Assert.Equal(5, c));
    }

    [Fact]
    public void Nhom_it_cau_hon_phan_duoc_chia_thi_khe_du_chuyen_sang_nhom_khac()
    {
        // Nhóm "Hiếm" chỉ có 1 câu nhưng phần chia là 5 ⇒ 4 khe thừa phải sang nhóm khác,
        // nếu không buổi thi ra 17 câu trong khi HR đặt 20 mà chẳng có lỗi nào.
        var pool = new List<PoolQuestion> { Q("Hiếm-1", group: "Hiếm") };
        foreach (var g in new[] { "A", "B", "C" })
            pool.AddRange(Enumerable.Range(1, 15).Select(i => Q($"{g}-{i}", group: g)));

        var result = QuestionPoolSelector.Select(pool, 20, Campaign, Candidate);

        Assert.Equal(20, result.Count);
        Assert.Single(result, q => q.Group == "Hiếm");
    }

    [Fact]
    public void Chua_phan_nhom_thi_rut_ngau_nhien_tu_mot_ro()
    {
        var result = QuestionPoolSelector.Select(Pool(60), 20, Campaign, Candidate);

        Assert.Equal(20, result.Count);
        Assert.All(result, q => Assert.Null(q.Group));
    }

    [Fact]
    public void Ten_nhom_khac_hoa_thuong_van_tinh_la_MOT_nhom()
    {
        // HR gõ tay trong Excel: "Thuật toán" và "thuật toán" là cùng một mảng năng lực. Coi là hai
        // nhóm thì phép chia đều bị lệch và mảng đó được rút gấp đôi phần đáng ra của nó.
        var pool = Enumerable.Range(1, 10).Select(i => Q($"X{i}", group: "Thuật toán"))
            .Concat(Enumerable.Range(1, 10).Select(i => Q($"Y{i}", group: "THUẬT TOÁN")))
            .Concat(Enumerable.Range(1, 10).Select(i => Q($"Z{i}", group: "Giao tiếp")))
            .ToList();

        var result = QuestionPoolSelector.Select(pool, 10, Campaign, Candidate);

        Assert.Equal(10, result.Count);
        var thuatToan = result.Count(q => string.Equals(q.Group, "Thuật toán", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(5, thuatToan);   // 2 nhóm ⇒ chia đôi, không phải 3 nhóm
    }

    // ───────────────── (e)(f) deterministic ─────────────────

    [Fact]
    public void Cung_ung_vien_rut_lai_ra_DUNG_de_cu()
    {
        var pool = Pool(60);

        var first = QuestionPoolSelector.Select(pool, 20, Campaign, Candidate);
        var second = QuestionPoolSelector.Select(pool, 20, Campaign, Candidate);

        // Buổi thi là create-or-get: đóng tab mở lại phải ra ĐÚNG đề — cả tập lẫn THỨ TỰ.
        Assert.Equal(first.Select(q => q.Id), second.Select(q => q.Id));
    }

    [Fact]
    public void Hai_ung_vien_khac_nhau_ra_de_khac_nhau()
    {
        var pool = Pool(60);

        var a = QuestionPoolSelector.Select(pool, 20, Campaign, Guid.NewGuid());
        var b = QuestionPoolSelector.Select(pool, 20, Campaign, Guid.NewGuid());

        Assert.NotEqual(a.Select(q => q.Id), b.Select(q => q.Id));
    }

    [Fact]
    public void Cung_ung_vien_o_hai_chien_dich_khac_nhau_ra_de_khac_nhau()
    {
        var pool = Pool(60);

        var a = QuestionPoolSelector.Select(pool, 20, Guid.NewGuid(), Candidate);
        var b = QuestionPoolSelector.Select(pool, 20, Guid.NewGuid(), Candidate);

        Assert.NotEqual(a.Select(q => q.Id), b.Select(q => q.Id));
    }

    // ───────────────── (g) xáo thứ tự ─────────────────

    [Fact]
    public void Thu_tu_cau_duoc_xao_khong_theo_thu_tu_HR_soan()
    {
        var pool = Pool(60);

        var result = QuestionPoolSelector.Select(pool, 20, Campaign, Candidate);

        // Thứ tự trong đề không được là thứ tự tương đối của chúng trong ngân hàng.
        var indexInPool = result.Select(q => pool.FindIndex(p => p.Id == q.Id)).ToList();
        Assert.False(
            indexInPool.SequenceEqual(indexInPool.OrderBy(i => i)),
            "Đề rút ra vẫn giữ nguyên thứ tự HR soạn — chưa xáo.");
    }

    [Fact]
    public void Cau_bat_buoc_KHONG_bi_don_len_dau_de()
    {
        // Không xáo lần cuối thì câu bắt buộc luôn đứng đầu và các nhóm luôn theo thứ tự tên ⇒ ứng viên
        // thi sau đoán được cấu trúc đề dù không biết câu cụ thể.
        var required = Enumerable.Range(1, 5).Select(i => Q($"BB{i}", required: true)).ToList();
        var pool = required.Concat(Pool(55)).ToList();
        var requiredIds = required.Select(q => q.Id).ToHashSet();

        var dondau = 0;
        for (var i = 0; i < 20; i++)
        {
            var result = QuestionPoolSelector.Select(pool, 20, Campaign, Guid.NewGuid());
            if (result.Take(5).All(q => requiredIds.Contains(q.Id))) dondau++;
        }

        Assert.True(dondau <= 1, $"Câu bắt buộc bị dồn lên đầu ở {dondau}/20 lượt — chưa xáo lần cuối.");
    }
}
