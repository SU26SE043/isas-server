using Isas.InterviewService.Entities;
using Isas.InterviewService.Enums;

namespace Isas.InterviewService.Services;

/// <summary>
/// Chọn bộ tiêu chí gửi vào MỘT lượt chấm: 4 tiêu chí CÁCH NÓI
/// (<see cref="ScoringScope.Always"/>) luôn có mặt, cộng đúng những tiêu chí NỘI DUNG mà câu hỏi
/// nhắm tới (<c>practice_questions.target_criterion_ids</c>).
///
/// <para><b>Vì sao tách thành helper dùng chung:</b> có ĐÚNG HAI đường đẩy job chấm —
/// <see cref="AnswerService"/> (publish lúc upload) và <see cref="StuckAnswerRepublisher"/>
/// (đẩy lại khi kẹt). Hai đường lệch luật là lỗi chỉ lộ ra khi có sự cố: answer nào phải nhờ
/// republisher cứu sẽ được chấm theo luật KHÁC answer chạy trơn tru, mà không có gì báo. Đúng
/// hạng lỗi F11 đã dính ở chính cặp đường này.</para>
///
/// <para><b>INT-9 không phải sửa:</b> guard "LLM chấm thiếu tiêu chí" phía AIService so với danh
/// sách tiêu chí ĐƯỢC GỬI VÀO, không phải toàn rubric ⇒ gửi ít hơn vẫn hợp lệ.</para>
/// </summary>
public static class ScoringScopeFilter
{
    /// <summary>
    /// Lọc <paramref name="criteria"/> theo nhãn của câu hỏi. <b><c>null</c> và <c>[]</c> là HAI ca
    /// khác nhau</b> — xem <see cref="Entities.PracticeQuestion.TargetCriterionIds"/>:
    ///
    /// <list type="bullet">
    ///   <item><c>null</c> = chưa hỏi / không đủ tin ⇒ trả NGUYÊN bộ tiêu chí (lùi an toàn, y như
    ///   trước thay đổi này).</item>
    ///   <item><c>[]</c> = đã hỏi, câu này không nhắm tiêu chí NỘI DUNG nào ⇒ chỉ giữ tiêu chí
    ///   <see cref="ScoringScope.Always"/>. Gộp ca này vào <c>null</c> sẽ làm tính năng vô hiệu đúng
    ///   ở nhóm câu cần nó nhất (câu xã giao vẫn bị chấm "Thiết kế hệ thống &amp; CSDL").</item>
    ///   <item>non-empty ⇒ tiêu chí <c>Always</c> + đúng những tiêu chí được nhắm tới.</item>
    /// </list>
    ///
    /// <para>LÙI AN TOÀN cuối: lọc xong mà rỗng ⇒ trả nguyên bộ. Bộ rỗng KHÔNG vô hại — cả hai caller
    /// đều bỏ publish khi không có tiêu chí ⇒ answer không bao giờ được chấm ⇒ buổi không bao giờ đóng
    /// ⇒ người luyện mất 1 credit (PAY-13). Ca này với tới được khi rubric riêng (BC16) bị sửa GIỮA
    /// BUỔI (bản cũ deactivate, id trong nhãn trỏ vào tiêu chí không còn active) hoặc khi rubric không
    /// có tiêu chí <c>Always</c> nào.</para>
    /// </summary>
    public static List<RubricCriterion> Apply(
        IReadOnlyList<RubricCriterion> criteria,
        IReadOnlyList<Guid>? targetCriterionIds,
        ILogger? logger = null,
        Guid? answerId = null)
    {
        if (targetCriterionIds is null)
            return criteria.ToList();

        var targeted = targetCriterionIds.ToHashSet();
        var scoped = criteria
            .Where(c => c.ScoringScope == ScoringScope.Always || targeted.Contains(c.Id))
            .ToList();

        if (scoped.Count == 0)
        {
            logger?.LogWarning(
                "Phạm vi chấm lọc ra 0 tiêu chí cho answer {AnswerId} (nhãn {Targeted} không khớp tiêu chí "
                + "active nào và rubric không có tiêu chí Always) — chấm ĐỦ rubric để không bỏ rơi answer",
                answerId, string.Join(",", targetCriterionIds));
            return criteria.ToList();
        }

        return scoped;
    }
}
