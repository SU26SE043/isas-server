using Isas.PaymentService.Models;

namespace Isas.PaymentService.Services
{
    /// <summary>
    /// Chọn return/cancel URL cho PayOS: ưu tiên URL do FE truyền (redirect về đúng khu vực người mua —
    /// candidate vs employer), fallback config chung <c>PayOS:ReturnUrl/CancelUrl</c>. Chỉ chấp nhận URL
    /// http(s) TUYỆT ĐỐI (chống open-redirect / URL rác); request không hợp lệ → coi như không truyền → dùng
    /// config. Kết quả rỗng cả 2 nguồn → <see cref="PaymentGatewayException"/> (BF3 — 502 sạch, no order mồ côi).
    /// </summary>
    public static class PayosUrlResolver
    {
        public static (string ReturnUrl, string CancelUrl) Resolve(
            string? reqReturn, string? reqCancel, PayOSSettings cfg)
        {
            var returnUrl = Pick(reqReturn, cfg.ReturnUrl);
            var cancelUrl = Pick(reqCancel, cfg.CancelUrl);

            if (string.IsNullOrWhiteSpace(returnUrl) || string.IsNullOrWhiteSpace(cancelUrl))
                throw new PaymentGatewayException(
                    "PayOS ReturnUrl/CancelUrl chưa cấu hình (set PayOS__ReturnUrl / PayOS__CancelUrl).");

            return (returnUrl, cancelUrl);
        }

        private static string Pick(string? requested, string? fallback) =>
            IsAbsoluteHttpUrl(requested) ? requested!.Trim() : (fallback ?? string.Empty);

        public static bool IsAbsoluteHttpUrl(string? url) =>
            !string.IsNullOrWhiteSpace(url)
            && Uri.TryCreate(url.Trim(), UriKind.Absolute, out var u)
            && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps);
    }
}
