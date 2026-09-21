using System.Text;

namespace Isas.Shared.Files;

/// <summary>
/// Lọc ký tự điều khiển khỏi text trích từ PDF trước khi nó chạm DB.
///
/// <para><b>Vì sao:</b> PdfPig trả về <c>\0</c> cho glyph mà font không khai bảng Unicode (đo trên prod
/// 21/09: CV PDF của một trang mẫu có chữ ghép "fi" ⇒ <c>"Pro\0cient"</c>, 6 lần). Postgres cấm
/// <c>\0</c> trong cột <c>text</c> ⇒ <c>22021 invalid byte sequence for encoding "UTF8": 0x00</c>
/// ở INSERT <c>cv_submission</c>, và vì cả lô CV lưu trong một SaveChanges nên MỘT file hỏng kéo
/// cả 4 file đổ theo, FE chỉ còn câu "Không thể phân tích CV". Đường B2C (<c>cv_analyses</c>,
/// <c>file_records.parsed_text</c>) cũng là cột text ⇒ cùng file đó hỏng y hệt — nên lọc ở bộ
/// trích DÙNG CHUNG (DB17), không lọc riêng từng service.</para>
///
/// <para><b>Giữ lại:</b> <c>\n</c> <c>\r</c> <c>\t</c> (cấu trúc dòng/cột của CV). <b>Bỏ:</b> mọi
/// <see cref="char.IsControl(char)"/> khác — C0 (<c>\0</c>…<c>\x1F</c>) lẫn C1 (<c>\x80</c>…<c>\x9F</c>).
/// Không thay bằng ký tự khác: <c>\0</c> ở đây nghĩa là "không biết glyph gì", đoán "fi" là bịa.</para>
/// </summary>
public static class ExtractedTextSanitizer
{
    /// <summary>Trả về text đã lọc; <paramref name="removed"/> = số ký tự bị bỏ (0 = text nguyên vẹn, trả cùng instance).</summary>
    public static string Strip(string text, out int removed)
    {
        ArgumentNullException.ThrowIfNull(text);
        removed = 0;
        foreach (var c in text)
            if (IsDisallowed(c)) removed++;
        if (removed == 0)
            return text;

        var sb = new StringBuilder(text.Length - removed);
        foreach (var c in text)
            if (!IsDisallowed(c)) sb.Append(c);
        return sb.ToString();
    }

    public static string Strip(string text) => Strip(text, out _);

    private static bool IsDisallowed(char c)
        => char.IsControl(c) && c != '\n' && c != '\r' && c != '\t';
}
