using Isas.InterviewService.Enums;

namespace Isas.InterviewService.Entities;

/// <summary>
/// Một lượt CHẤM THỬ bộ chuẩn B2C do admin chạy: AI viết 3 bài mẫu (yếu/khá/xuất sắc) cho một câu hỏi
/// rồi chấm chính chúng bằng thước đo đang lưu — để người soạn thấy "3 điểm khác 4 điểm ở chỗ nào"
/// TRƯỚC khi thước đó áp cho mọi người luyện tập.
///
/// <para><b>Vì sao bảng RIÊNG, không nới <c>rubric_preview_runs</c> của Campaign.</b> Ngoài ranh giới
/// DB-per-service (GEN-2), bảng bên đó có <c>campaign_id</c> NOT NULL + FK CASCADE + query filter join
/// <c>campaigns</c>, và khoá chống double-click của nó là UNIQUE trên <c>campaign_id</c> lọc
/// <c>status='Running'</c> — mà Postgres coi MỌI NULL là khác nhau, nên nới nullable sẽ làm khoá đó
/// <b>biến mất cho admin, không một triệu chứng nào</b>.</para>
///
/// <para><b>Vì sao lưu lịch sử thay vì trả rồi quên:</b> giá trị chính là so TRƯỚC/SAU khi sửa mốc, mà
/// muốn so trung thực thì phải biết hai lượt có cùng thước đo không ⇒ mỗi lượt đóng dấu
/// <see cref="RubricSnapshot"/> + <see cref="RubricFingerprint"/> + <see cref="PromptVersion"/>: cùng
/// vân tay mà điểm khác = nhiễu model; khác vân tay = đã đổi thước đo. Thiếu chúng thì mọi so sánh đều
/// là bịa, và người soạn sẽ quy thay đổi cho việc mình vừa sửa mốc trong khi thủ phạm có thể là ai đó
/// sửa prompt (F21) — đúng lý do BK23 tồn tại.</para>
/// </summary>
public class AdminRubricPreviewRun
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public JobCategory JobCategory { get; set; }
    public string Language { get; set; } = "vi";

    /// <summary>Phiên bản bộ chuẩn lúc chạy — quota tính theo từng phiên bản (thước mới = bài toán mới).</summary>
    public int RubricVersion { get; set; }

    public Guid CreatedByUserId { get; set; }

    /// <summary>
    /// Câu hỏi đem chấm thử. CỐ Ý không tham chiếu <c>practice_questions</c>: câu hỏi B2C thật được
    /// sinh từ CV/JD của chính người dùng nên chứa tên công ty/dự án của họ — hiện cho admin là RÒ RỈ
    /// dữ liệu. Nguồn hợp lệ chỉ có hai: bộ câu mẫu hằng số trong code, hoặc câu admin tự gõ.
    /// </summary>
    public string QuestionText { get; set; } = null!;

    public AdminRubricPreviewStatus Status { get; set; }

    /// <summary>Dạng chuẩn tắc của bộ thước đo đã dùng (JSON) — xem <c>RubricFingerprint</c>.</summary>
    public string RubricSnapshot { get; set; } = null!;

    /// <summary>SHA-256 hex của <see cref="RubricSnapshot"/>.</summary>
    public string RubricFingerprint { get; set; } = null!;

    /// <summary>Ba (hoặc bốn) bài mẫu + điểm từng tiêu chí, JSON.</summary>
    public string? Samples { get; set; }

    /// <summary>Bản prompt chấm (F21) lúc chạy — sửa prompt giữa hai lượt là đổi thước mà không đổi mốc.</summary>
    public int? PromptVersion { get; set; }

    /// <summary>
    /// Ba bài mẫu lệch nhau quá nhiều về ĐỘ DÀI. Không giấu: nếu bộ chấm cũng thưởng độ dài thì một
    /// dải điểm đẹp chỉ đang xác nhận một thước đo hỏng.
    /// </summary>
    public bool LengthParityWarning { get; set; }

    public string? ErrorReason { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
}
