namespace Isas.InterviewService.Models;

/// <summary>
/// Ngưỡng chấm tiêu chí CÁCH NÓI bằng SỐ ĐO (<see cref="Enums.CriterionScoringMethod.DeliveryMetrics"/>).
///
/// <para><b>🔴 Các con số mặc định dưới đây là PHÂN VỊ ĐO ĐƯỢC, không phải chuẩn ngành.</b> Lấy từ
/// <c>practice_answers</c> trên production, n=124 (đã lọc <c>speech_sec &gt;= 10</c>):</para>
/// <code>
/// silence_ratio    p10 0,002 · p25 0,036 · p50 0,144 · p75 0,254 · p90 0,359
/// pause_count      p10 0     · p25 0     · p50 0     · p75 3     · p90 4
/// speech_rate_wpm  p10 149   · p25 169   · p50 199   · p75 247   · p90 267
/// </code>
///
/// <para>⚠ <b>Đây là ĐIỂM KHỞI ĐẦU, chưa phải hiệu chuẩn.</b> Mẫu 124 answer đó lẫn nhiều audio
/// TỔNG HỢP (sinh bằng <c>say</c> lúc chạy thử e2e) — giọng máy đọc đều, không ngập ngừng, nên nó
/// kéo đuôi "trôi chảy" xuống thấp một cách giả tạo. Cắt lại khi có đủ bản ghi người thật; đó là
/// lý do bảng nằm ở CẤU HÌNH chứ không phải hằng số chôn trong code. Đừng viết ở đâu rằng đây là
/// mặt bằng ngành — không có dữ liệu nào nói thế (bài học F14).</para>
/// </summary>
public class DeliveryScoringOptions
{
    public const string SectionName = "DeliveryScoring";

    /// <summary>
    /// Thang của <see cref="SilenceBand.Score"/> trong bảng ngưỡng. Điểm bậc được quy về thang
    /// THẬT của tiêu chí (<c>maxScore</c>) theo tỉ lệ, nên rubric riêng BC16 để <c>maxScore = 10</c>
    /// vẫn dùng chung một bảng thay vì bị kẹt trần ở 5/10 mà không ai thấy.
    /// </summary>
    public const int BandScale = 5;

    /// <summary>
    /// Kill-switch. Tắt ⇒ tiêu chí <c>DeliveryMetrics</c> quay lại nhờ LLM chấm y như trước
    /// (không cần deploy, không cần sửa dữ liệu rubric).
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Con dấu phiên bản BỘ NGƯỠNG, ghi xuống <c>answer_scores.delivery_scoring_version</c>.
    ///
    /// <para>🔴 <b>ĐỔI NGƯỠNG THÌ PHẢI BUMP SỐ NÀY.</b> Điểm chấm bằng hai bộ ngưỡng khác nhau
    /// KHÔNG so sánh được với nhau, mà điểm vẫn bị đem so ở đo tiến bộ roadmap (BC15) và mốc peer
    /// (F14). Bảng mặc định trong code có test khoá gắn với version 1 (<c>DeliveryFluencyScorerTests</c>)
    /// nên sửa code mà quên bump là ĐỎ ngay; sửa bằng file cấu hình lúc deploy thì KHÔNG có gì bắt
    /// được — đó là phần trách nhiệm của người retune.</para>
    /// </summary>
    public int RuleVersion { get; set; } = 1;

    /// <summary>
    /// SÀN thời lượng NÓI (giây) để số đo có nghĩa. Dưới sàn ⇒ tiêu chí bị LOẠI khỏi điểm (không
    /// tính 0) — xem <see cref="Services.DeliveryFluencyScorer"/>.
    ///
    /// <para>Vì sao 10s: tỉ lệ tính trên mẫu 0,4–0,9 giây từng cho ra <b>1250 âm tiết/phút</b>. Trên
    /// production 129/253 answer nằm dưới sàn này, nên đây KHÔNG phải ca hiếm.</para>
    /// </summary>
    public double MinSpeechSec { get; set; } = 10d;

    /// <summary>
    /// Bảng ngưỡng theo <c>silence_ratio</c> — TÍN HIỆU CHÍNH. Rỗng ⇒ dùng
    /// <see cref="DefaultSilenceBands"/>.
    ///
    /// <para>⚠ CỐ Ý không đặt giá trị mặc định thẳng vào property: <c>ConfigurationBinder</c> bind
    /// mảng vào <c>List&lt;T&gt;</c> bằng cách CỘNG THÊM vào phần tử sẵn có, nên khai mặc định ở đây
    /// rồi override bằng appsettings sẽ ra bảng gồm CẢ HAI bộ — và bảng đó vẫn "chạy", chỉ là chấm
    /// bằng ngưỡng không ai chọn.</para>
    /// </summary>
    public List<SilenceBand> SilenceBands { get; set; } = [];

    /// <summary>
    /// Bảng mặc định: mỗi mốc là một PHÂN VỊ đo được, cắt từ thấp lên cao (im lặng ít = trôi chảy).
    /// <list type="bullet">
    ///   <item>≤ 0,036 (p25) → 5</item>
    ///   <item>≤ 0,144 (p50) → 4</item>
    ///   <item>≤ 0,254 (p75) → 3</item>
    ///   <item>≤ 0,359 (p90) → 2</item>
    ///   <item>còn lại (&gt; p90) → 1</item>
    /// </list>
    /// Bậc cuối để <c>1,0</c> vì <c>silence_ratio</c> theo định nghĩa nằm trong [0,1] ⇒ luôn có bậc
    /// khớp, không cần giá trị canh biên đặc biệt.
    /// </summary>
    public static IReadOnlyList<SilenceBand> DefaultSilenceBands { get; } =
    [
        new() { MaxSilenceRatio = 0.036d, Score = 5 },
        new() { MaxSilenceRatio = 0.144d, Score = 4 },
        new() { MaxSilenceRatio = 0.254d, Score = 3 },
        new() { MaxSilenceRatio = 0.359d, Score = 2 },
        new() { MaxSilenceRatio = 1.0d,   Score = 1 },
    ];

    /// <summary>
    /// TÍN HIỆU PHỤ — trừ điểm khi số lần dừng đạt ngưỡng này. Mặc định 4 = p90.
    ///
    /// <para>Vì sao chỉ dùng ở ĐUÔI TRÊN: <c>pause_count</c> có trung vị = 0 (p10/p25/p50 đều 0) nên
    /// ở nửa dưới nó KHÔNG phân biệt được ai với ai — thưởng cho "0 lần dừng" là thưởng cho phần
    /// phân bố mà thước đo mù, và lặp đúng sai lầm đã ghi cho <c>fillerCount</c> ("0 từ đệm" không
    /// phải lời khen). Chỉ từ p75=3 trở lên nó mới mang thông tin, và thông tin đó KHÁC với
    /// <c>silence_ratio</c>: cùng một tổng thời gian im lặng, vỡ ra thành nhiều quãng ngắn là nói
    /// nhát gừng, dồn vào một quãng dài thì không.</para>
    ///
    /// <para><c>null</c> hoặc ≤ 0 ⇒ TẮT hẳn tín hiệu phụ, chấm thuần <c>silence_ratio</c>.</para>
    /// </summary>
    public int? PausePenaltyFrom { get; set; } = 4;

    /// <summary>Số bậc bị trừ khi chạm <see cref="PausePenaltyFrom"/>.</summary>
    public int PausePenalty { get; set; } = 1;

    /// <summary>
    /// SÀN điểm (thang bậc) sau khi trừ. Mặc định 1, KHÔNG phải 0 — và đó là quyết định có chủ đích:
    /// trên thước này <b>0 để dành cho "không có gì để đo"</b>, mà ca đó đã được diễn đạt bằng cách
    /// LOẠI tiêu chí khỏi điểm. Người nói đủ <see cref="MinSpeechSec"/> giây thì có nhịp nói để đo,
    /// dù tệ ⇒ bậc thấp nhất là 1. Hai luật vì thế kể cùng một câu chuyện thay vì đá nhau.
    /// </summary>
    public int MinBand { get; set; } = 1;

    /// <summary>Bảng đang có hiệu lực, đã sắp tăng dần theo ngưỡng.</summary>
    public IReadOnlyList<SilenceBand> EffectiveSilenceBands()
        => (SilenceBands.Count > 0 ? SilenceBands : (IEnumerable<SilenceBand>)DefaultSilenceBands)
            .OrderBy(b => b.MaxSilenceRatio)
            .ToList();
}

/// <summary>Một bậc của bảng ngưỡng: <c>silence_ratio ≤ MaxSilenceRatio</c> ⇒ <c>Score</c>.</summary>
public class SilenceBand
{
    public double MaxSilenceRatio { get; set; }
    public int Score { get; set; }
}
