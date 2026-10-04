namespace Isas.InterviewService.Services;

/// <summary>
/// MỘT luật duy nhất cho "mốc ban đầu" (<c>roadmaps.baseline</c>) của lộ trình: điểm % từng tiêu chí
/// = <b>trung bình các buổi luyện candidate đã chọn</b> làm nguồn lúc tạo (quyết định sản phẩm
/// 2026-10-04 — trước đó lấy buổi MỚI NHẤT có chấm tiêu chí đó).
///
/// <para><b>Vì sao trung bình chứ không buổi mới nhất:</b> đo trên prod, một buổi gần như bỏ trống
/// ("đừng hỏi tôi, tôi chịu") làm mốc của 5/6 tiêu chí về 0% trong khi buổi trước đó cùng tiêu chí
/// được 20% ⇒ chặng đầu báo "+44%" phần lớn chỉ vì đúng một buổi tệ. Người dùng đã chủ động chọn
/// các buổi nào đại diện cho mình, nên mọi buổi được chọn đều phải góp vào mốc.</para>
///
/// <para><b>Đều theo BUỔI, không theo dòng điểm:</b> mỗi buổi góp đúng một giá trị cho mỗi tiêu chí
/// (trung bình các dòng cùng tên trong buổi đó — thường chỉ 1 dòng). Một buổi có hai dòng cùng tên
/// (rubric đổi version) không được nặng gấp đôi buổi khác.</para>
///
/// <para>Đường TẠO (<see cref="RoadmapService"/>) và đường HIỂN THỊ phần tính
/// (<see cref="RoadmapReportService"/>) gọi CÙNG <see cref="PerSession"/> + <see cref="Average"/> ⇒
/// danh sách buổi hiện dưới "mốc đem so" luôn cộng ra đúng con số mốc — do cấu trúc, không phải nhờ
/// hai chỗ tình cờ làm tròn giống nhau.</para>
/// </summary>
internal static class RoadmapBaselineRule
{
    internal readonly record struct Row(Guid SessionId, string CriterionName, decimal Percentage);

    /// <summary>
    /// % của TỪNG buổi cho từng tiêu chí (tên → buổi → %), làm tròn 2 chữ số. Đây là đúng giá trị
    /// được hiển thị cho từng buổi VÀ đúng giá trị đem đi lấy trung bình.
    /// </summary>
    internal static Dictionary<string, Dictionary<Guid, decimal>> PerSession(IEnumerable<Row> rows) =>
        rows.GroupBy(r => r.CriterionName, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => g.GroupBy(r => r.SessionId)
                    .ToDictionary(sg => sg.Key, sg => Math.Round(sg.Average(r => r.Percentage), 2)),
                StringComparer.Ordinal);

    /// <summary>Mốc của một tiêu chí từ % các buổi (đã qua <see cref="PerSession"/>).</summary>
    internal static decimal Average(IEnumerable<decimal> perSessionPercentages) =>
        Math.Round(perSessionPercentages.Average(), 2);

    /// <summary>Mốc ban đầu đủ mọi tiêu chí từ các dòng điểm của những buổi đã chọn.</summary>
    internal static Dictionary<string, decimal> Average(IEnumerable<Row> rows) =>
        PerSession(rows).ToDictionary(kv => kv.Key, kv => Average(kv.Value.Values), StringComparer.Ordinal);
}
