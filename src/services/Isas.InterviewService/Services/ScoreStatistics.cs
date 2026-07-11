namespace Isas.InterviewService.Services;

/// <summary>
/// E10 — self-consistency: điểm chốt mỗi <c>(answer, criterion)</c> = <b>MEDIAN</b> qua các attempt
/// (thay vì "attempt mới nhất"). Median không dịch được sang SQL (EF Core) → gọi sau khi
/// <c>ToListAsync()</c> materialize rồi tính client-side (dataset 1 answer × N attempt rất nhỏ).
///
/// <para><b>N=1 (mặc định, opt-in tắt):</b> median-of-1 = chính giá trị đó → giữ nguyên hành vi cũ.</para>
/// </summary>
public static class ScoreStatistics
{
    /// <summary>
    /// Median của tập điểm. Lẻ → phần tử giữa; chẵn → trung bình 2 phần tử giữa.
    /// Rỗng → 0 (không có điểm để chốt).
    /// </summary>
    public static decimal Median(IEnumerable<decimal> scores)
    {
        var sorted = scores.OrderBy(s => s).ToList();
        var n = sorted.Count;
        if (n == 0) return 0m;
        var mid = n / 2;
        return n % 2 == 1
            ? sorted[mid]
            : (sorted[mid - 1] + sorted[mid]) / 2m;
    }
}
