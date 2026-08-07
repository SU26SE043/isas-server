namespace Isas.Shared.Analytics
{
    public enum AnalyticsGranularity { Hour, Day, Month }

    public enum AnalyticsPeriodError { None, InvalidRange, InvalidGranularity }

    /// <summary>
    /// Khoá bucket analytics. <b>Phải tự khai <c>IComparable</c></b>: <c>record struct</c> chỉ auto-sinh
    /// <c>IEquatable</c> (đủ cho <c>GroupBy</c>) chứ KHÔNG sinh <c>CompareTo</c> — thiếu nó thì
    /// <c>Comparer&lt;T&gt;.Default</c> rơi về <c>ObjectComparer</c> và <c>OrderBy</c> ném
    /// <c>ArgumentException("At least one object must implement IComparable")</c> ngay ở lần so ĐẦU TIÊN.
    /// Nghĩa là 0–1 bucket thì im lặng, ≥2 bucket mới nổ ⇒ fixture test 1 bucket xanh trong khi
    /// production 500 với mọi dải ngày thật (đo 2026-08-07: 3/4 API admin analytics chết).
    /// </summary>
    public readonly record struct AnalyticsBucketKey(int Year, int Month, int Day, int Hour)
        : IComparable<AnalyticsBucketKey>
    {
        // Bậc thang thay vì (Y,M,D,H).CompareTo(tuple): để mutation gỡ ĐÚNG MỘT bậc được, và để thêm
        // field mới thì thấy ngay phải sửa ở đây — record tự cập nhật Equals nhưng KHÔNG cập nhật CompareTo.
        public int CompareTo(AnalyticsBucketKey other)
        {
            var c = Year.CompareTo(other.Year);
            if (c != 0) return c;
            c = Month.CompareTo(other.Month);
            if (c != 0) return c;
            c = Day.CompareTo(other.Day);
            if (c != 0) return c;
            return Hour.CompareTo(other.Hour);
        }
    }

    public sealed record AnalyticsPeriodResult(DateTime FromUtc, DateTime ToUtc, AnalyticsGranularity Granularity);

    /// <summary>
    /// FR18 — helper dùng chung cho mọi endpoint analytics (Auth/Interview/Campaign/Payment).
    /// Gom 3 thứ đã từng bị lặp/sai lệch: quy đổi UTC (bug 500 <c>POST /api/v1/campaign</c> do
    /// <c>DateTimeKind.Local</c>), kỳ nửa mở <c>[from, to)</c>, và bucket theo Year/Month/Day/Hour
    /// (KHÔNG dùng <c>date_trunc</c> — Npgsql-only, mất kiểm chứng ở test SQLite; xem <c>AiUsageService</c>).
    /// </summary>
    public static class AnalyticsPeriod
    {
        // Unspecified = client gửi chuỗi không offset → coi như đã là UTC (mọi mốc trong DB là UTC).
        // Local = client gửi offset số (+07:00) → quy đổi thật sự, đừng gán nhãn suông.
        public static DateTime ToUtc(DateTime value) => value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };

        /// <summary>
        /// Chuẩn hoá <c>from</c>/<c>to</c>/<c>groupBy</c> thành một kỳ nửa mở hợp lệ.
        /// <paramref name="allowedGranularities"/>: map chuỗi query-string (đã lowercase) → granularity —
        /// mỗi service tự khai bộ mình cho phép (Payment traffic thêm "hour", các endpoint khác chỉ
        /// "day"/"month") và tự viết thông điệp lỗi theo đúng ngôn ngữ hiện có, để KHÔNG phải sửa assert
        /// của test cũ khi migrate sang helper này.
        /// </summary>
        public static bool TryResolve(
            DateTime? from,
            DateTime? to,
            string? groupBy,
            IReadOnlyDictionary<string, AnalyticsGranularity> allowedGranularities,
            out AnalyticsPeriodResult? period,
            out AnalyticsPeriodError error)
        {
            var toUtc = ToUtc(to ?? DateTime.UtcNow);
            var fromUtc = ToUtc(from ?? toUtc.AddDays(-30));

            if (fromUtc >= toUtc)
            {
                period = null;
                error = AnalyticsPeriodError.InvalidRange;
                return false;
            }

            var key = (groupBy ?? "day").Trim().ToLowerInvariant();
            if (!allowedGranularities.TryGetValue(key, out var granularity))
            {
                period = null;
                error = AnalyticsPeriodError.InvalidGranularity;
                return false;
            }

            period = new AnalyticsPeriodResult(fromUtc, toUtc, granularity);
            error = AnalyticsPeriodError.None;
            return true;
        }

        /// <summary>
        /// Khoá gộp nhóm cho <c>GroupBy</c> phía C# (client-eval an toàn trên cả Npgsql lẫn SQLite).
        /// KHÔNG dùng <c>EF.Functions.DateTrunc</c> — chỉ Npgsql dịch được, SQLite (test) sẽ ném lúc chạy.
        /// </summary>
        public static AnalyticsBucketKey BucketKey(DateTime utc, AnalyticsGranularity granularity) => granularity switch
        {
            AnalyticsGranularity.Hour => new AnalyticsBucketKey(utc.Year, utc.Month, utc.Day, utc.Hour),
            AnalyticsGranularity.Month => new AnalyticsBucketKey(utc.Year, utc.Month, 1, 0),
            _ /* Day */ => new AnalyticsBucketKey(utc.Year, utc.Month, utc.Day, 0),
        };

        /// <summary>Mốc bắt đầu của bucket (để trả về <c>periodStart</c> trong response).</summary>
        public static DateTime BucketStart(AnalyticsBucketKey key, AnalyticsGranularity granularity) => granularity switch
        {
            AnalyticsGranularity.Hour => new DateTime(key.Year, key.Month, key.Day, key.Hour, 0, 0, DateTimeKind.Utc),
            AnalyticsGranularity.Month => new DateTime(key.Year, key.Month, 1, 0, 0, 0, DateTimeKind.Utc),
            _ => new DateTime(key.Year, key.Month, key.Day, 0, 0, 0, DateTimeKind.Utc),
        };
    }
}
