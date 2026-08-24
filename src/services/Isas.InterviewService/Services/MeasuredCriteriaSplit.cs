using Isas.InterviewService.Entities;
using Isas.InterviewService.Enums;

namespace Isas.InterviewService.Services;

/// <summary>
/// Tách bộ tiêu chí của MỘT lượt chấm thành phần gửi cho LLM và phần hệ TỰ TÍNH từ số đo.
///
/// <para><b>Vì sao là helper dùng chung:</b> có ĐÚNG HAI đường đẩy job chấm — <see cref="AnswerService"/>
/// (publish lúc upload) và <see cref="StuckAnswerRepublisher"/> (đẩy lại khi kẹt). Hai đường lệch
/// luật là lỗi chỉ lộ ra KHI ĐÃ CÓ SỰ CỐ: answer nào phải nhờ republisher cứu sẽ được chấm bằng
/// thước khác answer chạy trơn tru, và không có gì báo. Chính cặp đường này đã dính đúng lỗi đó ở
/// F11, rồi lại được nhắc ở kill-switch đáp án mẫu và ở cờ dải mặc định. Đây là lần thứ tư.</para>
/// </summary>
public static class MeasuredCriteriaSplit
{
    /// <summary>
    /// Bộ tiêu chí GỬI CHO LLM = bỏ những tiêu chí chấm bằng số đo.
    ///
    /// <para>Phải bỏ THẬT chứ không chỉ lờ điểm trả về: để nguyên thì LLM vẫn chấm, vẫn trả điểm, và
    /// hệ có HAI con số cho cùng một tiêu chí — hai nguồn cãi nhau là thứ tệ hơn cả cái bug đang sửa.
    /// Bỏ khỏi prompt còn đổi cả bối cảnh chấm các tiêu chí còn lại theo hướng đúng: LLM thôi phải
    /// phán về thứ nó không có dữ liệu.</para>
    ///
    /// <para><b>LÙI AN TOÀN:</b> lọc xong mà RỖNG ⇒ trả nguyên bộ. Bộ rỗng KHÔNG vô hại — cả hai
    /// caller đều bỏ publish khi không có tiêu chí ⇒ answer không bao giờ được chấm ⇒ buổi không bao
    /// giờ đóng ⇒ người luyện mất 1 credit (PAY-13), im lặng. Ca này với tới được thật: rubric riêng
    /// BC16 chỉ gồm đúng một tiêu chí trùng tên bộ chuẩn. Lượt chấm "thừa" đó vô hại vì callback
    /// (<see cref="AnswerService.SaveResultAsync"/>) vẫn BỎ mọi điểm LLM trả cho tiêu chí đo được —
    /// nó chỉ tồn tại để bảo đảm có một callback về mà chốt sổ answer.</para>
    /// </summary>
    public static List<RubricCriterion> ForAi(IReadOnlyList<RubricCriterion> criteria, ILogger? logger = null, Guid? answerId = null)
    {
        var ai = criteria.Where(c => c.ScoringMethod == CriterionScoringMethod.Ai).ToList();
        if (ai.Count == criteria.Count) return ai;

        if (ai.Count == 0)
        {
            logger?.LogWarning(
                "Answer {AnswerId}: MỌI tiêu chí của rubric đều chấm bằng số đo — vẫn gửi nguyên bộ cho "
                + "LLM để có callback chốt sổ (điểm LLM trả về cho chúng sẽ bị bỏ ở callback)", answerId);
            return criteria.ToList();
        }

        logger?.LogInformation(
            "Answer {AnswerId}: {Ai}/{Total} tiêu chí gửi LLM, {Measured} tiêu chí chấm bằng số đo",
            answerId, ai.Count, criteria.Count, criteria.Count - ai.Count);
        return ai;
    }
}
