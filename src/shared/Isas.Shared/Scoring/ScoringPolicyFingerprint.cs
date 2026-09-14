using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Isas.Shared.Scoring;

/// <summary>
/// SCP1 · HĐ-4 — VÂN TAY của một chính sách chấm, để nối XEM TRƯỚC ↔ ÁP: HR xem trước một cấu hình,
/// server tính vân tay; lúc <c>apply</c> server tính LẠI vân tay từ dòng chính sách đã lưu và so —
/// lệch ⇒ <c>409 POLICY_CHANGED_AFTER_PREVIEW</c> (ai đó đã đổi biểu thức giữa hai bước ⇒ bảng HR vừa
/// xem không còn đúng).
///
/// <para>Vân tay = <c>sha256(expression + passScorePct + engineVersion + danh sách tên biến)</c>.
/// Gộp cả <b>danh sách tên biến</b> (HĐ-4) chứ không chỉ chuỗi biểu thức: khi HĐ-1 thêm biến mới
/// (append-only) thì hai biểu thức khác nhau về TẬP BIẾN chạy ra kết quả khác nhau kể cả khi trông
/// giống — vân tay phải bắt được điều đó. Danh sách biến lấy từ <see cref="ScoringExpression.Parse"/>
/// (đã distinct + sắp ordinal) nên tất định.</para>
/// </summary>
public static class ScoringPolicyFingerprint
{
    public static string Compute(string? expression, int? passScorePct, string? engineVersion)
    {
        var expr = expression ?? string.Empty;
        var parsed = ScoringExpression.Parse(expr);
        var vars = string.Join(',', parsed.ReferencedVariables);

        // Ngăn cách bằng '\n' — ký tự KHÔNG hợp lệ trong biểu thức HĐ-1 (lexer chỉ nhận số/định danh/
        // toán tử/khoảng trắng ngang) nên không có va chạm kiểu "a\nb" vs "a" + "\nb".
        var canonical = string.Join('\n',
            expr,
            passScorePct?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            engineVersion ?? string.Empty,
            vars);

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
