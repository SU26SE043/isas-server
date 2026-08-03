using Microsoft.Extensions.Options;
using PaymentService.Models;

namespace Isas.PaymentService.Services
{
    /// <summary>
    /// Đổi <c>counterAccountBankId</c> (webhook thu) sang <c>toBin</c> (lệnh chi). <c>null</c> = KHÔNG
    /// resolve được ⇒ không chi tự động.
    /// </summary>
    public interface IBankBinResolver
    {
        string? Resolve(string? counterAccountBankId);
    }

    /// <summary>
    /// <para><b>Vì sao cần lớp này thay vì dùng thẳng <c>counterAccountBankId</c>:</b> hai trường đó
    /// KHÔNG cùng hệ mã. Đo trên dữ liệu thật của hệ thống: 12/15 giao dịch trả mã <b>8 chữ số</b>
    /// (<c>01203001</c>, <c>01358001</c>), chỉ 3/15 trả mã <b>6 chữ số</b> đúng dạng BIN NAPAS
    /// (<c>970422</c>) — trong khi <c>toBin</c> mà payOS nhận là BIN 6 số. Ném thẳng mã 8 số vào lệnh chi
    /// là sai với đa số giao dịch. Tài liệu payOS không định nghĩa định dạng của
    /// <c>counterAccountBankId</c> và cũng không có API tra cứu ngân hàng, nên bảng ánh xạ phải do người
    /// điền sau khi hỏi payOS.</para>
    ///
    /// <para><b>Fail-closed.</b> Không biết thì trả <c>null</c> để rơi về chuyển tay, KHÔNG đoán. Đoán
    /// sai ở đây không phải là một lệnh lỗi — nó là tiền vào tài khoản của người khác, và không có bước
    /// nào sau đó bắt lại được.</para>
    /// </summary>
    public class BankBinResolver : IBankBinResolver
    {
        private readonly RefundPayoutSettings _options;

        public BankBinResolver(IOptions<RefundPayoutSettings> options)
        {
            _options = options.Value;
        }

        public string? Resolve(string? counterAccountBankId)
        {
            var raw = counterAccountBankId?.Trim();
            if (string.IsNullOrEmpty(raw)) return null;

            // Bảng ánh xạ do ops điền (config, không cần deploy) — tra TRƯỚC để một mục cấu hình sai
            // sửa được ngay, và để mục tường minh luôn thắng suy đoán bên dưới.
            if (_options.BankBinMap.TryGetValue(raw, out var mapped))
            {
                var trimmed = mapped?.Trim();
                return IsBin(trimmed) ? trimmed : null;
            }

            // Vốn đã là BIN → dùng thẳng. Nếu một mã 6 số nào đó hoá ra không phải BIN thì
            // ValidateDestination phía payOS chặn lại, chứ không thành lệnh chuyển đi mù.
            return IsBin(raw) ? raw : null;
        }

        private static bool IsBin(string? value) =>
            value is { Length: 6 } && value.All(char.IsAsciiDigit);
    }
}
