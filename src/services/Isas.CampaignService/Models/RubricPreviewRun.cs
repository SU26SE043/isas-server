namespace Isas.CampaignService.Models
{
    /// <summary>
    /// CAMP-19 — một lượt CHẤM THỬ: AI viết 3 bài mẫu (yếu/khá/xuất sắc) cho một câu hỏi HR chọn rồi
    /// chấm chính chúng bằng thước đo đang lưu, để Employer nhìn thấy "6 điểm nghĩa là gì" TRƯỚC khi
    /// ứng viên thật vào thi.
    ///
    /// <para><b>Vì sao lưu lịch sử thay vì trả rồi quên.</b> Giá trị chính là so TRƯỚC/SAU khi sửa mốc.
    /// Muốn so trung thực thì phải biết hai lượt có cùng thước đo không — nên mỗi lượt đóng dấu
    /// <see cref="RubricSnapshot"/> + <see cref="RubricFingerprint"/> + <see cref="PromptVersion"/>:
    /// cùng vân tay mà điểm khác = nhiễu model; khác vân tay = đã đổi thước đo. Thiếu chúng thì mọi
    /// so sánh trước/sau đều là bịa, và HR sẽ quy thay đổi cho việc mình vừa sửa mốc trong khi thủ
    /// phạm có thể là admin đổi prompt (đúng lý do BK23 tồn tại).</para>
    ///
    /// <para>Row <c>Running</c> ghi TRƯỚC khi gọi AI: nó vừa là khoá chống double-click (UNIQUE có
    /// điều kiện) vừa là chỗ kết quả rơi vào kể cả khi trình duyệt HR chết giữa chừng.</para>
    /// </summary>
    public class RubricPreviewRun
    {
        public Guid Id { get; set; }
        public Guid CampaignId { get; set; }
        public Guid CreatedByUserId { get; set; }

        /// <summary>
        /// Câu hỏi được chấm thử. CỐ Ý KHÔNG FK: <c>PUT /questions</c> có thể xoá-và-tạo-lại câu hỏi,
        /// FK Restrict sẽ chặn HR sửa đề còn Cascade sẽ xoá mất lịch sử chấm thử.
        /// </summary>
        public Guid? QuestionId { get; set; }

        /// <summary>Snapshot nội dung câu hỏi — để lịch sử đọc được cả khi câu gốc đã bị sửa/xoá.</summary>
        public string QuestionText { get; set; } = null!;

        public RubricPreviewStatus Status { get; set; }

        /// <summary>Lượt này có tính phí không (3 lượt THÀNH CÔNG đầu của mỗi phiên bản thước đo là free).</summary>
        public bool Billed { get; set; }

        /// <summary>Dạng chuẩn tắc của bộ thước đo đã dùng (JSON) — xem <c>RubricFingerprint</c>.</summary>
        public string RubricSnapshot { get; set; } = null!;

        /// <summary>SHA-256 hex của <see cref="RubricSnapshot"/>.</summary>
        public string RubricFingerprint { get; set; } = null!;

        /// <summary>Bản thước đo (CAMP-18) tại thời điểm chạy — quota free tính theo từng phiên bản.</summary>
        public int RubricVersion { get; set; }

        /// <summary>Ba (hoặc bốn) bài mẫu + điểm từng tiêu chí, JSON.</summary>
        public string? Samples { get; set; }

        /// <summary>
        /// Bản prompt chấm (F21) lúc chạy. Admin sửa prompt giữa hai lượt là ĐỔI THƯỚC ĐO mà không đổi
        /// mốc nào — thiếu cột này thì HR quy nhầm thay đổi cho việc mình vừa sửa.
        /// </summary>
        public int? PromptVersion { get; set; }

        /// <summary>
        /// Ba bài mẫu lệch nhau quá nhiều về ĐỘ DÀI. Không giấu: nếu bộ chấm cũng thưởng độ dài thì dải
        /// điểm đẹp chỉ đang xác nhận một thước đo hỏng.
        /// </summary>
        public bool LengthParityWarning { get; set; }

        public string? ErrorReason { get; set; }

        public DateTime CreatedAt { get; set; }
        public DateTime? CompletedAt { get; set; }

        // Navigation
        public Campaign Campaign { get; set; } = null!;
    }

    public enum RubricPreviewStatus
    {
        Running,
        Succeeded,
        Failed
    }
}
