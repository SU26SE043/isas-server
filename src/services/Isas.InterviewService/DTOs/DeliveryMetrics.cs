using System.Text.Json;
using Isas.InterviewService.Entities;

namespace Isas.InterviewService.DTOs;

/// <summary>
/// F11 (FR06) — chỉ số CÁCH NÓI đo từ mốc thời gian Whisper (AIService <c>app/fluency.py</c>).
///
/// <para>Trước F11 mọi tín hiệu âm thanh bị vứt ngay sau khi transcribe: chỉ text đi tiếp, nên
/// "độ trôi chảy" chỉ có thể ĐOÁN từ chữ. Nay số đo đi kèm transcript suốt cả 2 đường
/// (tĩnh: worker tự transcribe · thích ứng: <c>/decide-next</c> đo rồi gửi kèm).</para>
///
/// <para>⚠ <b><see cref="FillerCount"/> là mức TỐI THIỂU, không phải số thật.</b> Whisper học
/// trên transcript đã làm sạch nên nó thường nuốt bớt từ đệm ⇒ đếm hụt. Chỉ số THỜI GIAN
/// (<see cref="LongestPauseSec"/>/<see cref="SilenceRatio"/>/<see cref="SpeechRateWpm"/>) đáng
/// tin hơn: tiếng "ừm" bị nuốt vẫn chiếm thời gian thật nên vẫn lộ ra ở đó. Đừng hiển thị
/// "0 từ đệm" như một lời khen.</para>
///
/// Mọi field nullable/có default: worker hoặc AIService bản CŨ không gửi → không được làm hỏng
/// lượt chấm (answer <c>Failed</c> = người luyện mất credit, PAY-13).
/// </summary>
public class DeliveryMetricsDto
{
    public double AudioSec { get; set; }
    public double SpeechSec { get; set; }
    public int WordCount { get; set; }

    /// <summary>Âm tiết/phút — tiếng Việt đơn âm tiết nên đây là nhịp nói, KHÔNG so trực tiếp
    /// được với "words per minute" của tiếng Anh.</summary>
    public double SpeechRateWpm { get; set; }

    public double LongestPauseSec { get; set; }
    public int PauseCount { get; set; }
    public double SilenceRatio { get; set; }
    public int FillerCount { get; set; }
    public double FillerPer100Words { get; set; }

    /// <summary>Từ đệm nào, mấy lần — để hiện "bạn nói 'ừm' 12 lần" thay vì chỉ một con số.</summary>
    public Dictionary<string, int> FillerBreakdown { get; set; } = [];
}

/// <summary>
/// F11 — chuyển đổi giữa <see cref="DeliveryMetricsDto"/> (hợp đồng dây) và các cột trên
/// <see cref="PracticeAnswer"/>. Gom về MỘT chỗ vì có <b>ba</b> nơi ghi (callback chấm ·
/// vòng thích ứng · reset khi upload lại) — ba bản sao logic map là ba cơ hội lệch nhau.
/// </summary>
public static class DeliveryMetricsMapper
{
    // Từ đệm tiếng Việt có dấu. Mặc định `System.Text.Json` escape mọi ký tự non-ASCII, nên
    // {"ừm":3} sẽ nằm trong DB dưới dạng {"ừm":3} — đúng về mặt JSON nhưng không ai đọc nổi
    // khi soi DB lúc có sự cố. Nới encoder để cột lưu chữ tiếng Việt như người viết.
    // An toàn ở đây: khoá đến từ danh sách từ đệm CỐ ĐỊNH của AIService (không phải input người
    // dùng), và giá trị này không bao giờ được nhúng thẳng vào HTML — nó đi ra API dưới dạng
    // field JSON, nơi bộ serializer của ASP.NET tự escape lại theo ngữ cảnh của nó.
    private static readonly JsonSerializerOptions Json = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Ghi chỉ số lên answer. <paramref name="metrics"/> = null → XOÁ hết về null
    /// (dùng cho INT-3 reset khi thu âm lại: giữ lại là hiện chỉ số của bản ghi đã bị thay).</summary>
    public static void Apply(PracticeAnswer answer, DeliveryMetricsDto? metrics)
    {
        answer.SpeechRateWpm = metrics?.SpeechRateWpm;
        answer.FillerCount = metrics?.FillerCount;
        answer.PauseCount = metrics?.PauseCount;
        answer.LongestPauseSec = metrics?.LongestPauseSec;
        answer.SilenceRatio = metrics?.SilenceRatio;
        answer.FillerBreakdown = metrics is null || metrics.FillerBreakdown.Count == 0
            ? null
            : JsonSerializer.Serialize(metrics.FillerBreakdown, Json);
    }

    /// <summary>Đọc ngược từ answer để đẩy lại vào <c>ScoringJob</c> khi republish (đường
    /// republisher không gọi lại AIService nên phải lấy bản đã lưu).
    /// null khi answer chưa từng đo được — KHÁC với "đo ra 0".</summary>
    public static DeliveryMetricsDto? Read(PracticeAnswer answer) => Read(
        answer.SpeechRateWpm, answer.FillerCount, answer.PauseCount,
        answer.LongestPauseSec, answer.SilenceRatio, answer.FillerBreakdown);

    /// <summary>Bản nhận từng giá trị — cho call site đọc bằng <c>.Select(...)</c> projection
    /// (StuckAnswerRepublisher) không có entity đầy đủ trong tay.</summary>
    public static DeliveryMetricsDto? Read(
        double? speechRateWpm, int? fillerCount, int? pauseCount,
        double? longestPauseSec, double? silenceRatio, string? fillerBreakdownJson)
    {
        if (speechRateWpm is null && fillerCount is null && pauseCount is null
            && longestPauseSec is null && silenceRatio is null)
            return null;

        Dictionary<string, int>? breakdown = null;
        if (!string.IsNullOrWhiteSpace(fillerBreakdownJson))
        {
            // Dữ liệu tự ghi nên hỏng là chuyện lạ — nhưng JSON hỏng KHÔNG được phép làm nổ
            // đường chấm (answer Failed = mất credit, PAY-13). Hỏng → coi như không có chi tiết.
            try
            {
                breakdown = JsonSerializer.Deserialize<Dictionary<string, int>>(fillerBreakdownJson);
            }
            catch (JsonException)
            {
                breakdown = null;
            }
        }

        return new DeliveryMetricsDto
        {
            SpeechRateWpm = speechRateWpm ?? 0,
            FillerCount = fillerCount ?? 0,
            PauseCount = pauseCount ?? 0,
            LongestPauseSec = longestPauseSec ?? 0,
            SilenceRatio = silenceRatio ?? 0,
            FillerBreakdown = breakdown ?? [],
        };
    }
}
