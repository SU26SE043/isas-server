namespace Isas.InterviewService.Enums;

/// <summary>
/// CÁI GÌ sinh ra điểm của một tiêu chí. Lưu string (GEN-2).
///
/// <para>Vì sao cần: có tiêu chí <b>không chấm được từ bản chép lời</b>. Đo trên production —
/// nộp CÙNG MỘT file ghi âm cho 4 câu hỏi khác nhau, tiêu chí "Độ trôi chảy &amp; tự tin" nhận
/// <b>0% · 40% · 60%</b>, trong khi máy đo VAD ra y hệt nhau cả 4 lần (<c>pause_count=0</c>,
/// <c>silence_ratio=0,023</c>, <c>speech_rate_wpm=186</c>). Độ trôi chảy là thuộc tính của BẢN
/// GHI ÂM — nó không thể đổi theo câu hỏi. Tức chấm nội dung đang lây sang chấm cách nói: câu bị
/// coi là lệch đề thì bộ chấm zero luôn cả tiêu chí này.</para>
///
/// <para>Nửa định nghĩa của tiêu chí đó là tín hiệu THỜI GIAN (im lặng, ngập ngừng) — thứ văn bản
/// KHÔNG mang. Không có cách nào sửa prompt để bù lại một dữ kiện không có mặt.</para>
/// </summary>
public enum CriterionScoringMethod
{
    /// <summary>
    /// Do LLM chấm từ transcript. MẶC ĐỊNH ⇒ mọi row không được phân loại tường minh (tiêu chí
    /// campaign B2B, rubric riêng BC16 tự đặt tên, row có trước cột này) giữ nguyên hành vi cũ.
    /// </summary>
    Ai = 0,

    /// <summary>
    /// Tính từ <c>practice_answers</c> (số đo VAD — F11), KHÔNG gửi cho LLM.
    /// Xem <see cref="Services.DeliveryFluencyScorer"/>.
    /// </summary>
    DeliveryMetrics = 1,
}
