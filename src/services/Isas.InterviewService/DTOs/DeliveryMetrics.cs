using System.Text.Json;
using Isas.InterviewService.Entities;

namespace Isas.InterviewService.DTOs;

/// <summary>
/// F11 (FR06) — chỉ số CÁCH NÓI đo từ audio (AIService <c>app/fluency.py</c>). Từ 2026-08-05
/// mốc thời gian lấy từ VAD chứ không phải biên segment Whisper — xem <see cref="MetricsVersion"/>.
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
    // ⚠ MỌI field số đều NULLABLE, và đó là điểm mấu chốt của bản vá 2026-07-19.
    //
    // Trước đây chúng non-nullable nên `Read()` phải `?? 0` từng field ⇒ "chưa đo được" và
    // "đo ra 0" đè lên nhau, chỉ còn phân biệt được khuyết ở mức CẢ CỤM. Với 4 field không hề
    // có cột lưu, `?? 0` biến khuyết-vĩnh-viễn thành số 0 trông như số đo thật — rồi số 0 đó
    // đi thẳng vào prompt chấm dưới nhãn "số liệu thật". null bây giờ đi được suốt từ DB ra
    // tới cả client lẫn prompt, và mỗi bên tự nói "chưa đo được" thay vì bịa một con số.
    public double? AudioSec { get; set; }
    public double? SpeechSec { get; set; }
    public int? WordCount { get; set; }

    /// <summary>Âm tiết/phút — tiếng Việt đơn âm tiết nên đây là nhịp nói, KHÔNG so trực tiếp
    /// được với "words per minute" của tiếng Anh.</summary>
    public double? SpeechRateWpm { get; set; }

    public double? LongestPauseSec { get; set; }
    public int? PauseCount { get; set; }
    public double? SilenceRatio { get; set; }
    public int? FillerCount { get; set; }
    public double? FillerPer100Words { get; set; }

    /// <summary>Từ đệm nào, mấy lần — để hiện "bạn nói 'ừm' 12 lần" thay vì chỉ một con số.</summary>
    public Dictionary<string, int> FillerBreakdown { get; set; } = [];

    /// <summary>
    /// Phiên bản THƯỚC ĐO đã sinh ra bộ số này (AIService <c>fluency.DELIVERY_METRICS_VERSION</c>).
    /// <c>1</c> = mốc thời gian lấy từ biên segment Whisper · <c>2</c> = lấy từ vùng tiếng nói VAD.
    ///
    /// <para>Cần thiết vì điểm chấm được đem SO SÁNH với nhau (xếp hạng B2B — CAMP-10; đo cải
    /// thiện của roadmap — BC15). Đổi cách đo giữa chừng mà không đánh dấu thì hai con số sinh
    /// ra từ hai thước khác nhau vẫn bị đặt cạnh nhau như thể cùng đơn vị.</para>
    ///
    /// <para>⚠ <b><c>null</c> ở đây nghĩa là "đo bằng thước cũ"</b>, KHÁC với quy ước của BK23
    /// (<c>prompt_version</c>) nơi <c>null</c> phải giữ nghĩa "không biết". Ở đây suy luận đó an
    /// toàn vì cột chỉ tồn tại từ bản vá 2026-08-05 trở đi, và mọi lượt đo từ đó đều đóng dấu —
    /// nên khuyết dấu chỉ có đúng một nguyên nhân. Đừng áp ngược tiền lệ này sang cột khác.</para>
    /// </summary>
    public int? MetricsVersion { get; set; }
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

        // Vá F11 — 4 field này TRƯỚC ĐÂY BỊ BỎ QUÊN ở đây. Chúng có trong DTO nhận từ AIService
        // nhưng không được ghi xuống cột nào, nên `Read()` không thể dựng lại và trả về 0.
        answer.AudioSec = metrics?.AudioSec;
        answer.SpeechSec = metrics?.SpeechSec;
        answer.WordCount = metrics?.WordCount;
        answer.FillerPer100Words = metrics?.FillerPer100Words;
        answer.MetricsVersion = metrics?.MetricsVersion;

        answer.FillerBreakdown = metrics is null || metrics.FillerBreakdown.Count == 0
            ? null
            : JsonSerializer.Serialize(metrics.FillerBreakdown, Json);
    }

    /// <summary>Đọc ngược từ answer để đẩy lại vào <c>ScoringJob</c> khi republish (đường
    /// republisher không gọi lại AIService nên phải lấy bản đã lưu).
    /// null khi answer chưa từng đo được — KHÁC với "đo ra 0".</summary>
    public static DeliveryMetricsDto? Read(PracticeAnswer answer) => Read(
        answer.SpeechRateWpm, answer.FillerCount, answer.PauseCount,
        answer.LongestPauseSec, answer.SilenceRatio, answer.FillerBreakdown,
        answer.AudioSec, answer.SpeechSec, answer.WordCount, answer.FillerPer100Words,
        answer.MetricsVersion);

    /// <summary>Bản nhận từng giá trị — cho call site đọc bằng <c>.Select(...)</c> projection
    /// (StuckAnswerRepublisher) không có entity đầy đủ trong tay.
    ///
    /// <para>4 tham số cuối có default <c>null</c> để call site cũ không phải sửa; nhưng call
    /// site nào đẩy job CHẤM thì PHẢI truyền đủ — thiếu là prompt chấm mất số đo thời gian,
    /// đúng lỗi bản vá 2026-07-19 đang bịt.</para></summary>
    public static DeliveryMetricsDto? Read(
        double? speechRateWpm, int? fillerCount, int? pauseCount,
        double? longestPauseSec, double? silenceRatio, string? fillerBreakdownJson,
        double? audioSec = null, double? speechSec = null,
        int? wordCount = null, double? fillerPer100Words = null,
        int? metricsVersion = null)
    {
        // "Chưa từng đo được" = KHÔNG có số nào. Giữ ngữ nghĩa cả-cụm cho giá trị trả về null
        // (worker nhận null → tự transcribe rồi tự đo), nhưng từ đây trở xuống KHÔNG bịa 0 cho
        // field lẻ nào nữa: khuyết field nào thì field đó ra null.
        //
        // ⚠ `metricsVersion` CỐ Ý KHÔNG nằm trong điều kiện này. Nó là con dấu MÔ TẢ bộ số, tự
        // nó không phải một số đo — đưa vào đây thì một answer chưa từng đo được nhưng lỡ có
        // dấu sẽ trả về DTO rỗng toàn null thay vì `null`, và worker sẽ tưởng "đã đo rồi" nên
        // bỏ qua bước tự transcribe.
        if (speechRateWpm is null && fillerCount is null && pauseCount is null
            && longestPauseSec is null && silenceRatio is null
            && audioSec is null && speechSec is null
            && wordCount is null && fillerPer100Words is null)
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
            // KHÔNG `?? 0` — xem chú thích đầu DeliveryMetricsDto. Field khuyết đi ra ngoài
            // dưới dạng null để client hiện "chưa đo được" và prompt chấm nói thẳng là thiếu,
            // thay vì cả hai cùng đọc một số 0 do ta bịa ra.
            SpeechRateWpm = speechRateWpm,
            FillerCount = fillerCount,
            PauseCount = pauseCount,
            LongestPauseSec = longestPauseSec,
            SilenceRatio = silenceRatio,
            AudioSec = audioSec,
            SpeechSec = speechSec,
            WordCount = wordCount,
            FillerPer100Words = fillerPer100Words,
            FillerBreakdown = breakdown ?? [],
            MetricsVersion = metricsVersion,
        };
    }
}
