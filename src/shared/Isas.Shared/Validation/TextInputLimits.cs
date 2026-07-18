namespace Isas.Shared.Validation;

/// <summary>
/// Ngưỡng độ dài CHUNG cho JD/tiêu chí người dùng nhập THẲNG dạng text (không qua file PDF).
/// Text này đi thẳng vào prompt Gemini nên nếu không chặn thì 1 request có thể mang khối lượng
/// tuỳ ý vào một lời gọi AI tính phí.
///
/// Vì sao 20.000 ký tự (một ngưỡng duy nhất cho CẢ B2B/Campaign lẫn B2C/Interview):
///   • JD thật: khuyến nghị tuyển dụng là 300–700 từ (~2.000–5.000 ký tự); JD doanh nghiệp dài dòng
///     kèm boilerplate (giới thiệu công ty, phúc lợi, cam kết bình đẳng) hiếm khi quá 8.000–10.000
///     ký tự. 20.000 ≈ 4× mức khuyến nghị và ~2× JD dài nhất thực tế → KHÔNG chặn nhầm JD hợp lệ.
///   • Cửa sổ ngữ cảnh KHÔNG phải ràng buộc: gemini-2.5-flash nhận ~1.048.576 token input. 20.000 ký
///     tự tiếng Việt ≈ 7.000–10.000 token (tiếng Việt tốn token hơn tiếng Anh) — dưới 1% cửa sổ, nên
///     một JD không bao giờ chèn ép được CV/transcript đi kèm trong cùng prompt.
///   • Ràng buộc THẬT là chi phí token + bề mặt lạm dụng: cap ở 20.000 ký tự (~20 KB) giữ phần đóng
///     góp xấu nhất của JD vào mỗi lời gọi Gemini ở mức bounded, dự đoán được.
///   • Tiêu chí (criteriaText) bản chất ngắn hơn JD → dùng chung ngưỡng này là rộng rãi, đồng thời
///     tránh việc người dùng phải nhớ hai con số khác nhau.
///
/// Ngưỡng áp cho text ĐÃ CHUẨN HOÁ (trim) — xem <see cref="NormalizeAndEnsureLimit"/>.
/// Ngưỡng này KHÔNG áp cho text trích từ PDF upload (luồng khác, đã giới hạn bằng cỡ file 10MB).
/// </summary>
public static class TextInputLimits
{
    /// <summary>Số ký tự tối đa cho JD/tiêu chí nhập tay. Dùng chung B2B + B2C (đừng cap lẻ từng bên).</summary>
    public const int JdTextMaxChars = 20_000;

    /// <summary>
    /// Chuẩn hoá TRƯỚC rồi mới đo (khớp NormalizeText sẵn có: rỗng/toàn khoảng trắng → null, còn lại
    /// thì trim) → khoảng trắng thừa không tính vào ngưỡng. Vượt ngưỡng → gọi <paramref name="onTooLong"/>
    /// để mỗi service ném đúng loại exception mà controller của nó map sang 400.
    /// </summary>
    public static string? NormalizeAndEnsureLimit(
        string? text, string fieldLabel, Func<string, Exception> onTooLong)
    {
        var normalized = string.IsNullOrWhiteSpace(text) ? null : text.Trim();

        if (normalized is not null && normalized.Length > JdTextMaxChars)
            throw onTooLong(TooLongMessage(fieldLabel, normalized.Length));

        return normalized;
    }

    /// <summary>
    /// Thông báo lỗi tiếng Việt: nói rõ giới hạn VÀ độ dài đang gửi để người dùng biết phải cắt bao nhiêu.
    /// Không format theo culture (giữ số thuần) → thông báo ổn định giữa các môi trường/test.
    /// </summary>
    public static string TooLongMessage(string fieldLabel, int actualChars)
        => $"{fieldLabel} tối đa {JdTextMaxChars} ký tự (đang gửi {actualChars} ký tự).";
}
