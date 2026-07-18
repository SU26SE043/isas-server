using System.Buffers.Text;
using System.Security.Cryptography;
using Isas.AuthService.DTOs;
using Microsoft.Extensions.Caching.Memory;

namespace Isas.AuthService.Services
{
    /// <summary>
    /// Kho mã dùng-một-lần cho vòng đăng nhập Google.
    /// <para>
    /// Vì sao cần: OAuth là điều hướng CẢ TRANG nên phiên phải quay về FE qua URL. Đính thẳng
    /// access/refresh token vào URL (query hay fragment) đều để lộ — query lọt access log + header
    /// <c>Referer</c>, fragment thì nằm trong <c>location.hash</c> nên extension trình duyệt đọc
    /// được. Thay vào đó callback chỉ mang một <b>mã tham chiếu</b> vô dụng nếu không đổi được:
    /// muốn lấy token phải POST <c>/auth/google/exchange</c>, và mã chết ngay sau lần đổi đầu tiên.
    /// </para>
    /// </summary>
    public interface IGoogleAuthCodeStore
    {
        /// <summary>Phát mã ngắn hạn trỏ tới phiên vừa tạo. Trả về mã để ghép vào URL redirect.</summary>
        string Issue(AuthResponse auth);

        /// <summary>
        /// Đổi mã lấy phiên. <b>Dùng một lần</b>: đổi thành công là mã chết ngay, lần sau trả
        /// <c>null</c>. Mã sai / hết hạn / đã dùng đều trả <c>null</c> (không phân biệt ra ngoài).
        /// </summary>
        AuthResponse? Consume(string? code);
    }

    public sealed class GoogleAuthCodeStore : IGoogleAuthCodeStore
    {
        /// <summary>
        /// 32 byte ngẫu nhiên mật mã (256 bit) — KHÔNG dùng <see cref="Guid"/>: Guid.NewGuid chỉ có
        /// 122 bit ngẫu nhiên và không cam kết dùng nguồn ngẫu nhiên mật mã.
        /// </summary>
        private const int CodeBytes = 32;

        private const string CacheKeyPrefix = "google-otc:";
        private const int DefaultTtlSeconds = 60;
        private const int MinTtlSeconds = 5;
        private const int MaxTtlSeconds = 600;

        private readonly IMemoryCache _cache;
        private readonly TimeSpan _ttl;

        /// <summary>
        /// <see cref="IMemoryCache"/> không có thao tác "đọc-và-xoá" nguyên tử, mà chính tính
        /// dùng-một-lần phụ thuộc vào đó: 2 request đổi cùng mã cùng lúc mà chỉ TryGetValue rồi
        /// Remove thì cả hai đều lấy được phiên. Khoá quanh cặp thao tác này (rất ngắn, tần suất
        /// thấp — mỗi lần đăng nhập Google đúng 1 lần).
        /// </summary>
        private readonly Lock _consumeLock = new();

        public GoogleAuthCodeStore(IMemoryCache cache, IConfiguration configuration)
        {
            _cache = cache;

            var configured = configuration.GetValue<int?>("Authentication:Google:OneTimeCodeTtlSeconds")
                             ?? DefaultTtlSeconds;
            // Kẹp lại: cấu hình sai (0/âm/quá dài) không được biến mã ngắn hạn thành mã sống mãi.
            _ttl = TimeSpan.FromSeconds(Math.Clamp(configured, MinTtlSeconds, MaxTtlSeconds));
        }

        public string Issue(AuthResponse auth)
        {
            ArgumentNullException.ThrowIfNull(auth);

            var code = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(CodeBytes));

            // TTL tuyệt đối: mã chỉ đủ sống cho cú redirect + 1 lần POST của FE, không gia hạn khi
            // truy cập. Hết hạn thì MemoryCache tự dọn — không cần sweeper.
            _cache.Set(CacheKeyPrefix + code, auth, _ttl);

            return code;
        }

        public AuthResponse? Consume(string? code)
        {
            if (string.IsNullOrWhiteSpace(code)) return null;

            var key = CacheKeyPrefix + code;
            lock (_consumeLock)
            {
                if (!_cache.TryGetValue<AuthResponse>(key, out var auth) || auth is null)
                    return null;

                _cache.Remove(key);
                return auth;
            }
        }
    }
}
