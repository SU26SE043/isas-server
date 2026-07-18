using Isas.AuthService.DTOs;
using Isas.AuthService.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Internal;

namespace Isas.AuthService.Tests;

/// <summary>
/// Mã dùng-một-lần thay mặt cho CẢ access + refresh token trong quãng đường về FE. Ba tính chất giữ
/// nó an toàn — dùng một lần, hết hạn nhanh, không đoán được — nên phải có test khoá lại từng cái.
/// </summary>
public class GoogleAuthCodeStoreTests
{
    [Fact]
    public void DoiMaHopLe_TraVeDungPhien()
    {
        var store = Store();
        var auth = Auth("access-1", "refresh-1");

        var code = store.Issue(auth);
        var got = store.Consume(code);

        Assert.NotNull(got);
        Assert.Equal("access-1", got!.AccessToken);
        Assert.Equal("refresh-1", got.RefreshToken);
    }

    // Tính chất quan trọng nhất: mã lọt ra ngoài (log proxy, lịch sử trình duyệt, extension đọc
    // query) cũng vô dụng vì FE đã tiêu nó rồi.
    [Fact]
    public void DoiLanThuHai_ThatBai()
    {
        var store = Store();
        var code = store.Issue(Auth());

        Assert.NotNull(store.Consume(code));
        Assert.Null(store.Consume(code));
        Assert.Null(store.Consume(code));
    }

    [Fact]
    public void MaHetHan_ThatBai()
    {
        var clock = new TestClock(new DateTimeOffset(2026, 7, 18, 10, 0, 0, TimeSpan.Zero));
        var store = Store(ttlSeconds: 60, clock: clock);
        var code = store.Issue(Auth());

        clock.Advance(TimeSpan.FromSeconds(61));           // > TTL 60s

        Assert.Null(store.Consume(code));
    }

    [Fact]
    public void MaConHan_VanDoiDuoc()
    {
        var clock = new TestClock(new DateTimeOffset(2026, 7, 18, 10, 0, 0, TimeSpan.Zero));
        var store = Store(ttlSeconds: 60, clock: clock);
        var code = store.Issue(Auth());

        clock.Advance(TimeSpan.FromSeconds(30));

        Assert.NotNull(store.Consume(code));
    }

    // "Dùng một lần" phải đúng cả khi BỊ ĐUA — đúng kịch bản kẻ đọc trộm mã sẽ chạy: đổi song song
    // với FE để chen vào khe giữa TryGetValue và Remove.
    // Dùng Barrier + thread thật (không Task.Run: thread-pool hay xếp tuần tự nên không tái hiện
    // được race) và lặp nhiều vòng — bỏ khoá trong Consume là test này đỏ.
    [Fact]
    public void NhieuRequestDoiCungLuc_ChiMotCaiThanhCong()
    {
        var store = Store();

        for (var round = 0; round < 200; round++)
        {
            const int racers = 8;
            var code = store.Issue(Auth());
            var winners = 0;
            using var barrier = new Barrier(racers);

            var threads = Enumerable.Range(0, racers).Select(_ => new Thread(() =>
            {
                barrier.SignalAndWait();                    // đồng loạt lao vào Consume
                if (store.Consume(code) is not null) Interlocked.Increment(ref winners);
            })).ToList();

            foreach (var t in threads) t.Start();
            foreach (var t in threads) t.Join();

            Assert.Equal(1, winners);
        }
    }

    [Theory]
    [InlineData("khong-ton-tai")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void MaBia_ThatBai(string? code)
    {
        var store = Store();
        store.Issue(Auth());                                // có mã thật trong kho, nhưng không phải mã này

        Assert.Null(store.Consume(code));
    }

    // Mã phải không đoán được: entropy mật mã, không phải Guid (122 bit, không cam kết nguồn CSPRNG).
    [Fact]
    public void MaCoDoNgauNhienCao_VaKhongTrungNhau()
    {
        var store = Store();

        var codes = Enumerable.Range(0, 200).Select(_ => store.Issue(Auth())).ToList();

        Assert.Equal(200, codes.Distinct().Count());
        // 32 byte → 43 ký tự base64url. Guid dạng "N" chỉ 32 ký tự hex → chốt chặn nếu ai đó đổi về Guid.
        Assert.All(codes, c => Assert.True(c.Length >= 40, $"Mã quá ngắn: {c.Length} ký tự"));
        // base64url: không có ký tự cần escape trong URL.
        Assert.All(codes, c => Assert.Matches("^[A-Za-z0-9_-]+$", c));
    }

    // TTL cấu hình sai không được biến mã ngắn hạn thành mã sống rất lâu.
    [Fact]
    public void TtlCauHinhQuaLon_BiKepLai()
    {
        var clock = new TestClock(new DateTimeOffset(2026, 7, 18, 10, 0, 0, TimeSpan.Zero));
        var store = Store(ttlSeconds: 999_999, clock: clock);
        var code = store.Issue(Auth());

        clock.Advance(TimeSpan.FromHours(1));               // > trần 600s

        Assert.Null(store.Consume(code));
    }

    private static GoogleAuthCodeStore Store(int? ttlSeconds = null, ISystemClock? clock = null)
    {
        var options = new MemoryCacheOptions();
        if (clock is not null) options.Clock = clock;

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Authentication:Google:OneTimeCodeTtlSeconds"] = ttlSeconds?.ToString()
        }).Build();

        return new GoogleAuthCodeStore(new MemoryCache(options), config);
    }

    private static AuthResponse Auth(string access = "a", string refresh = "r") =>
        new() { AccessToken = access, RefreshToken = refresh, ExpiresAt = DateTime.UtcNow.AddMinutes(15) };

    /// <summary>Đồng hồ giả để test hết hạn mà không phải ngủ thật.</summary>
    private sealed class TestClock(DateTimeOffset now) : ISystemClock
    {
        public DateTimeOffset UtcNow { get; private set; } = now;

        public void Advance(TimeSpan by) => UtcNow = UtcNow.Add(by);
    }
}
