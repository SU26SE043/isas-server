using System.Text.Json;
using Isas.Shared.Json;
using static Isas.PaymentService.DTOs.InvoiceRequest;

namespace Isas.PaymentService.Tests;

/// <summary>
/// Cùng lớp lỗi với bug Campaign (E2E INT-17 2026-07-18): chuỗi datetime có offset SỐ
/// ("+00:00") bị System.Text.Json parse thành DateTimeKind.Local; Npgsql chỉ ghi được UTC vào
/// timestamptz → DbUpdateException → HTTP 500.
///
/// Reachable ở Payment qua POST /admin/invoices/close: periodStart/periodEnd ghi thẳng vào
/// invoices.period_start/period_end (timestamptz).
/// </summary>
public class UtcDateTimeConverterTests
{
    // Khớp Program.cs của Payment: KHÔNG có JsonStringEnumConverter (Payment giữ enum SỐ theo hợp đồng FE).
    private static JsonSerializerOptions Opts()
    {
        var o = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        o.Converters.Add(new UtcDateTimeConverter());
        return o;
    }

    [Fact]
    public void CloseBillingPeriod_OffsetSo_RaUtc_KhongConLocal()
    {
        var json = """
        {"orgId":"0610da24-6fbf-4a51-9f75-6191bd40943f",
         "periodStart":"2026-07-01T00:00:00+00:00","periodEnd":"2026-07-31T23:59:59+00:00"}
        """;

        var req = JsonSerializer.Deserialize<CloseBillingPeriodRequest>(json, Opts())!;

        Assert.Equal(DateTimeKind.Utc, req.PeriodStart!.Value.Kind);
        Assert.Equal(DateTimeKind.Utc, req.PeriodEnd!.Value.Kind);
        Assert.Equal(new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc), req.PeriodStart.Value);
    }

    [Fact]
    public void CloseBillingPeriod_OffsetLechMuiGio_QuyDoiDungThoiDiem()
    {
        var json = """{"orgId":"0610da24-6fbf-4a51-9f75-6191bd40943f","periodStart":"2026-07-01T07:00:00+07:00"}""";
        var req = JsonSerializer.Deserialize<CloseBillingPeriodRequest>(json, Opts())!;
        Assert.Equal(DateTimeKind.Utc, req.PeriodStart!.Value.Kind);
        Assert.Equal(new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc), req.PeriodStart.Value);  // 07:00+07 = 00:00Z
    }

    [Fact]
    public void CloseBillingPeriod_Z_VanChay_KhongHoiQuy()
    {
        var json = """{"orgId":"0610da24-6fbf-4a51-9f75-6191bd40943f","periodStart":"2026-07-01T00:00:00Z"}""";
        var req = JsonSerializer.Deserialize<CloseBillingPeriodRequest>(json, Opts())!;
        Assert.Equal(DateTimeKind.Utc, req.PeriodStart!.Value.Kind);
    }

    [Fact]
    public void CloseBillingPeriod_BoTrong_VanLaNull()
    {
        // period tuỳ chọn (mặc định đầu tháng UTC → now) — null phải đi qua nguyên vẹn.
        var json = """{"orgId":"0610da24-6fbf-4a51-9f75-6191bd40943f"}""";
        var req = JsonSerializer.Deserialize<CloseBillingPeriodRequest>(json, Opts())!;
        Assert.Null(req.PeriodStart);
        Assert.Null(req.PeriodEnd);
    }

    [Fact]
    public void Enum_VanLaSO_KhongBiConverterLamLech()
    {
        // Guard: thêm UtcDateTimeConverter KHÔNG được đổi hợp đồng enum-số của Payment.
        var json = JsonSerializer.Serialize(new { status = global::PaymentService.Models.OrderStatus.Paid }, Opts());
        Assert.Contains("\"status\":2", json.Replace(" ", ""));
    }
}
