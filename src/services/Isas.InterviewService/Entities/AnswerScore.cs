namespace Isas.InterviewService.Entities;

public class AnswerScore
{
    public Guid Id { get; set; } = Guid.NewGuid();
 
    public Guid AnswerId { get; set; }
    public PracticeAnswer Answer { get; set; } = null!;
 
    public Guid CriterionId { get; set; }
    public RubricCriterion Criterion { get; set; } = null!;
 
    // Mặc định 1 - mở đường self-consistency (chấm nhiều lần) sau này
    public int AttemptNo { get; set; } = 1;
 
    public decimal Score { get; set; }
    public string? Reasoning { get; set; }

    // E9 — mức khớp khi chấm neo theo rubric_levels (= Score khi neo). Null nếu tiêu chí
    // không có mức khai báo (dải mặc định) và worker không trả levelMatched.
    public int? LevelMatched { get; set; }

    // Điểm gắn với phiên bản rubric lúc chấm (sửa rubric không làm loạn điểm cũ)
    public int RubricVersion { get; set; }

    /// <summary>
    /// F21 — con dấu phiên bản PROMPT lúc chấm. null = chấm trước F21 (hoặc chưa ai tuỳ biến
    /// mảnh prompt nào).
    ///
    /// <para>Vì sao cần dù đã có <see cref="RubricVersion"/>: rubric quyết định chấm CÁI GÌ,
    /// prompt quyết định chấm NHƯ THẾ NÀO. Đổi prompt chấm là đổi THƯỚC ĐO — điểm trước và sau
    /// không còn so sánh trực tiếp được, mà hệ thống đang dùng điểm để xếp hạng ứng viên với
    /// nhau (CAMP-10/E4) và tính mức cải thiện theo thời gian (BC15). Không có con dấu này thì
    /// sau một lần admin sửa prompt, mọi so sánh lặng lẽ mất nghĩa mà không ai biết.</para>
    ///
    /// <para>⚠ Hiện mới LƯU, chưa chỗ nào cảnh báo khi bảng xếp hạng trộn hai giá trị khác nhau
    /// — backlog BK23. Lưu trước vì đây là vế KHÔNG hồi tố được: thiếu cột thì điểm lịch sử
    /// vĩnh viễn mất dấu đã chấm bằng prompt nào.</para>
    /// </summary>
    public int? PromptVersion { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}