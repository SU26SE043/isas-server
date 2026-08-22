using Isas.InterviewService.DTOs;
using Isas.InterviewService.Models;
using Isas.InterviewService.Services;

namespace Isas.InterviewService.Tests;

/// <summary>
/// Chấm tiêu chí CÁCH NÓI bằng SỐ ĐO (thay vì nhờ LLM đọc bản chép lời).
///
/// <para><b>Bug đang sửa, đo trên production:</b> nộp CÙNG MỘT file ghi âm cho 4 câu hỏi khác nhau,
/// tiêu chí "Độ trôi chảy &amp; tự tin" nhận <b>0% · 40% · 60%</b> trong khi máy đo VAD ra y hệt
/// nhau cả 4 lần. Chấm nội dung lây sang chấm cách nói.</para>
/// </summary>
public class DeliveryFluencyScorerTests
{
    private static DeliveryScoringOptions Opts() => new();

    private static DeliveryMetricsDto M(
        double? silence = 0.10, double? speechSec = 30, int? pauses = 0,
        double? wpm = 199) => new()
        {
            SilenceRatio = silence,
            SpeechSec = speechSec,
            PauseCount = pauses,
            SpeechRateWpm = wpm,
        };

    // ── Bảng ngưỡng: mỗi PHÂN VỊ rơi đúng bậc đã chốt ────────────────────────────────────
    //
    // Neo thẳng vào phân vị đo được (n=124, đã lọc speech_sec ≥ 10) chứ không vào số tròn:
    //   p10 0,002 · p25 0,036 · p50 0,144 · p75 0,254 · p90 0,359
    [Theory]
    [InlineData(0.002, 5)]    // p10 — im lặng gần như không có
    [InlineData(0.036, 5)]    // p25 — ĐÚNG biên, phải thuộc bậc trên (điều kiện là `<=`)
    [InlineData(0.0361, 4)]   // ngay trên biên p25 → tụt đúng một bậc
    [InlineData(0.144, 4)]    // p50
    [InlineData(0.254, 3)]    // p75
    [InlineData(0.359, 2)]    // p90
    [InlineData(0.36, 1)]     // trên p90
    [InlineData(1.0, 1)]      // toàn im lặng — vẫn 1, KHÔNG phải 0 (xem test về sàn bên dưới)
    public void BangNguong_MoiPhanVi_RoiDungBac(double silence, int expected)
    {
        var r = DeliveryFluencyScorer.Score(M(silence: silence), 5, "vi", Opts());
        Assert.Equal(expected, Assert.NotNull(r).Band);
        Assert.Equal(expected, r!.Value.Score);   // maxScore = 5 = BandScale ⇒ bậc == điểm
    }

    // ── BẤT BIẾN TRUNG TÂM: cùng số đo ⇒ cùng điểm ──────────────────────────────────────
    [Fact]
    public void CungBoSoDo_LuonRaCungMotDiem_KhongPhuThuocGiKhac()
    {
        // Đây là chính cái đã hỏng trên production. Hàm chấm KHÔNG nhận transcript, câu hỏi, hay
        // điểm nội dung — nên không có đường nào để nội dung lây sang. Test gọi lại nhiều lần trên
        // các INSTANCE metrics khác nhau nhưng cùng giá trị, để bắt cả ca lỡ cache theo tham chiếu.
        var results = Enumerable.Range(0, 5)
            .Select(_ => DeliveryFluencyScorer.Score(M(silence: 0.2, speechSec: 42, pauses: 2), 5, "vi", Opts()))
            .ToList();

        Assert.All(results, r => Assert.Equal(results[0]!.Value.Score, Assert.NotNull(r).Score));
        Assert.All(results, r => Assert.Equal(results[0]!.Value.Reasoning, r!.Value.Reasoning));
    }

    // ── THIẾU SỐ ĐO ⇒ LOẠI khỏi điểm, KHÔNG cho 0 ───────────────────────────────────────
    //
    // `null` ở đây nghĩa "call site đừng ghi dòng nào", và đó là cách LOẠI được thực hiện. Cho 0
    // là phạt người học vì thứ không thuộc về họ — cùng luật INT-18. Ở B2C điểm tổng là trung bình
    // cộng (INT-10) nên một số 0 bịa vào kéo tụt cả buổi ~20%.
    [Fact]
    public void ThieuTiLeImLang_TraNull_KhongPhaiDiem0()
        => Assert.Null(DeliveryFluencyScorer.Score(M(silence: null), 5, "vi", Opts()));

    [Fact]
    public void ThieuThoiLuongNoi_TraNull_KhongPhaiDiem0()
        => Assert.Null(DeliveryFluencyScorer.Score(M(speechSec: null), 5, "vi", Opts()));

    [Fact]
    public void KhongCoSoDoNaoCa_TraNull()
        => Assert.Null(DeliveryFluencyScorer.Score(null, 5, "vi", Opts()));

    // ── SÀN thời lượng nói ───────────────────────────────────────────────────────────────
    [Theory]
    [InlineData(0.4)]    // mẫu dưới một giây từng cho ra 1250 âm tiết/phút
    [InlineData(9.99)]
    public void DuoiSanThoiLuong_TraNull(double speechSec)
        => Assert.Null(DeliveryFluencyScorer.Score(M(speechSec: speechSec), 5, "vi", Opts()));

    [Fact]
    public void DungSanThoiLuong_VanChamDuoc()
        => Assert.NotNull(DeliveryFluencyScorer.Score(M(speechSec: 10), 5, "vi", Opts()));

    // ── Số đo hỏng ⇒ LOẠI, không kẹp bừa vào một bậc ────────────────────────────────────
    [Theory]
    [InlineData(-0.1)]
    [InlineData(1.5)]
    public void TiLeImLangNgoaiKhoang_TraNull(double silence)
        => Assert.Null(DeliveryFluencyScorer.Score(M(silence: silence), 5, "vi", Opts()));

    // ── Tín hiệu PHỤ: pause_count chỉ trừ ở ĐUÔI TRÊN ───────────────────────────────────
    [Theory]
    [InlineData(0, 4)]    // p10/p25/p50 đều = 0 ⇒ KHÔNG được thưởng (thước mù ở nửa dưới)
    [InlineData(3, 4)]    // p75 — chưa chạm ngưỡng
    [InlineData(4, 3)]    // p90 — chạm ngưỡng, hạ một bậc
    [InlineData(9, 3)]    // rất nhiều lần dừng: vẫn chỉ hạ một bậc (không cộng dồn)
    public void SoLanDung_ChiTruODuoiTren(int pauses, int expected)
    {
        var r = DeliveryFluencyScorer.Score(M(silence: 0.144, pauses: pauses), 5, "vi", Opts());
        Assert.Equal(expected, Assert.NotNull(r).Band);
    }

    [Fact]
    public void TatTinHieuPhu_ThiKhongTruNua()
    {
        var o = Opts();
        o.PausePenaltyFrom = null;
        Assert.Equal(4, DeliveryFluencyScorer.Score(M(silence: 0.144, pauses: 99), 5, "vi", o)!.Value.Band);
    }

    // ── SÀN điểm = 1, KHÔNG phải 0 ──────────────────────────────────────────────────────
    [Fact]
    public void BacThapNhatCongTruPhu_VanKhongXuongDuoiSan()
    {
        // Ý nghĩa: trên thước này 0 để dành cho "không có gì để đo" — ca đó đã được diễn đạt bằng
        // cách LOẠI tiêu chí (trả null). Người nói đủ 10 giây thì có nhịp nói để đo, dù tệ.
        var r = DeliveryFluencyScorer.Score(M(silence: 0.9, pauses: 20), 5, "vi", Opts());
        Assert.Equal(1, Assert.NotNull(r).Band);
        Assert.Equal(1m, r!.Value.Score);
    }

    // ── Quy về thang THẬT của tiêu chí (rubric riêng BC16 để maxScore khác 5) ────────────
    [Theory]
    [InlineData(5, 4)]      // bộ chuẩn B2C
    [InlineData(10, 8)]     // 4/5 của 10
    [InlineData(20, 16)]
    public void QuyVeThangThatCuaTieuChi(int maxScore, decimal expected)
    {
        var r = DeliveryFluencyScorer.Score(M(silence: 0.144), maxScore, "vi", Opts());
        Assert.Equal(expected, Assert.NotNull(r).Score);
    }

    [Fact]
    public void MaxScoreMeo_TraNull_KhongDoanBua()
        => Assert.Null(DeliveryFluencyScorer.Score(M(), 0, "vi", Opts()));

    // ── Kill-switch ─────────────────────────────────────────────────────────────────────
    [Fact]
    public void TatKillSwitch_TraNull_DeLLMChamNhuCu()
    {
        var o = Opts();
        o.Enabled = false;
        Assert.Null(DeliveryFluencyScorer.Score(M(), 5, "vi", o));
    }

    // ── Nhận xét ────────────────────────────────────────────────────────────────────────
    [Theory]
    [InlineData("vi")]
    [InlineData("en")]
    public void NhanXet_KhongRong_VaKhongNoiLaChuanNganh(string language)
    {
        var r = DeliveryFluencyScorer.Score(M(silence: 0.2, pauses: 2), 5, language, Opts());
        var reasoning = Assert.NotNull(r).Reasoning;

        // Rỗng/quá ngắn ⇒ E11 gắn needs_review; một tiêu chí chấm-bằng-máy kéo cả answer vào diện
        // soi lại là báo động giả có hệ thống.
        Assert.False(string.IsNullOrWhiteSpace(reasoning));
        Assert.True(reasoning.Length >= 60, $"nhận xét quá ngắn: {reasoning}");

        // Bài học F14: nhãn phải nói đúng NGUỒN. Ngưỡng là phân vị của CHÍNH hệ thống này, nên
        // nhận xét phải NÓI RA điều đó thay vì để người đọc tự cho rằng đây là mặt bằng ngành.
        //
        // ⚠ Assert đầu tiên tôi viết là `DoesNotContain("chuẩn ngành")` — và nó ĐỎ vì chính câu
        // đính chính chứa cụm đó. Cấm chuỗi là sai công cụ: thứ cần cấm là LỜI KHẲNG ĐỊNH, mà một
        // lời phủ định thì lại là thứ ta muốn có. Đổi sang đòi đúng câu đính chính.
        Assert.Contains(
            language == "en" ? "not an industry standard" : "không phải chuẩn ngành",
            reasoning, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NhanXet_NoiRoTocDoNoi_KHONG_ThamGiaTinhDiem()
    {
        // Nêu tốc độ nói làm bối cảnh là hữu ích; để người đọc TƯỞNG nó đã trừ điểm thì không.
        var r = DeliveryFluencyScorer.Score(M(silence: 0.2, wpm: 260), 5, "vi", Opts());
        Assert.Contains("KHÔNG tham gia tính điểm", Assert.NotNull(r).Reasoning);
    }

    [Fact]
    public void TocDoNoi_KhongLamDoiDiem()
    {
        // Đơn vị của chỉ số này KHÔNG đồng nhất giữa hai ngôn ngữ (vi đếm ÂM TIẾT, en đếm TỪ) và
        // dải "bình thường" duy nhất hệ đang có thì chưa ai hiệu chuẩn — nên nó không được chạm vào điểm.
        var slow = DeliveryFluencyScorer.Score(M(silence: 0.2, wpm: 60), 5, "vi", Opts());
        var fast = DeliveryFluencyScorer.Score(M(silence: 0.2, wpm: 400), 5, "vi", Opts());
        Assert.Equal(Assert.NotNull(slow).Score, Assert.NotNull(fast).Score);
    }

    // ── CON DẤU: bảng mặc định gắn chặt với RuleVersion ─────────────────────────────────
    [Fact]
    public void BangNguongMacDinh_GanChat_VoiRuleVersion1()
    {
        // 🔴 Đây là guard cho việc RETUNE. Điểm chấm bằng hai bộ ngưỡng khác nhau KHÔNG so sánh được,
        // mà điểm vẫn bị đem so ở BC15 (đo tiến bộ) và F14 (mốc peer). Sửa bảng mặc định trong CODE
        // mà quên bump `RuleVersion` ⇒ test này ĐỎ.
        //
        // ⚠ Retune bằng FILE CẤU HÌNH lúc deploy thì KHÔNG có gì bắt được — đó là phần trách nhiệm
        // của người retune, đã ghi ở `DeliveryScoringOptions.RuleVersion`.
        var o = new DeliveryScoringOptions();

        Assert.Equal(1, o.RuleVersion);
        Assert.Equal(10d, o.MinSpeechSec);
        Assert.Equal(4, o.PausePenaltyFrom);
        Assert.Equal(1, o.PausePenalty);
        Assert.Equal(1, o.MinBand);
        Assert.Equal(5, DeliveryScoringOptions.BandScale);

        Assert.Equal(
            [(0.036, 5), (0.144, 4), (0.254, 3), (0.359, 2), (1.0, 1)],
            o.EffectiveSilenceBands().Select(b => (b.MaxSilenceRatio, b.Score)));
    }

    [Fact]
    public void BangNguongTuCauHinh_DeVAO_ThayThe_ChuKhongCongThem()
    {
        // ⚠ `ConfigurationBinder` bind mảng vào List<T> bằng cách CỘNG THÊM vào phần tử sẵn có. Nếu
        // ai đó đưa bảng mặc định vào property initializer rồi override bằng appsettings thì bảng
        // thật sẽ gồm CẢ HAI bộ — vẫn "chạy", chỉ là chấm bằng ngưỡng không ai chọn.
        var o = new DeliveryScoringOptions();
        Assert.Empty(o.SilenceBands);   // mặc định RỖNG; giá trị thật nằm ở DefaultSilenceBands

        o.SilenceBands = [new SilenceBand { MaxSilenceRatio = 1.0, Score = 2 }];
        Assert.Equal(2, DeliveryFluencyScorer.Score(M(silence: 0.001), 5, "vi", o)!.Value.Band);
    }
}
