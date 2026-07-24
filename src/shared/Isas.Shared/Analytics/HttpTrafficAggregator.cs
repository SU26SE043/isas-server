namespace Isas.Shared.Analytics
{
    public sealed record TrafficWindowStat(
        DateTime WindowStart,
        DateTime WindowEnd,
        string RouteId,
        string StatusClass,
        int Requests,
        long SumDurationMs,
        int MaxDurationMs);

    /// <summary>
    /// FR18 (Phần B) — gộp traffic HTTP trong bộ nhớ theo cửa sổ thời gian cố định, để Gateway đẩy định kỳ
    /// về Payment thay vì 1 request = 1 dòng ghi DB. Lớp THUẦN (không phụ thuộc ASP.NET/DI) — Gateway
    /// không có test project riêng, đặt logic ở đây là cách duy nhất unit-test được nó.
    ///
    /// Cố ý AT-MOST-ONCE (không phải outbox DB2): đây là telemetry, không phải tiền. Cửa sổ đã đóng mà
    /// vượt <c>maxPendingWindows</c> (sink chết lâu / flush không kịp) → BỎ cửa sổ cũ nhất, không giữ vô hạn.
    /// </summary>
    public sealed class HttpTrafficAggregator
    {
        private readonly object _lock = new();
        private readonly int _windowSeconds;
        private readonly int _maxPendingWindows;

        private DateTime _currentWindowStart;
        private Dictionary<(string RouteId, string StatusClass), MutableStat> _current = new();

        // Cửa sổ đã đóng (thời gian đã trôi qua) nhưng chưa được Drain() lấy đi.
        private readonly LinkedList<ClosedWindow> _pending = new();

        public event Action<string>? OnWindowDropped;

        public HttpTrafficAggregator(int windowSeconds, int maxPendingWindows)
        {
            if (windowSeconds <= 0) throw new ArgumentOutOfRangeException(nameof(windowSeconds));
            _windowSeconds = windowSeconds;
            _maxPendingWindows = Math.Max(1, maxPendingWindows);
        }

        private sealed class MutableStat
        {
            public int Requests;
            public long SumDurationMs;
            public int MaxDurationMs;
        }

        private sealed record ClosedWindow(
            DateTime Start, DateTime End,
            Dictionary<(string RouteId, string StatusClass), MutableStat> Data);

        public void Record(string routeId, int statusCode, long elapsedMs, DateTime? nowUtc = null)
        {
            var now = nowUtc ?? DateTime.UtcNow;
            var statusClass = StatusClassOf(statusCode);

            lock (_lock)
            {
                RollWindowIfNeeded(now);

                var key = (routeId, statusClass);
                if (!_current.TryGetValue(key, out var stat))
                {
                    stat = new MutableStat();
                    _current[key] = stat;
                }

                stat.Requests++;
                stat.SumDurationMs += elapsedMs;
                if (elapsedMs > stat.MaxDurationMs) stat.MaxDurationMs = (int)elapsedMs;
            }
        }

        /// <summary>
        /// Trả TOÀN BỘ cửa sổ đã đóng còn đang chờ + xoá khỏi bộ nhớ. Cửa sổ ĐANG MỞ không được trả
        /// (chưa đủ dữ liệu — trả sớm sẽ phải gửi lại cửa sổ đó lần sau với số khác, sai số liệu).
        /// </summary>
        public IReadOnlyList<TrafficWindowStat> Drain(DateTime? nowUtc = null)
        {
            lock (_lock)
            {
                RollWindowIfNeeded(nowUtc ?? DateTime.UtcNow);

                var result = new List<TrafficWindowStat>();
                foreach (var window in _pending)
                {
                    foreach (var ((routeId, statusClass), stat) in window.Data)
                    {
                        result.Add(new TrafficWindowStat(
                            window.Start, window.End, routeId, statusClass,
                            stat.Requests, stat.SumDurationMs, stat.MaxDurationMs));
                    }
                }

                _pending.Clear();
                return result;
            }
        }

        // Gọi dưới _lock. Đóng cửa sổ hiện tại vào _pending nếu thời gian đã vượt qua windowEnd,
        // rồi mở cửa sổ mới. Tự trôi theo thời gian THẬT dù không ai gọi Record/Drain trong lúc đó.
        private void RollWindowIfNeeded(DateTime now)
        {
            if (_currentWindowStart == default)
            {
                _currentWindowStart = FloorToWindow(now);
                return;
            }

            var windowEnd = _currentWindowStart.AddSeconds(_windowSeconds);
            if (now < windowEnd) return;

            if (_current.Count > 0)
            {
                _pending.AddLast(new ClosedWindow(_currentWindowStart, windowEnd, _current));

                while (_pending.Count > _maxPendingWindows)
                {
                    var dropped = _pending.First!.Value;
                    _pending.RemoveFirst();
                    OnWindowDropped?.Invoke(
                        $"Bỏ cửa sổ traffic {dropped.Start:o}–{dropped.End:o} do tràn MaxPendingWindows ({_maxPendingWindows}).");
                }
            }

            _current = new Dictionary<(string, string), MutableStat>();
            _currentWindowStart = FloorToWindow(now);
        }

        private DateTime FloorToWindow(DateTime now)
        {
            var epochSeconds = (long)(now - DateTime.UnixEpoch).TotalSeconds;
            var flooredSeconds = epochSeconds - (epochSeconds % _windowSeconds);
            return DateTime.UnixEpoch.AddSeconds(flooredSeconds);
        }

        private static string StatusClassOf(int statusCode) => (statusCode / 100) switch
        {
            2 => "2xx",
            3 => "3xx",
            4 => "4xx",
            5 => "5xx",
            _ => "other"
        };
    }
}
