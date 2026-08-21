using System.ComponentModel.DataAnnotations;

namespace Isas.InterviewService.DTOs;

/// <summary>
/// Ngưỡng đạt của MỘT cấp độ, dưới góc nhìn màn quản trị.
///
/// <para>Trả về đủ BA thứ vì thiếu bất kỳ thứ nào thì admin không biết mình đang sửa cái gì so với
/// cái gì: <paramref name="EffectivePct"/> = con số đang có hiệu lực THẬT · <paramref name="DefaultPct"/>
/// = con số sẽ quay về nếu bấm reset · <paramref name="IsOverridden"/> = đã có ai chỉnh chưa. Chỉ
/// trả giá trị hiệu lực thì "60" không phân biệt được "mặc định của code" với "ai đó đã đặt đúng
/// bằng mặc định".</para>
/// </summary>
/// <param name="Level">Tên cấp độ, chính tắc ("Fresher", "Junior", …).</param>
/// <param name="EffectivePct">Ngưỡng đang có hiệu lực (hàng DB nếu có, ngược lại = mặc định).</param>
/// <param name="DefaultPct">Mặc định trong code/cấu hình — giá trị sau khi reset.</param>
/// <param name="IsOverridden">Có hàng trong DB không. <c>false</c> ⇒ đang chạy mặc định.</param>
/// <param name="UpdatedBy">Admin đã đặt (null khi chưa ai chỉnh).</param>
/// <param name="UpdatedAt">Lúc đặt (null khi chưa ai chỉnh).</param>
/// <param name="IsKnownLevel">
/// <c>false</c> = hàng DB trỏ tới một cấp độ mà code KHÔNG còn biết (cấp bị gỡ khỏi enum sau khi
/// đã cấu hình). Không đường nào đọc hàng đó nữa; hiện ra để admin thấy mà dọn, thay vì để nó nằm
/// vô hình trong bảng.
/// </param>
public record RoadmapLevelThresholdResponse(
    string Level,
    int EffectivePct,
    int DefaultPct,
    bool IsOverridden,
    Guid? UpdatedBy,
    DateTime? UpdatedAt,
    bool IsKnownLevel = true);

public class UpdateRoadmapLevelThresholdsRequest
{
    /// <summary>
    /// Khoá = tên cấp độ (không phân biệt hoa/thường), giá trị = ngưỡng % nguyên trong [0, 100].
    ///
    /// <para>Cập nhật MỘT PHẦN: cấp độ không nằm trong body thì giữ nguyên. Toàn bộ body được
    /// validate TRƯỚC khi ghi bất cứ thứ gì — một entry sai thì KHÔNG entry nào được ghi, để admin
    /// không rơi vào cảnh nhận 400 mà nửa thay đổi đã nằm trong DB.</para>
    /// </summary>
    [Required(ErrorMessage = "Cần ít nhất một cấp độ trong 'thresholds'.")]
    public Dictionary<string, int> Thresholds { get; set; } = [];
}
