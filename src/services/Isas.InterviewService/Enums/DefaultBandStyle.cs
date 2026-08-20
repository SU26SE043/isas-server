namespace Isas.InterviewService.Enums;

/// <summary>
/// Cách sinh dải mức MẶC ĐỊNH cho tiêu chí KHÔNG khai <c>rubric_levels</c>
/// (xem <see cref="Services.ScoringCriteriaBuilder.DefaultBand"/>).
///
/// <para><b>Đây là cần gạt của một thay đổi THƯỚC ĐO, và nó MẶC ĐỊNH TẮT.</b> Đổi dải mặc định là
/// đổi ý nghĩa của mọi điểm số sinh ra sau đó — mà điểm đang dùng để xếp hạng ứng viên (CAMP-10) và
/// để đo cải thiện theo thời gian (BC15). Lý do bật <see cref="Descriptive"/> hiện mới là GIẢ
/// THUYẾT chưa nghiệm thu (xem <see cref="Services.ScoringCriteriaBuilder.DefaultBand"/>), nên bật
/// phải là một QUYẾT ĐỊNH sau khi đo, không phải thứ lặng lẽ đi kèm một lần deploy.</para>
///
/// <para>Khác tiền lệ <c>Scoring:UseSampleAnswer</c> (mặc định BẬT vì đó là dữ liệu HR chủ động soạn
/// ra để AI chấm theo): ở đây chưa có ai chủ động khai gì cả, và số đo hiện có KHÔNG kết luận được
/// thước mới tốt hơn.</para>
/// </summary>
public enum DefaultBandStyle
{
    /// <summary>
    /// MẶC ĐỊNH — hành vi có từ E9: liệt kê MỌI số nguyên <c>0..maxScore</c>, descriptor
    /// <c>"Mức i/maxScore"</c>. Descriptor này không mang thông tin (prompt in ra
    /// <c>• Mức 3: Mức 3/5</c> rồi dòng dưới bắt AI "bám descriptor của mức đã chọn" — bám một
    /// tautology), nhưng đây là thước đã sinh ra toàn bộ điểm đang lưu, nên nó là mốc quy chiếu.
    /// </summary>
    EveryInteger = 0,

    /// <summary>
    /// OPT-IN (thêm 2026-08-20) — tối đa 6 mốc trải đều trên thang, descriptor là bậc chất lượng so
    /// sánh được ("Không đáp ứng / Yếu / Trung bình / Khá / Tốt / Xuất sắc"), ĐỘC LẬP với
    /// <c>maxScore</c>. Bật bằng <c>Scoring:DefaultBandStyle=Descriptive</c>.
    /// </summary>
    Descriptive = 1
}
