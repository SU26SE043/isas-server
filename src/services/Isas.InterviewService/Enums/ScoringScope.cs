namespace Isas.InterviewService.Enums;

/// <summary>
/// Phạm vi chấm của một tiêu chí: tiêu chí này được chấm cho MỌI câu trả lời, hay chỉ cho những
/// câu hỏi thực sự nhắm tới nó.
///
/// <para><b>Vì sao cần:</b> trước đây mọi câu trả lời bị chấm trên TOÀN BỘ rubric, nên một câu hỏi
/// hẹp ("giải thích cơ chế xoay vòng refresh token") vẫn bị chấm tiêu chí "Thiết kế hệ thống &amp;
/// CSDL" và ăn điểm thấp <i>chỉ vì không được hỏi</i>. Đo trên deploy: cùng trình độ, bài trả lời
/// câu hỏi hẹp ~69/100 còn bài "đại luận" 91–97.</para>
///
/// <para><b>Mặc định là <see cref="Always"/> — CÓ CHỦ ĐÍCH.</b> Mọi tiêu chí không được phân loại
/// tường minh (rubric riêng của candidate — BC16, tiêu chí campaign B2B, row cũ trong DB) giữ
/// NGUYÊN hành vi hôm nay. Chiều mặc định an toàn là "chấm thừa", không phải "bỏ chấm": bỏ chấm
/// nhầm = tiêu chí biến mất khỏi kết quả mà không ai thấy.</para>
/// </summary>
public enum ScoringScope
{
    /// <summary>
    /// Luôn chấm, bất kể câu hỏi hỏi gì — dành cho tiêu chí về CÁCH NÓI (giao tiếp, trôi chảy,
    /// ngữ pháp/dùng từ, thuật ngữ). Đây là thứ quan sát được ở BẤT KỲ câu trả lời nào.
    /// </summary>
    Always = 0,

    /// <summary>
    /// Chỉ chấm khi câu hỏi nhắm tới tiêu chí này (<c>practice_questions.target_criterion_ids</c>)
    /// — dành cho tiêu chí NỘI DUNG chuyên môn. Câu hỏi không nhắm tới ⇒ tiêu chí không được gửi
    /// vào lượt chấm ⇒ không có <c>answer_scores</c> ⇒ không kéo điểm xuống.
    /// </summary>
    WhenTargeted = 1
}
