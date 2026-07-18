using System.Text.Json;
using System.Text.Json.Serialization;

namespace Isas.Shared.Json;

/// <summary>
/// Chuẩn hoá MỌI <see cref="DateTime"/> nhận qua JSON về <see cref="DateTimeKind.Utc"/>.
///
/// <para><b>Vì sao cần:</b> System.Text.Json parse chuỗi có <b>offset SỐ</b>
/// (<c>"2026-07-18T14:00:00+00:00"</c> — ISO-8601 hoàn toàn hợp lệ) thành
/// <c>DateTimeKind.Local</c>. Npgsql CHỈ ghi được UTC vào cột <c>timestamp with time zone</c>
/// → <c>DbUpdateException</c> → HTTP <b>500</b>. Chuỗi kết thúc <c>Z</c> ra <c>Kind=Utc</c> nên
/// không lỗi ⇒ bug chỉ lộ với client gửi offset số (Python <c>isoformat()</c>, Java
/// <c>OffsetDateTime.toString()</c>, nhiều HTTP client khác).</para>
///
/// <para><b>Vì sao chuẩn hoá thay vì trả 400:</b> offset số là ISO-8601 hợp lệ và KHÔNG nhập nhằng
/// (<c>+00:00</c> ≡ <c>Z</c>), nên từ chối nó là bắt lỗi oan client đúng chuẩn. Chuẩn hoá tại BIÊN
/// giữ API dễ dùng và loại hẳn lớp lỗi này.</para>
///
/// <para><b>Lợi ích kèm:</b> mọi so sánh với <c>DateTime.UtcNow</c> (validate hạn/ngày bắt đầu)
/// không còn lệch theo timezone của máy chủ — trước đây một giá trị <c>Kind=Local</c> đem so với
/// <c>UtcNow</c> sai đúng bằng offset máy.</para>
///
/// Đăng ký ở <c>AddJsonOptions</c> (mẫu BK20 với <c>JsonStringEnumConverter</c>). System.Text.Json
/// tự bọc converter này cho property <c>DateTime?</c> nên KHÔNG cần converter nullable riêng.
/// </summary>
public sealed class UtcDateTimeConverter : JsonConverter<DateTime>
{
    public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => ToUtc(reader.GetDateTime());

    public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options)
        => writer.WriteStringValue(ToUtc(value));

    /// <summary>Local → đổi múi giờ về UTC; Unspecified → coi như đã là UTC; Utc → giữ nguyên.</summary>
    public static DateTime ToUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        // Unspecified: client gửi "2026-07-18T14:00:00" (không offset). Coi là UTC để khớp cột
        // timestamptz và KHÔNG phụ thuộc timezone máy chủ (ToUniversalTime() sẽ phụ thuộc).
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };
}
