using Isas.PaymentService.Controllers;
using Isas.PaymentService.DTOs;
using Isas.PaymentService.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using PaymentService.Models;

namespace Isas.PaymentService.Tests;

/// <summary>
/// PP4 — `402` phải NÓI ĐƯỢC LÝ DO.
///
/// Trước vòng này `ReserveAsync` gộp 5 nguyên nhân khác hẳn nhau (chưa có ví · hết credit · chạm hạn
/// mức postpaid · còn hoá đơn quá hạn · ví đình chỉ) thành đúng một body
/// <c>{"error":"Insufficient credits"}</c> ⇒ FE B2B chỉ nói được "mua thêm credit", kể cả khi việc
/// cần làm là **thanh toán hoá đơn** hoặc **xin nâng hạn mức**.
///
/// ⚠ Bộ test này sinh ra vì một phép mutation: bỏ trường <c>reason</c> khỏi controller mà **toàn bộ
/// suite vẫn xanh** — tức thân 402 mới hoàn toàn không được test nào phủ.
/// </summary>
public class ReserveReasonBodyTests
{
    private const string Token = "test-internal-token";

    private static InternalCreditsController Build(ReserveResult result)
    {
        var credits = new Mock<ICreditAccountService>();
        credits.Setup(c => c.ReserveAsync(
                It.IsAny<OwnerType>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(result);

        var config = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Internal:Token"] = Token }).Build();

        return new InternalCreditsController(credits.Object, config, NullLogger<InternalCreditsController>.Instance);
    }

    private static CreditOpRequest Req() => new()
    {
        OwnerType = OwnerType.Org,
        OwnerId = Guid.NewGuid(),
        SessionId = Guid.NewGuid(),
    };

    /// <summary>Đọc field của anonymous object trong body 402 bằng reflection.</summary>
    private static string? Field(object? body, string name) =>
        body?.GetType().GetProperty(name)?.GetValue(body) as string;

    public static TheoryData<ReserveResult, string> Cases() => new()
    {
        { ReserveResult.NoWallet(), "no_wallet" },
        { ReserveResult.OutOfCredit(), "out_of_credit" },
        { ReserveResult.LimitReached(), "limit_reached" },
        { ReserveResult.InvoiceOverdue(), "invoice_overdue" },
        { ReserveResult.Suspended(), "suspended" },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Reserve_402_MangDungReasonVaMessage(ReserveResult result, string expectedReason)
    {
        var controller = Build(result);

        var response = await controller.ReserveAsync(Req(), Token, default);

        var obj = Assert.IsType<ObjectResult>(response);
        Assert.Equal(StatusCodes.Status402PaymentRequired, obj.StatusCode);
        Assert.Equal(expectedReason, Field(obj.Value, "reason"));

        // `error` GIỮ NGUYÊN chuỗi cũ ở mọi nhánh — client cũ đọc `error` không được vỡ.
        Assert.Equal("Insufficient credits", Field(obj.Value, "error"));

        // `message` là câu cho người dùng cuối: phải có, và phải KHÁC NHAU theo từng lý do
        // (nếu dùng chung một câu thì tính năng này vô nghĩa).
        Assert.False(string.IsNullOrWhiteSpace(Field(obj.Value, "message")));
    }

    /// <summary>5 lý do phải cho ra 5 câu KHÁC NHAU — chống việc ai đó gộp lại một câu chung.</summary>
    [Fact]
    public async Task NamLyDo_ChoRaNamCauKhacNhau()
    {
        var messages = new List<string?>();
        foreach (var (result, _) in Cases().Select(row => ((ReserveResult)row[0], (string)row[1])))
        {
            var obj = Assert.IsType<ObjectResult>(await Build(result).ReserveAsync(Req(), Token, default));
            messages.Add(Field(obj.Value, "message"));
        }

        Assert.Equal(5, messages.Distinct().Count());
    }

    /// <summary>Đường thành công KHÔNG được lẫn `reason` — 200 giữ nguyên hợp đồng cũ.</summary>
    [Fact]
    public async Task Reserve_ThanhCong_Van200_KhongCoReason()
    {
        var controller = Build(ReserveResult.Reserved(Guid.NewGuid(), 3));

        var response = await controller.ReserveAsync(Req(), Token, default);

        var ok = Assert.IsType<OkObjectResult>(response);
        Assert.Null(ok.Value?.GetType().GetProperty("reason"));
    }
}
