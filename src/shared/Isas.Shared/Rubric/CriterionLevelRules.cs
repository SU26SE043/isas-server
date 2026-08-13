namespace Isas.Shared.Rubric;

/// <summary>
/// CAMP-17 — luật cấu trúc của MỘT thang điểm, dùng chung cho cả tiêu chí campaign (B2B) lẫn
/// rubric B2C (bộ chuẩn admin + rubric riêng người luyện).
///
/// <para><b>Vì sao dùng chung chứ không copy-paste:</b> thang méo KHÔNG làm lỗi nào nổ ở đường
/// chấm — nó chỉ làm điểm sai. Hai bản luật lệch nhau nghĩa là cùng một bộ mốc được nhận ở chỗ
/// này và bị từ chối ở chỗ kia (hoặc tệ hơn: được nhận ở cả hai nhưng chấm ra hai kiểu), và không
/// có triệu chứng nào ngoài điểm số trông vẫn hợp lý.</para>
/// </summary>
public static class CriterionLevelRules
{
    /// <summary>2 mốc là ít nhất để có "biên"; &gt;10 thì mô tả giữa các mốc chồng lấn tới mức chính
    /// người chấm cũng không phân biệt được.</summary>
    public const int LevelsMin = 2;
    public const int LevelsMax = 10;

    /// <summary>Dưới 20 ký tự thì không thể vừa nói "CÓ gì" vừa nói "CÒN THIẾU gì"; trên 500 thì mốc
    /// thành một đoạn văn và prompt chấm phình theo (số tiêu chí × số mốc).</summary>
    public const int DescriptorMin = 20;
    public const int DescriptorMax = 500;

    /// <summary>
    /// Kiểm + chuẩn hoá (trim mô tả, sắp theo điểm tăng dần) một thang điểm.
    ///
    /// <para><b>KHÔNG ném exception — cố ý.</b> Hai service map lỗi thành HTTP 400 theo hai đường
    /// khác nhau: Campaign có middleware bắt <c>ArgumentException</c>, còn controller của Interview
    /// chỉ bắt <c>InvalidOperationException</c> nên <c>ArgumentException</c> rơi xuống
    /// <c>catch(Exception)</c> và ra <b>500 với MỌI input sai</b> (đúng lỗi đã xảy ra ở F2b). Trả lỗi
    /// về cho caller ném đúng loại của mình là cách duy nhất không tái tạo nó.</para>
    /// </summary>
    /// <param name="criterionName">Tên tiêu chí — chỉ dùng để ghép câu lỗi cho người đọc.</param>
    /// <param name="maxScore">Thang điểm của tiêu chí; mốc phải nằm trong <c>[0, maxScore]</c>.</param>
    /// <returns>
    /// <c>Error == null</c> ⇒ hợp lệ, <c>Levels</c> là bản đã chuẩn hoá. Ngược lại <c>Error</c> là câu
    /// giải thích **vi phạm ĐẦU TIÊN** kèm tên tiêu chí và mốc — đủ để trả thẳng cho người dùng.
    /// </returns>
    public static (string? Error, IReadOnlyList<RubricLevelSnapshot> Levels) Validate(
        string criterionName, int maxScore, IReadOnlyList<RubricLevelSnapshot> items)
    {
        var empty = Array.Empty<RubricLevelSnapshot>();

        if (items.Count < LevelsMin || items.Count > LevelsMax)
            return ($"Tiêu chí '{criterionName}' phải có {LevelsMin}–{LevelsMax} mốc điểm (hiện: {items.Count}).", empty);

        var scores = new HashSet<int>();
        var normalized = new List<RubricLevelSnapshot>(items.Count);

        foreach (var item in items)
        {
            if (item.Score < 0 || item.Score > maxScore)
                return ($"Mốc {item.Score} của tiêu chí '{criterionName}' phải nằm trong [0, {maxScore}].", empty);

            if (!scores.Add(item.Score))
                return ($"Tiêu chí '{criterionName}' có hai mốc cùng điểm {item.Score} — việc chọn mức khi chấm sẽ không xác định.", empty);

            var descriptor = item.Descriptor?.Trim() ?? string.Empty;
            if (descriptor.Length < DescriptorMin || descriptor.Length > DescriptorMax)
                return ($"Mô tả mốc {item.Score} của tiêu chí '{criterionName}' phải dài {DescriptorMin}–{DescriptorMax} ký tự (hiện: {descriptor.Length}).", empty);

            normalized.Add(new RubricLevelSnapshot(item.Score, descriptor));
        }

        // Hai mốc biên là bắt buộc, và cả hai đều hỏng IM LẶNG nếu thiếu:
        if (!scores.Contains(0))
            return ($"Tiêu chí '{criterionName}' phải có mốc 0 — thiếu nó thì câu trả lời trống bị chấm về mốc thấp nhất đang có, tức người không nói gì vẫn có điểm.", empty);

        if (!scores.Contains(maxScore))
            return ($"Tiêu chí '{criterionName}' phải có mốc {maxScore} (điểm tối đa) — thiếu nó thì không có mức nào mô tả câu trả lời đạt điểm cao nhất, và luật \"đáp án mẫu viết ở mức tối đa\" trỏ vào một mức không tồn tại.", empty);

        // Sắp tăng dần: đường chấm và mọi chỗ đọc mốc đều giả định thứ tự này. `.Include()` của EF
        // KHÔNG bảo đảm thứ tự nào cả, nên chuẩn hoá ngay tại cửa vào thay vì tin vào DB.
        normalized.Sort((a, b) => a.Score.CompareTo(b.Score));
        return (null, normalized);
    }
}
