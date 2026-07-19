using Isas.PaymentService.Controllers;
using Isas.PaymentService.DTOs;
using Isas.PaymentService.Models;
using Isas.PaymentService.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PaymentService.Models;

namespace Isas.PaymentService.Tests;

/// <summary>
/// F22 (FR18) — đo token/chi phí AI.
///
/// Bối cảnh: AIService gọi Gemini 10 chỗ và trước vòng này KHÔNG chỗ nào đọc <c>usage_metadata</c> ⇒ hệ
/// thống không biết mình đốt bao nhiêu tiền. GEN-4 cấm AIService ghi DB nên nó ĐẨY số liệu qua callback nội
/// bộ về đây (mẫu callback chấm điểm); Payment giữ bảng vì chi phí AI chỉ có nghĩa khi đọc cạnh doanh thu
/// (F19 cũng ở service này).
/// </summary>
public class AiUsageF22Tests
{
    private static readonly DateTime T0 = new(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);

    private const string FlashModel = "gemini-2.5-flash";

    // Giá thật của gemini-2.5-flash (USD/1 triệu token) tại thời điểm viết.
    private static AiPricingSettings Pricing(decimal input = 0.30m, decimal output = 2.50m) => new()
    {
        Models = new Dictionary<string, AiPricingSettings.ModelPrice>
        {
            [FlashModel] = new() { InputPerMillionUsd = input, OutputPerMillionUsd = output }
        },
        Default = new() { InputPerMillionUsd = 99m, OutputPerMillionUsd = 99m }
    };

    private static AiUsageService NewService(PaymentTestDb tdb, AiPricingSettings? pricing = null) =>
        new(tdb.Db, Options.Create(pricing ?? Pricing()), NullLogger<AiUsageService>.Instance);

    private static RecordAiUsageRequest Req(
        string operation = "score", int prompt = 1_000_000, int output = 1_000_000,
        string model = FlashModel, int? urlsProposed = null, int? urlsRejected = null) => new()
    {
        Operation = operation,
        Model = model,
        PromptTokens = prompt,
        OutputTokens = output,
        TotalTokens = prompt + output,
        ResourceUrlsProposed = urlsProposed,
        ResourceUrlsRejected = urlsRejected
    };

    // ── Ghi nhận + quy tiền ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Record_QuyTien_TheoDonGiaCauHinh()
    {
        using var tdb = new PaymentTestDb();
        var svc = NewService(tdb);

        await svc.RecordAsync(Req(prompt: 1_000_000, output: 1_000_000));

        var row = await tdb.Db.AiUsageLogs.SingleAsync();
        // 1 triệu in + 1 triệu out = đúng 1 lần đơn giá mỗi vế.
        Assert.Equal(0.30m + 2.50m, row.CostUsd);
        Assert.Equal("score", row.Operation);
    }

    [Fact]
    public async Task Record_LuuDonGiaSnapshot_TrenTungDong()
    {
        using var tdb = new PaymentTestDb();
        await NewService(tdb).RecordAsync(Req());

        var row = await tdb.Db.AiUsageLogs.SingleAsync();
        Assert.Equal(0.30m, row.InputPricePerMillionUsd);
        Assert.Equal(2.50m, row.OutputPricePerMillionUsd);
    }

    /// <summary>
    /// Đơn giá là dữ liệu SẼ ĐỔI (Google đổi bảng giá). Nếu chỉ lưu token rồi nhân giá hiện hành lúc xem báo
    /// cáo thì mọi số liệu LỊCH SỬ tự động sai đi mỗi lần đổi giá — và sai trong im lặng. Đây là lý do tồn
    /// tại của hai cột đơn giá trên từng dòng (mẫu <c>Invoice.UnitPrice</c>).
    /// </summary>
    [Fact]
    public async Task Record_DoiGiaSauDo_KhongHoiToDongCu()
    {
        using var tdb = new PaymentTestDb();

        await NewService(tdb, Pricing(0.30m, 2.50m)).RecordAsync(Req(operation: "score"));
        // Google tăng giá gấp 10.
        await NewService(tdb, Pricing(3.00m, 25.00m)).RecordAsync(Req(operation: "decide_next"));

        var cu = await tdb.Db.AiUsageLogs.SingleAsync(u => u.Operation == "score");
        var moi = await tdb.Db.AiUsageLogs.SingleAsync(u => u.Operation == "decide_next");

        Assert.Equal(0.30m + 2.50m, cu.CostUsd);        // dòng cũ GIỮ NGUYÊN tiền của nó
        Assert.Equal(3.00m + 25.00m, moi.CostUsd);
        Assert.Equal(0.30m, cu.InputPricePerMillionUsd);
    }

    [Fact]
    public async Task Record_ModelLa_DungGiaDefault()
    {
        using var tdb = new PaymentTestDb();
        await NewService(tdb).RecordAsync(Req(model: "gemini-9-chua-ton-tai"));

        var row = await tdb.Db.AiUsageLogs.SingleAsync();
        // Ghi theo giá mặc định (gần đúng) + log cảnh báo — KHÔNG ghi cost = 0, vì cost = 0 làm báo cáo
        // chi phí trông đẹp một cách sai sự thật.
        Assert.Equal(99m, row.InputPricePerMillionUsd);
        Assert.True(row.CostUsd > 0);
    }

    [Fact]
    public async Task Record_TokenAm_BiKep_KhongTruVaoTong()
    {
        using var tdb = new PaymentTestDb();
        await NewService(tdb).RecordAsync(Req(prompt: -5_000_000, output: 1_000_000));

        var row = await tdb.Db.AiUsageLogs.SingleAsync();
        Assert.Equal(0, row.PromptTokens);
        // Token âm lọt vào sẽ TRỪ vào tổng chi phí — báo cáo sai theo hướng có lợi cho ta.
        Assert.Equal(2.50m, row.CostUsd);
    }

    [Fact]
    public async Task Record_ThieuTotal_SuyRaTuHaiVe()
    {
        using var tdb = new PaymentTestDb();
        var req = Req(prompt: 100, output: 40);
        req.TotalTokens = 0;                     // SDK không trả total
        await NewService(tdb).RecordAsync(req);

        Assert.Equal(140, (await tdb.Db.AiUsageLogs.SingleAsync()).TotalTokens);
    }

    // ── Báo cáo ──────────────────────────────────────────────────────────────────────────────

    private static async Task SeedAsync(PaymentTestDb tdb, string operation, DateTime at,
        int prompt = 1_000_000, int output = 1_000_000, int? proposed = null, int? rejected = null)
    {
        tdb.Db.AiUsageLogs.Add(new AiUsageLog
        {
            Id = Guid.NewGuid(),
            Operation = operation,
            Model = FlashModel,
            PromptTokens = prompt,
            OutputTokens = output,
            TotalTokens = prompt + output,
            InputPricePerMillionUsd = 0.30m,
            OutputPricePerMillionUsd = 2.50m,
            CostUsd = prompt / 1_000_000m * 0.30m + output / 1_000_000m * 2.50m,
            ResourceUrlsProposed = proposed,
            ResourceUrlsRejected = rejected,
            CreatedAt = at
        });
        await tdb.Db.SaveChangesAsync();
    }

    [Fact]
    public async Task Report_GopTheoOperation_ChoBietTienDiDau()
    {
        using var tdb = new PaymentTestDb();
        await SeedAsync(tdb, "score", T0.AddHours(1));
        await SeedAsync(tdb, "score", T0.AddHours(2));
        await SeedAsync(tdb, "generate_questions", T0.AddHours(3));

        var report = await NewService(tdb).GetReportAsync(T0, T0.AddDays(1), AiUsageGranularity.Day);

        Assert.Equal(3, report.TotalCalls);
        var score = report.ByOperation.Single(o => o.Operation == "score");
        Assert.Equal(2, score.Calls);
        Assert.Equal(4_000_000, score.TotalTokens);
        Assert.Equal(3 * (0.30m + 2.50m), report.TotalCostUsd);
    }

    [Fact]
    public async Task Report_GopTheoNgay()
    {
        using var tdb = new PaymentTestDb();
        await SeedAsync(tdb, "score", T0.AddHours(3));
        await SeedAsync(tdb, "score", T0.AddDays(1).AddHours(5));

        var report = await NewService(tdb).GetReportAsync(T0, T0.AddDays(3), AiUsageGranularity.Day);

        Assert.Equal(2, report.Buckets.Count);
        Assert.Equal(T0, report.Buckets[0].PeriodStart);
        Assert.Equal(DateTimeKind.Utc, report.Buckets[0].PeriodStart.Kind);
        Assert.Equal(T0.AddDays(1), report.Buckets[1].PeriodStart);
    }

    [Fact]
    public async Task Report_KyNuaMo_KhongDemTrung()
    {
        using var tdb = new PaymentTestDb();
        await SeedAsync(tdb, "score", T0);                    // đúng biên `from` → TÍNH
        await SeedAsync(tdb, "score", T0.AddDays(1));         // đúng biên `to`   → KHÔNG tính

        var report = await NewService(tdb).GetReportAsync(T0, T0.AddDays(1), AiUsageGranularity.Day);

        Assert.Equal(1, report.TotalCalls);
    }

    [Fact]
    public async Task Report_KhongCoDuLieu_TraVeSoKhong_KhongNo()
    {
        using var tdb = new PaymentTestDb();

        var report = await NewService(tdb).GetReportAsync(T0, T0.AddDays(1), AiUsageGranularity.Day);

        Assert.Equal(0, report.TotalCalls);
        Assert.Equal(0m, report.TotalCostUsd);
        Assert.Empty(report.Buckets);
        Assert.Null(report.ResourceUrls);
    }

    // ── F15: tỉ lệ URL bị allowlist loại ─────────────────────────────────────────────────────

    [Fact]
    public async Task Report_TiLeUrlBiLoai()
    {
        using var tdb = new PaymentTestDb();
        await SeedAsync(tdb, "generate_lesson_theory", T0.AddHours(1), proposed: 4, rejected: 3);
        await SeedAsync(tdb, "generate_lesson_theory", T0.AddHours(2), proposed: 6, rejected: 2);
        await SeedAsync(tdb, "score", T0.AddHours(3));       // không liên quan URL

        var report = await NewService(tdb).GetReportAsync(T0, T0.AddDays(1), AiUsageGranularity.Day);

        Assert.NotNull(report.ResourceUrls);
        Assert.Equal(10, report.ResourceUrls!.Proposed);
        Assert.Equal(5, report.ResourceUrls.Rejected);
        Assert.Equal(0.5, report.ResourceUrls.RejectedRate);
    }

    [Fact]
    public async Task Report_KhongCoLuotSinhTaiLieu_TraNull_KhongPhai0()
    {
        using var tdb = new PaymentTestDb();
        await SeedAsync(tdb, "score", T0.AddHours(1));

        var report = await NewService(tdb).GetReportAsync(T0, T0.AddDays(1), AiUsageGranularity.Day);

        // null ≠ 0/0: "không áp dụng" khác "AI đề xuất 0 link". Hiện 0% bị loại là một khẳng định
        // ta không có cơ sở để nói.
        Assert.Null(report.ResourceUrls);
    }

    // ── Endpoint nội bộ (GEN-1: không qua gateway, gác bằng X-Internal-Token) ─────────────────

    private static InternalAiUsageController NewInternalController(PaymentTestDb tdb, string? configured)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Internal:Token"] = configured })
            .Build();
        return new InternalAiUsageController(
            NewService(tdb), config, NullLogger<InternalAiUsageController>.Instance);
    }

    [Fact]
    public async Task Internal_TokenDung_GhiDuoc()
    {
        using var tdb = new PaymentTestDb();
        var ctrl = NewInternalController(tdb, "secret-token");

        var result = await ctrl.RecordAsync(Req(), "secret-token");

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(1, await tdb.Db.AiUsageLogs.CountAsync());
    }

    [Fact]
    public async Task Internal_TokenSai_401_KhongGhi()
    {
        using var tdb = new PaymentTestDb();
        var ctrl = NewInternalController(tdb, "secret-token");

        var result = await ctrl.RecordAsync(Req(), "token-sai");

        Assert.IsType<UnauthorizedObjectResult>(result);
        Assert.Equal(0, await tdb.Db.AiUsageLogs.CountAsync());
    }

    [Fact]
    public async Task Internal_ThieuHeader_401()
    {
        using var tdb = new PaymentTestDb();
        Assert.IsType<UnauthorizedObjectResult>(
            await NewInternalController(tdb, "secret-token").RecordAsync(Req(), null));
    }

    /// <summary>Fail-closed: chưa cấu hình token thì từ chối hết, KHÔNG mở toang endpoint máy-máy.</summary>
    [Fact]
    public async Task Internal_ChuaCauHinhToken_TuChoiHet()
    {
        using var tdb = new PaymentTestDb();
        Assert.IsType<UnauthorizedObjectResult>(
            await NewInternalController(tdb, null).RecordAsync(Req(), "bat-ky-token-nao"));
        Assert.IsType<UnauthorizedObjectResult>(
            await NewInternalController(tdb, "").RecordAsync(Req(), ""));
    }

    [Fact]
    public async Task Internal_ThieuOperation_400()
    {
        using var tdb = new PaymentTestDb();
        var ctrl = NewInternalController(tdb, "secret-token");

        var result = await ctrl.RecordAsync(Req(operation: "  "), "secret-token");

        Assert.IsType<BadRequestObjectResult>(result);
    }

    // ── Endpoint admin ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Admin_KhoangThoiGianSai_400()
    {
        using var tdb = new PaymentTestDb();
        var ctrl = new AdminAiUsageController(NewService(tdb));

        var result = await ctrl.GetAiUsage(from: T0.AddDays(1), to: T0);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task Admin_GroupByLa_400()
    {
        using var tdb = new PaymentTestDb();
        var ctrl = new AdminAiUsageController(NewService(tdb));

        var result = await ctrl.GetAiUsage(from: T0, to: T0.AddDays(1), groupBy: "tuan");

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    /// <summary>
    /// Client gửi offset số (<c>+07:00</c>) cho ra <c>DateTimeKind.Local</c> và Npgsql TỪ CHỐI — đúng bug
    /// 500 đã gặp ở <c>POST /api/v1/campaign</c>. Mốc phải được quy đổi thật, không gán nhãn suông.
    /// </summary>
    [Fact]
    public async Task Admin_MocThoiGianLocal_DuocQuyDoiUtc()
    {
        using var tdb = new PaymentTestDb();
        var ctrl = new AdminAiUsageController(NewService(tdb));
        var local = new DateTime(2026, 7, 1, 7, 0, 0, DateTimeKind.Local);

        var result = await ctrl.GetAiUsage(from: local, to: local.AddDays(1));

        var report = Assert.IsType<AiUsageReportResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(DateTimeKind.Utc, report.From.Kind);
        Assert.Equal(local.ToUniversalTime(), report.From);
    }
}
