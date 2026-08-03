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

            // (1) Khớp NGUYÊN mã trước — để ops ghim được một mã cụ thể khi cần, và mục tường minh
            // luôn thắng mọi suy diễn bên dưới.
            if (TryMap(raw, out var exact)) return exact;

            // (2) Mã CITAD 8 số → lấy 3 số GIỮA làm mã ngân hàng. Cấu trúc
            // [2 số tỉnh][3 số tổ chức][3 số chi nhánh] được xác minh bằng dữ liệu, không phải phỏng đoán:
            // trong danh sách CITAD do ngân hàng công bố, 695 mã của Kho Bạc Nhà nước trải 63 tỉnh đều
            // mang cùng 3 số giữa `701`, và mọi nhóm ngân hàng thương mại đều thuần đúng một tên.
            //
            // Khớp theo 3 số này thay vì cả 8 số là điều BẮT BUỘC, vì mã CITAD phân biệt tới CHI NHÁNH:
            // khách mở tài khoản ở chi nhánh khác sẽ mang mã khác, và bảng khớp-cả-8-số sẽ trượt họ.
            if (IsCitad(raw) && TryMap(raw.Substring(2, 3), out var bySegment)) return bySegment;

            // (3) Vốn đã là BIN (payOS trả BIN với một số ngân hàng, CITAD với số khác) → dùng thẳng.
            return IsBin(raw) ? raw : null;
        }

        // Giá trị trong bảng phải là BIN hợp lệ; một dòng cấu hình gõ sai KHÔNG được biến thành lệnh
        // chuyển tiền đi mù — thà trả null để rơi về chuyển tay.
        private bool TryMap(string key, out string? bin)
        {
            bin = null;
            if (!_options.BankBinMap.TryGetValue(key, out var mapped)) return false;
            var trimmed = mapped?.Trim();
            if (!IsBin(trimmed)) return false;
            bin = trimmed;
            return true;
        }

        private static bool IsBin(string? value) =>
            value is { Length: 6 } && value.All(char.IsAsciiDigit);

        private static bool IsCitad(string value) =>
            value.Length == 8 && value.All(char.IsAsciiDigit);
    }
}
