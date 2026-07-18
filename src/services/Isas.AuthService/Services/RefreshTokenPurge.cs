using Isas.AuthService.Models;
using Microsoft.EntityFrameworkCore;

namespace Isas.AuthService.Services
{
    /// <summary>
    /// DB28 — lõi dọn <c>refresh_tokens</c>, tách khỏi <see cref="RefreshTokenPurger"/> để unit-test
    /// gọi thẳng (không phải dựng BackgroundService + đợi timer).
    ///
    /// ⚠ ĐÂY LÀ CODE XOÁ DỮ LIỆU — bất biến an toàn phải giữ:
    /// <list type="number">
    ///   <item><b>Không bao giờ xoá token còn dùng được.</b> "Còn dùng được" = (a) chưa hết hạn và chưa
    ///   thu hồi, HOẶC (b) đã bị XOAY VÒNG nhưng còn trong cửa sổ ân hạn AUTH-9 (<c>replaced_by</c> trỏ
    ///   tới token thay thế vừa tạo). Cả hai vế đều nằm ngoài vị ngữ xoá bên dưới.</item>
    ///
    ///   <item><b>Không bao giờ cắt mắt xích <c>replaced_by</c> đang trong ân hạn.</b>
    ///   <c>ResolveGraceReplacementAsync</c> đi theo chuỗi <c>replaced_by</c>, nên xoá token THAY THẾ
    ///   trong khi token bị xoay còn sống sẽ làm hỏng refresh của người đang mở nhiều tab. Bất biến cứu
    ///   ta ở đây: token thay thế LUÔN trẻ hơn token nó thay (cả <c>created_at</c> lẫn <c>expires_at</c>,
    ///   vì nó được tạo đúng lúc token cũ bị xoay và nhận hạn mới). Vị ngữ xoá chỉ dùng 2 mốc đó, nên
    ///   một mắt xích KHÔNG THỂ bị xoá trước mắt xích đứng trước nó — không có ca "predecessor sống mà
    ///   successor đã mất".</item>
    ///
    ///   <item><b>Ngưỡng giữ luôn rộng hơn cửa sổ ân hạn.</b> Ân hạn cấu hình bằng GIÂY nhưng vẫn có thể
    ///   bị đặt lớn bất thường; <see cref="ResolveRetentionDays"/> nâng ngưỡng giữ theo nó nên hai tham số
    ///   này không thể chồng lấn dù cấu hình sai tay.</item>
    /// </list>
    /// </summary>
    public static class RefreshTokenPurge
    {
        /// <summary>
        /// Ngưỡng giữ THỰC TẾ (ngày): lấy cấu hình, kẹp sàn <see cref="RefreshTokenRetentionSettings.MinRetentionDays"/>,
        /// rồi nâng lên trên cửa sổ ân hạn AUTH-9 + 1 ngày đệm.
        ///
        /// Vì sao phải nâng theo ân hạn: token bị xoay vòng chỉ chết hẳn SAU khi hết ân hạn. Nếu ai đó
        /// đặt <c>Jwt:RefreshTokenGraceSeconds</c> lớn hơn ngưỡng giữ thì job này sẽ xoá đúng những token
        /// vẫn đang được ân hạn — người dùng nhiều tab bị đá về trang đăng nhập. Nâng ngưỡng ở đây khiến
        /// cấu hình sai KHÔNG thể tạo ra ca đó.
        /// </summary>
        public static int ResolveRetentionDays(RefreshTokenRetentionSettings settings, int graceSeconds)
        {
            var configured = settings.RetentionDays >= RefreshTokenRetentionSettings.MinRetentionDays
                ? settings.RetentionDays
                : RefreshTokenRetentionSettings.DefaultRetentionDays;

            if (graceSeconds <= 0) return configured;

            var graceDays = (int)Math.Ceiling(graceSeconds / 86_400d);
            return Math.Max(configured, graceDays + 1);
        }

        /// <summary>
        /// Xoá row đã chết, theo batch. Trả về số row thực xoá (để log/quan sát).
        /// </summary>
        /// <param name="graceSeconds">
        /// <c>Jwt:RefreshTokenGraceSeconds</c> — chỉ dùng để NÂNG ngưỡng giữ, không nới lỏng nó.
        /// </param>
        public static async Task<int> PurgeAsync(
            AuthDbContext db,
            DateTime nowUtc,
            RefreshTokenRetentionSettings settings,
            int graceSeconds,
            CancellationToken ct = default)
        {
            var cutoff = nowUtc.AddDays(-ResolveRetentionDays(settings, graceSeconds));
            var batchSize = settings.EffectiveBatchSize;
            var maxBatches = settings.EffectiveMaxBatchesPerRun;

            var deleted = 0;

            for (var batch = 0; batch < maxBatches; batch++)
            {
                // VỊ NGỮ XOÁ — hai lớp, cả hai đều chỉ chạm row đã chết hẳn:
                //  (a) expires_at < cutoff  → hết hạn từ lâu; RefreshTokenAsync trả 401 "expired" dù
                //      có xuất trình. Phủ luôn token chưa revoke (hết hạn tự nhiên) lẫn đã revoke.
                //  (b) is_revoked && created_at < cutoff → đã thu hồi từ lâu. Bắt được ca thu hồi
                //      THẲNG TAY (đăng xuất/đổi quyền, replaced_by NULL) — những row này giữ nguyên
                //      expires_at tương lai nên vế (a) không dọn nổi, mà chúng thì không bao giờ dùng
                //      lại được nữa (không revoke-ngược, không ân hạn).
                // Token còn sống (chưa hết hạn + chưa revoke) KHÔNG khớp vế nào.
                var ids = await db.RefreshTokens
                    .AsNoTracking()
                    .Where(t => t.ExpiresAt < cutoff || (t.IsRevoked && t.CreatedAt < cutoff))
                    .OrderBy(t => t.ExpiresAt)
                    .Select(t => t.Id)
                    .Take(batchSize)
                    .ToListAsync(ct);

                if (ids.Count == 0) break;

                // Xoá theo danh sách Id đã chốt (không ExecuteDelete thẳng trên query có Take — không
                // phải provider nào cũng dịch được LIMIT trong DELETE).
                deleted += await db.RefreshTokens
                    .Where(t => ids.Contains(t.Id))
                    .ExecuteDeleteAsync(ct);

                if (ids.Count < batchSize) break;   // batch cuối → hết ứng viên
            }

            return deleted;
        }
    }
}
