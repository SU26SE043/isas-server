namespace Isas.InterviewService.Entities;

/// <summary>
/// Ngưỡng % ĐẠT của một cấp độ lộ trình, do admin chỉnh (BC15/D20).
///
/// <para><b>Chỉ lưu phần GHI ĐÈ, không lưu bản mặc định.</b> Bảng rỗng = "chưa ai chỉnh" = hệ thống
/// chạy đúng như trước khi có tính năng này (rơi về <see cref="Models.RoadmapOptions.ThresholdFor"/>,
/// vốn đã phủ cả <c>appsettings</c>/env lẫn hằng số trong code). Chép bản mặc định vào đây sẽ tạo
/// hai nguồn sự thật cho cùng một con số, và bản seed sẽ lệch khỏi code ngay lần sửa đầu tiên mà
/// không ai biết. Cùng lập luận với <see cref="PromptTemplate"/> (F21).</para>
///
/// <para><b>Khoá là CHUỖI tên cấp độ, không phải một cột cho mỗi cấp.</b> Thêm cấp độ mới
/// (Intern, Lead…) chỉ cần thêm giá trị vào <c>RoadmapLevel</c> — KHÔNG cần migration, không cần
/// đụng bảng. Cột-cứng-mỗi-cấp thì mỗi cấp mới là một lần đổi schema trên DB production.</para>
///
/// <para><b>KHÔNG hồi tố lộ trình đã đóng sổ.</b> Ngưỡng được chốt lúc build report và snapshot vào
/// <c>roadmaps.final_report</c>; sửa ở đây chỉ ảnh hưởng report tính từ lúc sửa trở đi. Đó là hành
/// vi cố ý: một lộ trình đã báo "Đạt" cho người học không được phép âm thầm đổi thành "Chưa đạt"
/// vì admin vừa kéo ngưỡng lên.</para>
///
/// <para>Sửa tại chỗ (UPDATE), KHÔNG append-only như <see cref="PromptTemplate"/>: không có con dấu
/// nào ở nơi khác trỏ ngược về một hàng của bảng này (khác <c>answer_scores.prompt_version</c>), nên
/// giữ lịch sử version ở đây không trả lời thêm được câu hỏi nào — báo cáo đã chốt tự mang theo
/// ngưỡng của nó trong snapshot.</para>
/// </summary>
public class RoadmapLevelThreshold
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Tên cấp độ, dạng CHÍNH TẮC đúng như <c>RoadmapLevel.ToString()</c> ("Fresher", "Junior"…).
    ///
    /// <para>⚠ Ghi vào đây phải đi qua chuẩn hoá của <c>RoadmapThresholdService</c>. Lưu "fresher"
    /// thường trong khi đường đọc hỏi "Fresher" là hỏng IM LẶNG: không lỗi, không cảnh báo, admin
    /// tưởng đã chỉnh xong còn report vẫn dùng ngưỡng mặc định.</para>
    /// </summary>
    public string Level { get; set; } = null!;

    /// <summary>Ngưỡng đạt, phần trăm nguyên trong [0, 100]. CHECK ở tầng DB.</summary>
    public int ThresholdPct { get; set; }

    /// <summary>User id của admin đã đặt giá trị này (JWT sub). Không FK — Auth là service khác (GEN-2).</summary>
    public Guid UpdatedBy { get; set; }

    /// <summary>
    /// Lúc đặt giá trị. CỐ Ý không implement <see cref="IHasUpdatedAt"/>: dấu này phải đi CÙNG CẶP
    /// với <see cref="UpdatedBy"/> (ai + khi nào), nên nó được set tường minh ở đúng chỗ biết
    /// "ai" — để SaveChanges đóng dấu hộ thì có đường sửa được thời gian mà không sửa người.
    /// </summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
