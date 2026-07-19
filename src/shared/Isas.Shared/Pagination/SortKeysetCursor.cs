using System.Text;
using Microsoft.AspNetCore.WebUtilities;

namespace Isas.Shared.Pagination;

/// <summary>
/// Opaque keyset (seek) cursor cho ordering mà khoá DẪN KHÔNG phải <c>created_at</c> — ví dụ shortlist
/// sàng CV xếp theo <c>overall_match_score DESC</c>, hay danh sách xếp theo tên. Khác
/// <see cref="KeysetCursor"/> (cố định <c>(CreatedAt, Id)</c>) ở chỗ khoá dẫn là một CHUỖI tuỳ ý do
/// caller tự diễn giải (số, tên đã lower-case…), còn <c>Id</c> vẫn là tie-break duy nhất-toàn-cục.
/// <para>
/// ⚠ <b>Hợp đồng bắt buộc:</b> khoá dẫn PHẢI không-NULL ở cả <c>ORDER BY</c> lẫn predicate keyset —
/// caller chuẩn hoá bằng <c>COALESCE</c> (vd <c>score ?? -1</c>, <c>full_name ?? ''</c>). NULL trong
/// khoá keyset là bẫy kinh điển: <c>NULL &lt; x</c> cho UNKNOWN nên predicate loại nhầm cả trang,
/// và thứ tự NULL còn khác nhau giữa Postgres (NULLS FIRST khi DESC) và SQLite. Ép non-null ở cả hai
/// vế là cách rẻ nhất để phân trang không lệ thuộc provider.
/// </para>
/// <para>
/// Wire form: base64 URL-safe của <c>"{key}:{id:N}"</c>, PARSE TỪ PHẢI SANG (32 hex cuối = id, ký tự
/// ngay trước là dấu phân cách) nên <c>key</c> được phép chứa cả dấu <c>':'</c>. Decode là TOÀN PHẦN —
/// mọi giá trị hỏng đều ra <c>null</c> ("trang đầu"), không bao giờ ném ⇒ cursor rác không thành 500.
/// </para>
/// </summary>
public sealed record SortKeysetCursor(string Key, Guid Id)
{
    private const int IdHexLength = 32;

    // "{key}:{id:N}" — độ dài tối thiểu khi key rỗng = 1 (':') + 32 (id).
    private const int MinRawLength = 1 + IdHexLength;

    public string Encode()
    {
        var raw = $"{Key}:{Id:N}";
        return WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(raw));
    }

    /// <summary>Decode; trả <c>null</c> cho input rỗng/hỏng (= trang đầu).</summary>
    public static SortKeysetCursor? Decode(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor))
            return null;

        try
        {
            var raw = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(cursor));
            if (raw.Length < MinRawLength)
                return null;

            // Parse từ PHẢI: id là 32 hex cuối, ngay trước nó là ':' → key giữ được ký tự ':' bên trong.
            var sep = raw.Length - IdHexLength - 1;
            if (raw[sep] != ':')
                return null;
            if (!Guid.TryParseExact(raw.AsSpan(sep + 1), "N", out var id))
                return null;

            return new SortKeysetCursor(raw[..sep], id);
        }
        catch (FormatException)
        {
            // Base64UrlDecode ném FormatException với input không hợp lệ — nuốt để về "trang đầu".
            return null;
        }
    }

    /// <summary>
    /// Khoá dẫn dưới dạng số nguyên (cho ordering theo điểm). Trả <c>null</c> nếu cursor mang khoá
    /// không phải số — caller coi như trang đầu thay vì dựng predicate với giá trị rác.
    /// </summary>
    public int? KeyAsInt() =>
        int.TryParse(Key, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var v)
            ? v
            : null;

    /// <summary>Dựng cursor từ khoá số (đối xứng với <see cref="KeyAsInt"/>).</summary>
    public static SortKeysetCursor FromInt(int key, Guid id) =>
        new(key.ToString(System.Globalization.CultureInfo.InvariantCulture), id);
}
