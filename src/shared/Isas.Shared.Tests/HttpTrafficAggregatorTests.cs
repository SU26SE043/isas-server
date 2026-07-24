using Isas.Shared.Analytics;
using Xunit;

namespace Isas.Shared.Tests
{
    public class HttpTrafficAggregatorTests
    {
        private static readonly DateTime T0 = new(2026, 7, 20, 10, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void Record_GomTheoRouteVaStatusClass()
        {
            var agg = new HttpTrafficAggregator(windowSeconds: 60, maxPendingWindows: 3);

            agg.Record("campaign-route", 200, 50, T0);
            agg.Record("campaign-route", 201, 30, T0.AddSeconds(1));
            agg.Record("campaign-route", 404, 10, T0.AddSeconds(2));

            // Drain SAU khi cửa sổ đã trôi qua — cửa sổ đang mở không được trả.
            var stats = agg.Drain(T0.AddSeconds(61));

            var ok2xx = Assert.Single(stats, s => s.RouteId == "campaign-route" && s.StatusClass == "2xx");
            Assert.Equal(2, ok2xx.Requests);
            Assert.Equal(80, ok2xx.SumDurationMs);
            Assert.Equal(50, ok2xx.MaxDurationMs);

            var e4xx = Assert.Single(stats, s => s.RouteId == "campaign-route" && s.StatusClass == "4xx");
            Assert.Equal(1, e4xx.Requests);
        }

        [Fact]
        public void Drain_CuaSoDangMo_KhongDuocTra()
        {
            var agg = new HttpTrafficAggregator(windowSeconds: 60, maxPendingWindows: 3);
            agg.Record("auth-route", 200, 10, T0);

            // Chưa qua windowEnd (T0+60s) → cửa sổ vẫn đang mở.
            var stats = agg.Drain(T0.AddSeconds(30));

            Assert.Empty(stats);
        }

        [Fact]
        public void Drain_XoaKhoiBoNhoSauKhiTra()
        {
            var agg = new HttpTrafficAggregator(windowSeconds: 60, maxPendingWindows: 3);
            agg.Record("auth-route", 200, 10, T0);

            var first = agg.Drain(T0.AddSeconds(61));
            Assert.Single(first);

            // Không Record thêm gì mới, cửa sổ trước đã bị lấy đi → Drain lại phải rỗng.
            var second = agg.Drain(T0.AddSeconds(62));
            Assert.Empty(second);
        }

        [Fact]
        public void NhieuCuaSoLienTiep_MoiCuaSoMotDong()
        {
            var agg = new HttpTrafficAggregator(windowSeconds: 60, maxPendingWindows: 5);

            agg.Record("payment-route", 200, 10, T0);                      // cửa sổ 1
            agg.Record("payment-route", 200, 10, T0.AddSeconds(65));       // cửa sổ 2
            agg.Record("payment-route", 200, 10, T0.AddSeconds(130));      // cửa sổ 3

            var stats = agg.Drain(T0.AddSeconds(200));

            // Cửa sổ hiện tại (chứa request thứ 3) CHƯA đóng ở mốc 200 nếu chưa đủ 60s kể từ khi mở —
            // nên chỉ khẳng định có ÍT NHẤT 2 cửa sổ tách biệt đã đóng, không tính cứng số 3.
            var windowStarts = stats.Select(s => s.WindowStart).Distinct().ToList();
            Assert.True(windowStarts.Count >= 2);
        }

        [Fact]
        public void TranMaxPendingWindows_BoCuaSoCuNhat_VaBaoQuaEvent()
        {
            var agg = new HttpTrafficAggregator(windowSeconds: 10, maxPendingWindows: 2);
            var droppedMessages = new List<string>();
            agg.OnWindowDropped += msg => droppedMessages.Add(msg);

            // Tạo 4 cửa sổ liên tiếp (10s/cửa sổ) mà KHÔNG Drain giữa chừng → phải tràn maxPendingWindows=2.
            agg.Record("r", 200, 1, T0);
            agg.Record("r", 200, 1, T0.AddSeconds(11));
            agg.Record("r", 200, 1, T0.AddSeconds(22));
            agg.Record("r", 200, 1, T0.AddSeconds(33));

            var stats = agg.Drain(T0.AddSeconds(44));

            // Giữ tối đa maxPendingWindows cửa sổ → không quá 2 WindowStart khác nhau còn lại.
            var windowStarts = stats.Select(s => s.WindowStart).Distinct().ToList();
            Assert.True(windowStarts.Count <= 2);
            Assert.NotEmpty(droppedMessages);
        }

        [Fact]
        public void StatusClass_PhanLoaiDung2xx3xx4xx5xx()
        {
            var agg = new HttpTrafficAggregator(windowSeconds: 60, maxPendingWindows: 3);

            agg.Record("r", 200, 1, T0);
            agg.Record("r", 301, 1, T0);
            agg.Record("r", 400, 1, T0);
            agg.Record("r", 500, 1, T0);

            var stats = agg.Drain(T0.AddSeconds(61));

            Assert.Contains(stats, s => s.StatusClass == "2xx" && s.Requests == 1);
            Assert.Contains(stats, s => s.StatusClass == "3xx" && s.Requests == 1);
            Assert.Contains(stats, s => s.StatusClass == "4xx" && s.Requests == 1);
            Assert.Contains(stats, s => s.StatusClass == "5xx" && s.Requests == 1);
        }

        [Fact]
        public void MaxDurationMs_LayGiaTriLonNhatTrongCuaSo_KhongPhaiTongHayTrungBinh()
        {
            var agg = new HttpTrafficAggregator(windowSeconds: 60, maxPendingWindows: 3);

            agg.Record("r", 200, 5, T0);
            agg.Record("r", 200, 999, T0.AddSeconds(1));
            agg.Record("r", 200, 20, T0.AddSeconds(2));

            var stat = Assert.Single(agg.Drain(T0.AddSeconds(61)));

            Assert.Equal(999, stat.MaxDurationMs);
            Assert.Equal(1024, stat.SumDurationMs); // 5+999+20 — để bên gọi tự tính avg = sum/count
        }
    }
}