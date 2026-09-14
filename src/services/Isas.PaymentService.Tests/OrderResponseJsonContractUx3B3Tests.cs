using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Isas.PaymentService.DTOs;
using Isas.Shared.Json;

namespace Isas.PaymentService.Tests;

/// <summary>
/// UX3-B3 — LƯỚI CHẶN LỆCH HỢP ĐỒNG cho <see cref="OrderRequest.OrderResponse"/>.
///
/// <para>Đợt rà UX3 tìm được &gt;30 trường frontend đọc mà backend không gửi. KHÔNG lỗi nào nổ —
/// màn hình chỉ lặng lẽ hiện 0 / dấu gạch / ngày hôm nay. Hợp đồng JSON chỉ tồn tại trong đầu người
/// viết. Test này biến việc ĐỔI TÊN một trường của DTO thành một test ĐỎ ngay tại backend.</para>
///
/// <para>Chỉ khoá TẬP TÊN KHOÁ CẤP MỘT — không kiểu, không giá trị. Danh sách kỳ vọng là chuỗi
/// VIẾT CỨNG (không <c>nameof</c>, không reflection): nếu nó tự suy từ DTO thì đổi tên trường sẽ
/// đổi cả hai vế và test không bao giờ đỏ.</para>
/// </summary>
public class OrderResponseJsonContractUx3B3Tests
{
    // Mirror của Program.cs:41 `AddControllers().AddJsonOptions(...)`. ASP.NET MVC khởi tạo
    // JsonOptions.JsonSerializerOptions = new(JsonSerializerDefaults.Web) ⇒ camelCase; Payment KHÔNG
    // thêm JsonStringEnumConverter (giữ enum SỐ theo hợp đồng FE) — chỉ IgnoreCycles + Never + UTC.
    // KHÔNG dựng `new JsonSerializerOptions()` trần: sẽ ra PascalCase và bỏ sót đúng lớp bug lưới này canh.
    private static JsonSerializerOptions RuntimeOptions()
    {
        var o = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            ReferenceHandler = ReferenceHandler.IgnoreCycles,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        };
        o.Converters.Add(new UtcDateTimeConverter());
        return o;
    }

    // ── HỢP ĐỒNG: tên khoá JSON cấp một của OrderResponse (GET /payment/order/my-orders, /order/{id}).
    //    Sắp xếp. Chuỗi VIẾT CỨNG — sinh lần đầu bằng chính test này rồi dán vào.
    //    ⚠ ĐỔI DANH SÁCH NÀY = ĐỔI HỢP ĐỒNG VỚI FRONTEND.
    private static readonly string[] ExpectedKeys =
    [
        "amountVnd",
        "checkoutUrl",
        "createdAt",
        "expiredAt",
        "id",
        "interviewCredits",
        "invoiceId",
        "kind",
        "ownerId",
        "ownerType",
        "packageId",
        "packageName",
        "paidAt",
        "payosOrderCode",
        "status",
    ];

    [Fact]
    public void OrderResponse_TopLevelJsonKeys_MatchFrozenContract()
    {
        var json = JsonSerializer.Serialize(new OrderRequest.OrderResponse(), RuntimeOptions());
        var actual = ((JsonObject)JsonNode.Parse(json)!)
            .Select(kv => kv.Key).OrderBy(k => k, StringComparer.Ordinal).ToArray();

        Assert.True(
            actual.SequenceEqual(ExpectedKeys),
            "Tên khoá JSON của OrderResponse đã lệch hợp đồng.\n" +
            "Đổi tên khoá JSON là ĐỔI HỢP ĐỒNG. Nếu cố ý, cập nhật ExpectedKeys trong test NÀY " +
            "VÀ báo cho bên frontend (họ đang đọc đúng những tên cũ).\n\n" +
            "Kỳ vọng : [" + string.Join(", ", ExpectedKeys) + "]\n" +
            "Thực tế : [" + string.Join(", ", actual) + "]\n\n" +
            "Danh sách mới (dán vào ExpectedKeys nếu ĐÚNG Ý ĐỒ):\n" +
            string.Join("\n", actual.Select(k => $"    \"{k}\",")));
    }
}
