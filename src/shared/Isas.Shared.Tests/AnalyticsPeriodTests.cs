using Isas.Shared.Analytics;
using Xunit;

namespace Isas.Shared.Tests
{
    public class AnalyticsPeriodTests
    {
        private static readonly IReadOnlyDictionary<string, AnalyticsGranularity> DayMonth =
            new Dictionary<string, AnalyticsGranularity>
            {
                ["day"] = AnalyticsGranularity.Day,
                ["month"] = AnalyticsGranularity.Month,
            };

        private static readonly IReadOnlyDictionary<string, AnalyticsGranularity> DayMonthHour =
            new Dictionary<string, AnalyticsGranularity>
            {
                ["day"] = AnalyticsGranularity.Day,
                ["month"] = AnalyticsGranularity.Month,
                ["hour"] = AnalyticsGranularity.Hour,
            };

        // ── ToUtc ──────────────────────────────────────────────────────────

        [Fact]
        public void ToUtc_Utc_GiuNguyen()
        {
            var v = new DateTime(2026, 7, 20, 10, 0, 0, DateTimeKind.Utc);
            Assert.Equal(v, AnalyticsPeriod.ToUtc(v));
        }

        [Fact]
        public void ToUtc_Local_QuyDoiThat()
        {
            // Local +07:00 → UTC phải lùi lại thật sự, không chỉ gán nhãn.
            var local = new DateTime(2026, 7, 20, 10, 0, 0, DateTimeKind.Local);
            var utc = AnalyticsPeriod.ToUtc(local);
            Assert.Equal(DateTimeKind.Utc, utc.Kind);
            Assert.Equal(local.ToUniversalTime(), utc);
        }

        [Fact]
        public void ToUtc_Unspecified_ChiGanNhan_KhongDoiGioTri()
        {
            // Unspecified (client gửi chuỗi không offset) → coi NHƯ ĐÃ LÀ UTC, không quy đổi.
            var v = new DateTime(2026, 7, 20, 10, 0, 0, DateTimeKind.Unspecified);
            var utc = AnalyticsPeriod.ToUtc(v);
            Assert.Equal(DateTimeKind.Utc, utc.Kind);
            Assert.Equal(10, utc.Hour); // giờ KHÔNG đổi — chỉ đổi Kind
        }

        // ── TryResolve ─────────────────────────────────────────────────────

        [Fact]
        public void TryResolve_ThieuCaTuLanDen_MacDinh30NgayGanNhat()
        {
            var ok = AnalyticsPeriod.TryResolve(null, null, null, DayMonth, out var period, out var error);

            Assert.True(ok);
            Assert.Equal(AnalyticsPeriodError.None, error);
            Assert.NotNull(period);
            Assert.Equal(AnalyticsGranularity.Day, period!.Granularity); // mặc định groupBy=day
            Assert.True((period.ToUtc - period.FromUtc).TotalDays is > 29.9 and < 30.1);
        }

        [Fact]
        public void TryResolve_FromBangTo_TraLoi_InvalidRange()
        {
            var mark = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);
            var ok = AnalyticsPeriod.TryResolve(mark, mark, "day", DayMonth, out var period, out var error);

            Assert.False(ok);
            Assert.Null(period);
            Assert.Equal(AnalyticsPeriodError.InvalidRange, error);
        }

        [Fact]
        public void TryResolve_FromLonHonTo_TraLoi_InvalidRange()
        {
            var from = new DateTime(2026, 7, 10, 0, 0, 0, DateTimeKind.Utc);
            var to = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);
            var ok = AnalyticsPeriod.TryResolve(from, to, "day", DayMonth, out _, out var error);

            Assert.False(ok);
            Assert.Equal(AnalyticsPeriodError.InvalidRange, error);
        }

        [Theory]
        [InlineData("week")]
        [InlineData("")]
        [InlineData("Day ")] // có khoảng trắng — vẫn phải trim trước khi so
        public void TryResolve_GroupByLa_KhopSauTrimLowercase_HoacBaoLoi(string raw)
        {
            var trimmedLower = raw.Trim().ToLowerInvariant();
            var ok = AnalyticsPeriod.TryResolve(null, null, raw, DayMonth, out var period, out var error);

            if (trimmedLower == "day")
            {
                Assert.True(ok);
                Assert.Equal(AnalyticsGranularity.Day, period!.Granularity);
            }
            else
            {
                Assert.False(ok);
                Assert.Equal(AnalyticsPeriodError.InvalidGranularity, error);
            }
        }

        [Fact]
        public void TryResolve_HourChiHopLeKhiServiceTuKhaiBao()
        {
            // Payment traffic (§B2) khai thêm "hour" — Auth/Interview/Campaign KHÔNG khai → phải lỗi.
            var okWithHour = AnalyticsPeriod.TryResolve(null, null, "hour", DayMonthHour, out var p1, out _);
            Assert.True(okWithHour);
            Assert.Equal(AnalyticsGranularity.Hour, p1!.Granularity);

            var okWithoutHour = AnalyticsPeriod.TryResolve(null, null, "hour", DayMonth, out _, out var err2);
            Assert.False(okWithoutHour);
            Assert.Equal(AnalyticsPeriodError.InvalidGranularity, err2);
        }

        [Fact]
        public void TryResolve_MonthHopLe()
        {
            var ok = AnalyticsPeriod.TryResolve(null, null, "month", DayMonth, out var period, out _);
            Assert.True(ok);
            Assert.Equal(AnalyticsGranularity.Month, period!.Granularity);
        }

        [Fact]
        public void TryResolve_ChuyenDoiVeUtc_TruocKhiSoSanh()
        {
            // from Local, to Unspecified — cả hai phải được quy về Utc trước khi so from<to.
            var fromLocal = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Local);
            var toUnspecified = new DateTime(2026, 7, 20, 0, 0, 0, DateTimeKind.Unspecified);

            var ok = AnalyticsPeriod.TryResolve(fromLocal, toUnspecified, "day", DayMonth, out var period, out _);

            Assert.True(ok);
            Assert.Equal(DateTimeKind.Utc, period!.FromUtc.Kind);
            Assert.Equal(DateTimeKind.Utc, period.ToUtc.Kind);
        }

        // ── BucketKey / BucketStart ────────────────────────────────────────

        [Fact]
        public void BucketKey_Day_BoQuaGio()
        {
            var a = new DateTime(2026, 7, 20, 3, 0, 0, DateTimeKind.Utc);
            var b = new DateTime(2026, 7, 20, 23, 59, 0, DateTimeKind.Utc);

            Assert.Equal(AnalyticsPeriod.BucketKey(a, AnalyticsGranularity.Day),
                         AnalyticsPeriod.BucketKey(b, AnalyticsGranularity.Day));
        }

        [Fact]
        public void BucketKey_Month_BoQuaNgayVaGio()
        {
            var a = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);
            var b = new DateTime(2026, 7, 31, 23, 0, 0, DateTimeKind.Utc);

            Assert.Equal(AnalyticsPeriod.BucketKey(a, AnalyticsGranularity.Month),
                         AnalyticsPeriod.BucketKey(b, AnalyticsGranularity.Month));
        }

        [Fact]
        public void BucketKey_Hour_PhanBietTheoGio()
        {
            var a = new DateTime(2026, 7, 20, 3, 10, 0, DateTimeKind.Utc);
            var b = new DateTime(2026, 7, 20, 4, 10, 0, DateTimeKind.Utc);

            Assert.NotEqual(AnalyticsPeriod.BucketKey(a, AnalyticsGranularity.Hour),
                             AnalyticsPeriod.BucketKey(b, AnalyticsGranularity.Hour));
        }

        [Fact]
        public void BucketStart_TraVeDungMocDauBucket()
        {
            var t = new DateTime(2026, 7, 20, 15, 42, 33, DateTimeKind.Utc);
            var key = AnalyticsPeriod.BucketKey(t, AnalyticsGranularity.Hour);
            var start = AnalyticsPeriod.BucketStart(key, AnalyticsGranularity.Hour);

            Assert.Equal(new DateTime(2026, 7, 20, 15, 0, 0, DateTimeKind.Utc), start);
        }

        // ── Q5: AnalyticsBucketKey phải sắp xếp được ─────────────────────────
        // Trước bản vá, `OrderBy` trên khoá này ném ArgumentException NGAY LẦN SO ĐẦU TIÊN ⇒ chỉ nổ khi
        // có ≥2 bucket. Mọi test cũ ở file này chỉ dùng Equals/NotEqual (IEquatable có sẵn) nên mù ca đó.

        [Fact]
        public void OrderBy_NhieuBucket_SapTheoThoiGian_KeCaVuotBienNamVaThang()
        {
            // Cố ý trộn: vượt biên NĂM (2025-12-31 → 2026-01-01) và vượt biên THÁNG (2026-01-31 → 2026-02-01).
            // Nếu CompareTo so sai bậc (vd bỏ Year) thì hai cặp này là chỗ lộ ra trước tiên.
            var mong = new[]
            {
                new AnalyticsBucketKey(2025, 12, 31, 0),
                new AnalyticsBucketKey(2026, 1, 1, 0),
                new AnalyticsBucketKey(2026, 1, 31, 0),
                new AnalyticsBucketKey(2026, 2, 1, 0),
                new AnalyticsBucketKey(2026, 2, 1, 5),
            };
            var tron = new[] { mong[3], mong[0], mong[4], mong[2], mong[1] };

            Assert.Equal(mong, tron.OrderBy(k => k).ToArray());
        }

        [Theory]
        // Mỗi cặp chỉ khác ĐÚNG MỘT trường ⇒ mutation gỡ một bậc trong CompareTo làm đúng dòng này đỏ.
        [InlineData(2025, 7, 20, 3, 2026, 7, 20, 3)]  // Year
        [InlineData(2026, 6, 20, 3, 2026, 7, 20, 3)]  // Month
        [InlineData(2026, 7, 19, 3, 2026, 7, 20, 3)]  // Day
        [InlineData(2026, 7, 20, 2, 2026, 7, 20, 3)]  // Hour
        public void CompareTo_TungBac_NhoHonThiAm(int y1, int m1, int d1, int h1, int y2, int m2, int d2, int h2)
        {
            var nho = new AnalyticsBucketKey(y1, m1, d1, h1);
            var lon = new AnalyticsBucketKey(y2, m2, d2, h2);

            Assert.True(nho.CompareTo(lon) < 0);
            Assert.True(lon.CompareTo(nho) > 0);
        }

        [Fact]
        public void CompareTo_NhatQuanVoi_Equals()
        {
            var keys = new[]
            {
                new AnalyticsBucketKey(2026, 7, 20, 3),
                new AnalyticsBucketKey(2026, 7, 20, 4),
                new AnalyticsBucketKey(2026, 7, 21, 3),
                new AnalyticsBucketKey(2026, 8, 20, 3),
            };

            foreach (var a in keys)
                foreach (var b in keys)
                    Assert.Equal(a.Equals(b), a.CompareTo(b) == 0);
        }

        [Fact]
        public void ThemFieldMoi_PhaiSuaCaCompareTo_KhongChiEquals()
        {
            // Guard trôi dạt: `record struct` tự cập nhật Equals/GetHashCode khi thêm field, nhưng CompareTo
            // là code viết tay ⇒ field thứ 5 sẽ lọt khỏi thứ tự sắp xếp trong IM LẶNG. Test này đỏ để nhắc.
            Assert.Equal(4, typeof(AnalyticsBucketKey).GetProperties().Length);
        }
    }
}
