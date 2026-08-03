using System.Text.Json;
using PayOS.Models.V1.Payouts;
using PayOS.Models.V1.PayoutsAccount;

namespace Isas.PaymentService.Tests;

/// <summary>
/// Khoá HÌNH DẠNG phản hồi payOS bằng payload THẬT, không phải payload ta tự nghĩ ra.
///
/// <para>Lý do có bộ test này: hình dạng suy từ tên trường là thứ dễ sai nhất khi tích hợp bên thứ ba,
/// và sai kiểu đó KHÔNG làm test nào khác đỏ — nó chỉ làm dữ liệu về rỗng hoặc lời gọi ném lúc chạy
/// thật. Cùng lớp với các lỗi parse đã từng bắt được ở vòng grounding.</para>
/// </summary>
public class PayoutClientShapeTests
{
    /// <summary>
    /// Phản hồi THẬT từ <c>GET /v1/payouts-account/balance</c> (server production, 2026-08-03).
    /// </summary>
    private const string RealBalanceJson =
        """{"accountNumber":"leminhduc","accountName":"Lê Minh Đức","currency":"VND","balance":"100000"}""";

    [Fact]
    public void SoDuViChi_LaCHUOI_KhongPhaiSo()
    {
        var info = JsonSerializer.Deserialize<PayoutAccountInfo>(RealBalanceJson);

        // Khoá đúng lý do PayoutClient phải long.TryParse thay vì đọc thẳng. Ai đó "sửa" kiểu cột này
        // thành số sẽ làm mọi lần đọc số dư ném lúc chạy thật, mà không test nào khác kêu.
        Assert.Equal("100000", info!.Balance);
        Assert.Equal("VND", info.Currency);
    }

    [Fact]
    public void LenhChi_TransactionsDangMANG_DocDuocDayDu()
    {
        var payout = JsonSerializer.Deserialize<Payout>(PayoutJson(asArray: true));

        var txn = Assert.Single(payout!.Transactions);
        Assert.Equal(PayoutTransactionState.Processing, txn.State);
        // Tên chủ tài khoản nhận là dữ liệu để đối chiếu chống chuyển nhầm người — mất nó là mất bộ dò.
        Assert.Equal("NGUYEN VAN A", txn.ToAccountName);
        Assert.Equal("payout_123", payout.Id);
    }

    [Fact]
    public void LenhChi_TransactionsDangOBJECT_SDK_KHONG_DocDuoc()
    {
        // ⚠ Tài liệu payOS vẽ `transactions` là OBJECT đánh khoá theo chỉ số (`{"0": {...}}`), trong khi
        // SDK khai List<PayoutTransaction>. Hai thứ đó không tương thích. Test này ghi nhận sự thật đó
        // thay vì giả vờ không có.
        //
        // Hệ quả NẾU API thật trả dạng object: lời gọi tạo lệnh ném JsonException → PayoutClient xếp vào
        // "không rõ kết quả" → đơn nằm InFlight và KHÔNG bao giờ được đóng dấu. An toàn về tiền (không
        // chuyển lần hai) nhưng kẹt vĩnh viễn, phải đối soát tay.
        //
        // ✅ ĐÃ XÁC MINH bằng lệnh chi THẬT trên production (2026-08-03): payOS trả `transactions` dạng
        // MẢNG, đúng như SDK khai — tài liệu vẽ sai. Nhánh kẹt-vĩnh-viễn nói trên KHÔNG xảy ra trong
        // thực tế. Test này giữ lại để nếu payOS đổi sang dạng object thì có chỗ ghi nhận, chứ không
        // còn là ẩn số chặn việc bật tính năng.
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<Payout>(PayoutJson(asArray: false)));
    }

    // Hình dạng lấy từ ví dụ chính thức của payOS docs (POST /v1/payouts), chỉ đổi cách gói `transactions`.
    private static string PayoutJson(bool asArray)
    {
        const string txn = """
            {"id":"txn_1","referenceId":"ref_1","amount":100000,"description":"Hoan tien",
             "toBin":"970422","toAccountNumber":"123456789","toAccountName":"NGUYEN VAN A",
             "state":"PROCESSING"}
            """;
        var transactions = asArray ? $"[{txn}]" : $$"""{"0":{{txn}}}""";
        return $$"""
            {"id":"payout_123","referenceId":"ref_1","transactions":{{transactions}},
             "category":["refund"],"approvalState":"PROCESSING","createdAt":"2024-01-15T10:30:00.000Z"}
            """;
    }
}
