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

    /// <summary>
    /// Con dấu BỘ NGƯỠNG đã sinh ra dòng điểm này, khi tiêu chí được chấm bằng SỐ ĐO
    /// (<see cref="Enums.CriterionScoringMethod.DeliveryMetrics"/>) thay vì bằng LLM.
    ///
    /// <para>Vì sao cần: điểm do số đo và điểm do LLM chấm trước đó <b>không so sánh được với
    /// nhau</b> — cùng một bản ghi từng nhận 0%/40%/60% tuỳ câu hỏi, nay nhận một con số cố định.
    /// Mà điểm vẫn bị đem so ở đo tiến bộ roadmap (BC15) và mốc peer (F14). Cùng lý do tồn tại của
    /// <c>metrics_version</c> / <c>scoring_scope_version</c> / <c>screening_version</c>.</para>
    ///
    /// <para>⚠ <b><c>null</c> = KHÔNG BIẾT</b> (dòng do LLM chấm, hoặc ghi trước cột này) — TUYỆT
    /// ĐỐI không suy ra "phiên bản khác" từ nó, đó là bịa từ chỗ không biết (BK23). Chiều dùng
    /// được là chiều ngược lại: dòng có giá trị thì CHẮC CHẮN do số đo sinh ra, nên
    /// <c>delivery_scoring_version IS NOT NULL</c> là cách nhận diện đáng tin.</para>
    ///
    /// <para>Đóng dấu PER-ROW chứ không per-answer, cùng lý do với <see cref="PromptVersion"/>:
    /// một answer có N attempt (E10) và republisher có thể bù attempt sau một lần deploy đổi ngưỡng.</para>
    /// </summary>
    public int? DeliveryScoringVersion { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}