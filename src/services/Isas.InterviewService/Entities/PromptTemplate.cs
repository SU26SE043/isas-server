namespace Isas.InterviewService.Entities;

/// <summary>
/// F21 (FR17) — một MẢNH prompt do admin tuỳ biến, thay cho việc sửa <c>prompts.py</c> rồi
/// build lại image.
///
/// <para><b>Chỉ lưu phần GHI ĐÈ, không lưu bản mặc định.</b> Văn bản mặc định của mọi prompt
/// vẫn nằm trong <c>app/prompts.py</c> (28KB tiếng Việt). Chép nó sang đây sẽ tạo ra hai nguồn
/// sự thật cho cùng một câu chữ, ở hai ngôn ngữ, và bản seed sẽ lệch khỏi code ngay lần sửa
/// prompts.py đầu tiên mà không ai biết. Bảng rỗng = "chưa ai tuỳ biến gì" = AIService chạy
/// đúng như trước khi có F21.</para>
///
/// <para><b>APPEND-ONLY.</b> Sửa = deactivate bản cũ + insert bản mới <c>Version+1</c> (mẫu
/// BC16). KHÔNG bao giờ UPDATE văn bản tại chỗ: điểm đã chấm có đóng dấu
/// <c>answer_scores.prompt_version</c>, nên sửa tại chỗ sẽ khiến con dấu đó trỏ tới một văn bản
/// KHÁC với văn bản thực sự đã chấm ⇒ dấu vết kiểm toán nói dối. Với thứ quyết định điểm của
/// người trả tiền, "không truy lại được" là hỏng chứ không phải bất tiện.</para>
///
/// <para>⚠ <b>Không phải mảnh nào của prompt cũng vào được đây.</b> Khung chống prompt-injection
/// (AI-4/E11), delimiter bọc dữ liệu ứng viên, hợp đồng output, luật chọn mức E9 và luật trích
/// dẫn E11 do CODE giữ, admin không sửa được. Xem <c>app/prompt_registry.py</c> §"Vì sao khung
/// bất biến".</para>
/// </summary>
public class PromptTemplate
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Khoá mảnh, vd <c>scoring.persona</c> · <c>category.BE.guidance</c>. Tập khoá hợp
    /// lệ do CODE khai (<see cref="Data.PromptTemplateKeys"/>) — admin không tự đặt khoá mới,
    /// vì khoá lạ sẽ không có chỗ nào đọc và người sửa tưởng mình vừa đổi được hành vi.</summary>
    public string Key { get; set; } = null!;

    /// <summary>Tăng dần theo từng lần sửa, bắt đầu từ 1.</summary>
    public int Version { get; set; }

    /// <summary>Văn bản thay thế bản mặc định trong code.</summary>
    public string Body { get; set; } = null!;

    /// <summary>Đúng 1 bản active cho mỗi <see cref="Key"/>; bản cũ giữ lại để truy lịch sử.</summary>
    public bool IsActive { get; set; } = true;

    /// <summary>User id của admin đã tạo bản này (JWT sub). Không FK — Auth là service khác (GEN-2).</summary>
    public Guid UpdatedBy { get; set; }

    /// <summary>Vì sao sửa. Bắt buộc: sáu tháng sau, "vì sao đổi cách chấm" là câu không ai
    /// trả lời được nếu lúc sửa không ai ghi.</summary>
    public string? ChangeNote { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
