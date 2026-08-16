namespace Isas.Shared.Rubric;

/// <summary>
/// Chọn MỨC KỲ VỌNG cho ba bài mẫu của một lượt chấm thử.
///
/// <para><b>Do CODE chọn, không phải model tự đặt</b> — đó là cả điểm mấu chốt: có mức biết trước
/// thì mới so được "kỳ vọng vs thật", và đó là số đo duy nhất phơi bày việc model tự khen văn do
/// chính nó viết ra (self-scoring bias).</para>
///
/// <para><b>Vì sao ở Shared:</b> chấm thử chạy ở hai chỗ — employer kiểm thước đo campaign
/// (CampaignService) và admin kiểm bộ chuẩn B2C (InterviewService). Nếu mỗi bên tự chọn mức kỳ
/// vọng thì hai báo cáo "kỳ vọng vs thật" **đo hai thứ khác nhau trong khi trông giống hệt** — và
/// không có gì trên màn hình nói ra điều đó.</para>
/// </summary>
public static class ExpectedLevels
{
    /// <param name="sortedAscending">Mốc đã sắp theo điểm TĂNG DẦN (dùng <see cref="CriterionLevelRules.Validate"/>).</param>
    public static (int Weak, int Good, int Excellent) For(IReadOnlyList<RubricLevelSnapshot> sortedAscending)
    {
        var n = sortedAscending.Count;
        return (
            sortedAscending[n / 4].Score,
            sortedAscending[Math.Min(n - 1, (int)(n * 0.6))].Score,
            sortedAscending[n - 1].Score);
    }
}
