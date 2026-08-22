using System.Globalization;
using Isas.InterviewService.DTOs;
using Isas.InterviewService.Models;

namespace Isas.InterviewService.Services;

/// <summary>Kết quả chấm bằng số đo — <c>null</c> ở call site nghĩa là LOẠI tiêu chí, không phải 0 điểm.</summary>
/// <param name="Score">Điểm trên thang THẬT của tiêu chí (đã quy từ thang bậc theo <c>maxScore</c>).</param>
/// <param name="Band">Bậc trên thang <see cref="DeliveryScoringOptions.BandScale"/> — để nêu trong nhận xét.</param>
public readonly record struct DeliveryFluencyResult(decimal Score, int Band, string Reasoning);

/// <summary>
/// Chấm tiêu chí CÁCH NÓI từ SỐ ĐO của chính bản ghi, KHÔNG qua LLM.
///
/// <para><b>Vì sao tồn tại</b> — đo trên production: nộp CÙNG MỘT file ghi âm cho 4 câu hỏi khác
/// nhau, tiêu chí "Độ trôi chảy &amp; tự tin" nhận <b>0% · 40% · 60%</b>, trong khi số đo VAD ra y
/// hệt nhau cả 4 lần. Độ trôi chảy là thuộc tính của BẢN GHI — nó không đổi theo câu hỏi. Chấm nội
/// dung đang lây sang chấm cách nói. Và không sửa được bằng prompt: nửa định nghĩa của tiêu chí là
/// tín hiệu THỜI GIAN, thứ transcript không mang.</para>
///
/// <para><b>Bất biến trung tâm (có test khoá):</b> cùng một bộ số đo ⇒ LUÔN cùng một điểm. Hàm này
/// thuần tuý, không nhận câu hỏi, không nhận transcript, không nhận nội dung — nên câu trả lời lệch
/// đề KHÔNG thể kéo tiêu chí này xuống nữa.</para>
///
/// <para><b>Tín hiệu nào được dùng, và vì sao:</b></para>
/// <list type="number">
///   <item><c>silence_ratio</c> — CHÍNH. Phân bố trải đều trên cả 5 phân vị (0,002 → 0,359) nên nó
///   phân biệt được; và nó là TỈ LỆ THỜI GIAN thuần, không phụ thuộc ngôn ngữ.</item>
///   <item><c>pause_count</c> — PHỤ, chỉ ở đuôi trên (xem <see cref="DeliveryScoringOptions.PausePenaltyFrom"/>).</item>
///   <item><c>speech_rate_wpm</c> — <b>KHÔNG dùng để tính điểm</b>, chỉ nêu làm bối cảnh trong nhận
///   xét. Hai lý do độc lập, mỗi lý do đủ để loại: (a) nó HAI CHIỀU (quá chậm và quá nhanh đều xấu)
///   nên cần một "dải bình thường", mà dải duy nhất hệ đang có (180–320 vi / 120–180 en trong
///   <c>prompts.py</c>) CHƯA AI HIỆU CHUẨN và mâu thuẫn với chính số đo — p25 đo được là 169, tức
///   1/4 số người sẽ bị coi là "dưới bình thường", và một giọng đọc TTS đều đặn từng bị gắn "nói
///   nhanh" ở 186. Đem một dải chưa kiểm chứng đi trừ điểm là hợp thức hoá giả định thành con số.
///   (b) ĐƠN VỊ KHÔNG ĐỒNG NHẤT giữa hai ngôn ngữ — tiếng Việt đếm ÂM TIẾT/phút, tiếng Anh đếm
///   TỪ/phút (xem <see cref="DeliveryMetricsDto.SpeechRateWpm"/>), nên một ngưỡng chung sẽ sai ở ít
///   nhất một ngôn ngữ. Bộ phân vị dẫn ở options cũng trộn cả hai ⇒ tự nó không phân xử được.</item>
///   <item><c>filler_count</c> — KHÔNG dùng: chính repo đã ghi nó là mức TỐI THIỂU (ASR nuốt bớt từ
///   đệm), nên nó nói dối theo chiều KHEN. Phần ngập ngừng bị nuốt vẫn hiện ra ở thời gian im lặng,
///   tức <c>silence_ratio</c> đã bắt hộ.</item>
/// </list>
/// </summary>
public static class DeliveryFluencyScorer
{
    /// <summary>
    /// <c>null</c> ⇒ KHÔNG chấm được ⇒ call site phải LOẠI tiêu chí khỏi điểm (không ghi dòng
    /// <c>answer_scores</c>), KHÔNG được cho 0.
    ///
    /// <para>Cho 0 là phạt người học vì thứ không thuộc về họ — cùng luật với INT-18 ("tiêu chí
    /// không ai hỏi thì loại khỏi điểm"). Ở B2C điểm tổng là TRUNG BÌNH CỘNG (INT-10) nên một số 0
    /// bịa vào đây kéo tụt cả buổi khoảng 20% mà không có dòng nào giải thích tại sao.</para>
    /// </summary>
    public static DeliveryFluencyResult? Score(
        DeliveryMetricsDto? metrics, int maxScore, string? language, DeliveryScoringOptions options)
    {
        if (!options.Enabled) return null;

        // maxScore méo (0 hoặc âm) ⇒ không có thang nào để quy về. Loại, đừng đoán.
        if (maxScore < 1) return null;

        // Thiếu TÍN HIỆU CHÍNH ⇒ loại. Đây là ca thường trực chứ không phải ngoại lệ: đường degrade
        // (adaptive lỗi) và answer ghi trước F11 đều không có số đo nào.
        if (metrics?.SilenceRatio is not { } silenceRatio) return null;

        // Dưới sàn thời lượng ⇒ loại. `speech_sec` khuyết cũng loại: không có mẫu số thì không biết
        // tỉ lệ im lặng được tính trên bao nhiêu giây, mà tỉ lệ trên mẫu dưới một giây thì vô nghĩa.
        if (metrics.SpeechSec is not { } speechSec || speechSec < options.MinSpeechSec) return null;

        // Số đo hỏng (âm / vượt 1) — không thể tin thước đo nữa. Kẹp về một bậc nào đó là bịa.
        if (silenceRatio is < 0d or > 1d) return null;

        var bands = options.EffectiveSilenceBands();
        if (bands.Count == 0) return null;   // cấu hình rỗng ⇒ thà không chấm còn hơn chấm bừa

        // Bậc đầu tiên mà silence_ratio còn nằm dưới ngưỡng. Bảng đã sắp tăng dần; bậc cuối phủ 1,0
        // nên vòng này luôn tìm được — `Last()` chỉ là lưới an toàn khi ai đó cấu hình bảng cụt.
        var band = bands.FirstOrDefault(b => silenceRatio <= b.MaxSilenceRatio)?.Score
                   ?? bands[^1].Score;

        var pauseCount = metrics.PauseCount;
        var penalised = options.PausePenaltyFrom is { } from && from > 0
                        && pauseCount is { } pc && pc >= from;
        if (penalised) band -= options.PausePenalty;

        band = Math.Clamp(band, options.MinBand, DeliveryScoringOptions.BandScale);

        // Quy từ thang bậc về thang THẬT của tiêu chí. maxScore = BandScale (bộ chuẩn B2C = 5) ⇒
        // phép nhân/chia triệt tiêu, ra đúng con số bậc.
        var score = Math.Round(
            (decimal)band / DeliveryScoringOptions.BandScale * maxScore,
            MidpointRounding.AwayFromZero);
        score = Math.Clamp(score, 0m, maxScore);

        return new DeliveryFluencyResult(score, band,
            BuildReasoning(silenceRatio, pauseCount, metrics.SpeechRateWpm, speechSec,
                band, penalised, language));
    }

    /// <summary>
    /// Nhận xét nêu ĐÚNG những gì đã dùng. Ba ràng buộc, mỗi cái ứng một cách hỏng đã có tiền lệ:
    /// <list type="bullet">
    ///   <item>KHÔNG rỗng — E11 gắn <c>needs_review</c> cho nhận xét rỗng/quá ngắn; một tiêu chí
    ///   chấm-bằng-máy mà kéo cả answer vào diện soi lại là báo động giả có hệ thống.</item>
    ///   <item>KHÔNG được ngụ ý <c>speech_rate</c> đã tác động lên điểm — nó chỉ là bối cảnh.</item>
    ///   <item>KHÔNG được gọi ngưỡng là chuẩn ngành (bài học F14: nhãn phải nói đúng NGUỒN).</item>
    /// </list>
    /// </summary>
    private static string BuildReasoning(
        double silenceRatio, int? pauseCount, double? speechRateWpm, double speechSec,
        int band, bool penalised, string? language)
    {
        var en = string.Equals(language?.Trim(), "en", StringComparison.OrdinalIgnoreCase);
        var c = CultureInfo.InvariantCulture;

        var ratio = silenceRatio.ToString("0.###", c);
        var secs = speechSec.ToString("0.#", c);
        var rate = speechRateWpm?.ToString("0", c);

        if (en)
        {
            var s = $"Scored from acoustic measurements, not from the transcript: silence ratio {ratio} "
                  + $"over {secs}s of speech maps to band {band}/{DeliveryScoringOptions.BandScale} "
                  + "on the internal threshold table.";
            if (pauseCount is { } p)
                s += penalised
                    ? $" {p} significant pauses (at or above the internal cut-off) lowered it by one band."
                    : $" Significant pauses: {p}.";
            if (rate is not null)
                s += $" Speaking rate {rate}/min is reported for context only and did not affect this score.";
            return s + " Thresholds are percentiles of this platform's own recordings, not an industry standard.";
        }

        var t = $"Chấm bằng SỐ ĐO từ bản ghi âm, không qua bản chép lời: tỉ lệ im lặng {ratio} "
              + $"trên {secs}s nói, rơi vào bậc {band}/{DeliveryScoringOptions.BandScale} của bảng ngưỡng nội bộ.";
        if (pauseCount is { } pv)
            t += penalised
                ? $" Có {pv} lần dừng đáng kể (chạm ngưỡng nội bộ) nên bị hạ một bậc."
                : $" Số lần dừng đáng kể: {pv}.";
        if (rate is not null)
            t += $" Tốc độ nói {rate}/phút chỉ nêu làm bối cảnh, KHÔNG tham gia tính điểm này.";
        return t + " Ngưỡng cắt theo phân vị trên chính dữ liệu của hệ thống, không phải chuẩn ngành.";
    }
}
