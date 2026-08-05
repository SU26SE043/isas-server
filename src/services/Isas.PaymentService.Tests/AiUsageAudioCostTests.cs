using System.Text.Json;
using Isas.PaymentService.Controllers;
using Isas.PaymentService.DTOs;
using Isas.PaymentService.Models;
using Isas.PaymentService.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PaymentService.Models;

namespace Isas.PaymentService.Tests;

/// <summary>
/// F22 — chi phí AI tính theo PHÚT AUDIO (bổ sung cho <see cref="AiUsageF22Tests"/> vốn chỉ đo token).
///
/// VÌ SAO CẦN NHÁNH RIÊNG: không phải model nào cũng bán theo token. Model chép lời (<c>whisper-1</c>) bán
/// theo PHÚT AUDIO, và một lượt chép lời có <c>promptTokens = outputTokens = 0</c> ⇒ công thức token cho ra
/// ĐÚNG 0 đồng. Không có nhánh này thì toàn bộ chi phí transcribe vô hình trên báo cáo admin — không
/// exception, không log, chỉ là một con số không bao giờ xuất hiện. Đúng kiểu hỏng đã xảy ra khi env
/// <c>USAGE_SINK_BASE</c> vắng trên container và F22 tắt câm nhiều ngày mà không ai biết.
/// </summary>
public class AiUsageAudioCostTests
{
    private static readonly DateTime T0 = new(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);

    private const string FlashModel = "gemini-2.5-flash";
    private const string WhisperModel = "whisper-1";

    /// <summary>
    /// Bảng giá hai đơn vị. Chú ý <see cref="FlashModel"/> CỐ Ý không khai giá phút (nó bán theo token —
    /// đúng thực tế), còn <c>Default</c> CÓ khai để làm lưới cho model chép lời chưa kịp vào bảng.
    /// </summary>
    private static AiPricingSettings Pricing(
        decimal whisperPerMinute = 0.006m, decimal? defaultPerMinute = 0.009m) => new()
        {
            Models = new Dictionary<string, AiPricingSettings.ModelPrice>
            {
                [FlashModel] = new() { InputPerMillionUsd = 0.30m, OutputPerMillionUsd = 2.50m },
                [WhisperModel] = new() { PricePerMinuteUsd = whisperPerMinute }
            },
            Default = new()
            {
                InputPerMillionUsd = 99m,
                OutputPerMillionUsd = 99m,
                PricePerMinuteUsd = defaultPerMinute
            }
        };

    private static AiUsageService NewService(PaymentTestDb tdb, AiPricingSettings? pricing = null) =>
        new(tdb.Db, Options.Create(pricing ?? Pricing()), NullLogger<AiUsageService>.Instance);

    /// <summary>Lượt CHÉP LỜI: chỉ có độ dài audio, 0 token — đúng hình dạng payload AIService gửi.</summary>
    private static RecordAiUsageRequest AudioReq(
        int seconds = 120, string model = WhisperModel, string operation = "transcribe") => new()
        {
            Operation = operation,
            Model = model,
            PromptTokens = 0,
            OutputTokens = 0,
            TotalTokens = 0,
            AudioSeconds = seconds
        };

    /// <summary>Lượt sinh chữ: token, KHÔNG có <c>audioSeconds</c>.</summary>
    private static RecordAiUsageRequest TokenReq(string operation = "score") => new()
    {
        Operation = operation,
        Model = FlashModel,
        PromptTokens = 1_000_000,
        OutputTokens = 1_000_000,
        TotalTokens = 2_000_000
    };

    // ── Tính tiền theo phút ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Record_LuotChepLoi_TinhTheoPhut_KhongRa0()
    {
        using var tdb = new PaymentTestDb();

        await NewService(tdb).RecordAsync(AudioReq(seconds: 120));

        var row = await tdb.Db.AiUsageLogs.SingleAsync();
        // 120s = 2 phút × 0.006 USD/phút.
        Assert.Equal(0.012m, row.CostUsd);
        // Khẳng định tường minh: đây CHÍNH LÀ con số mà công thức token sẽ cho ra nếu nhánh đơn vị bị bỏ.
        Assert.NotEqual(0m, row.CostUsd);
        Assert.Equal(120, row.AudioSeconds);
    }

    /// <summary>
    /// Một lượt chép lời rất ngắn vẫn phải có tiền: chi phí mỗi lượt cỡ 1e-4 USD, nếu bị làm tròn về 0 thì
    /// nó biến mất khỏi tổng và cả nghìn lượt cộng lại vẫn ra 0.
    /// </summary>
    [Fact]
    public async Task Record_LuotChepLoiRatNgan_KhongBiLamTronVe0()
    {
        using var tdb = new PaymentTestDb();

        await NewService(tdb).RecordAsync(AudioReq(seconds: 1));

        Assert.True((await tdb.Db.AiUsageLogs.SingleAsync()).CostUsd > 0m);
    }

    [Fact]
    public async Task Record_LuotChepLoi_LuuDonGiaPhutSnapshot()
    {
        using var tdb = new PaymentTestDb();

        await NewService(tdb).RecordAsync(AudioReq());

        var row = await tdb.Db.AiUsageLogs.SingleAsync();
        Assert.Equal(0.006m, row.PricePerMinuteUsd);
    }

    /// <summary>
    /// Cùng lý do với hai đơn giá token (mẫu <c>Invoice.UnitPrice</c>): nhà cung cấp đổi giá là chuyện SẼ
    /// xảy ra; đọc giá hiện hành lúc xem báo cáo thì mọi số liệu LỊCH SỬ tự sai đi, và sai trong im lặng.
    /// </summary>
    [Fact]
    public async Task Record_DoiGiaPhutSauDo_KhongHoiToDongCu()
    {
        using var tdb = new PaymentTestDb();

        await NewService(tdb, Pricing(whisperPerMinute: 0.006m))
            .RecordAsync(AudioReq(operation: "transcribe"));
        // Nhà cung cấp tăng giá gấp 10.
        await NewService(tdb, Pricing(whisperPerMinute: 0.060m))
            .RecordAsync(AudioReq(operation: "transcribe_v2"));

        var cu = await tdb.Db.AiUsageLogs.SingleAsync(u => u.Operation == "transcribe");
        var moi = await tdb.Db.AiUsageLogs.SingleAsync(u => u.Operation == "transcribe_v2");

        Assert.Equal(0.012m, cu.CostUsd);          // dòng cũ GIỮ NGUYÊN tiền của nó
        Assert.Equal(0.006m, cu.PricePerMinuteUsd);
        Assert.Equal(0.120m, moi.CostUsd);
    }

    // ── Nhánh bám DỮ LIỆU, không bám bảng giá ────────────────────────────────────────────────

    /// <summary>
    /// Model chép lời chưa kịp vào bảng giá (whisper-2 mai này) phải rơi về giá mặc định, KHÔNG rơi về
    /// công thức token — vì rơi về token là ra 0 đồng, tức đúng cái lỗ tính năng này sinh ra để bịt.
    /// </summary>
    [Fact]
    public async Task Record_ModelChepLoiLa_RoiVeGiaMacDinh_KhongPhai0()
    {
        using var tdb = new PaymentTestDb();

        await NewService(tdb).RecordAsync(AudioReq(seconds: 60, model: "whisper-9-chua-ton-tai"));

        var row = await tdb.Db.AiUsageLogs.SingleAsync();
        Assert.Equal(0.009m, row.PricePerMinuteUsd);   // Default.PricePerMinuteUsd
        Assert.Equal(0.009m, row.CostUsd);             // 1 phút
    }

    /// <summary>
    /// Model CÓ trong bảng nhưng khai giá token (gemini) mà lượt gọi lại gửi <c>audioSeconds</c> ⇒ vẫn phải
    /// tính theo phút bằng giá mặc định. Đây là bậc thang thứ hai: "model có trong bảng" không đồng nghĩa
    /// "bảng biết giá theo phút của nó".
    /// </summary>
    [Fact]
    public async Task Record_ModelKhaiGiaToken_NhungGuiAudio_VanTinhTheoPhut()
    {
        using var tdb = new PaymentTestDb();

        await NewService(tdb).RecordAsync(AudioReq(seconds: 60, model: FlashModel));

        var row = await tdb.Db.AiUsageLogs.SingleAsync();
        Assert.Equal(0.009m, row.CostUsd);
    }

    /// <summary>
    /// Không tìm được đơn giá phút ở đâu cả → ghi 0 (không nổ, đây là đường quan sát) nhưng KHÔNG được im
    /// lặng — service log cảnh báo. Test khoá phần hành vi kiểm chứng được: dòng vẫn ghi và không rơi nhầm
    /// sang công thức token.
    /// </summary>
    [Fact]
    public async Task Record_KhongCoDonGiaPhutODau_VanGhiDong_KhongRoiSangCongThucToken()
    {
        using var tdb = new PaymentTestDb();
        var pricing = Pricing(defaultPerMinute: null);
        pricing.Models[WhisperModel].PricePerMinuteUsd = null;

        await NewService(tdb, pricing).RecordAsync(AudioReq(seconds: 120));

        var row = await tdb.Db.AiUsageLogs.SingleAsync();
        Assert.Equal(120, row.AudioSeconds);
        Assert.Null(row.PricePerMinuteUsd);
        Assert.Equal(0m, row.CostUsd);
    }

    // ── Không regress nhánh token ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Record_LuotToken_GiuNguyenCongThucCu_VaKhongCoDauVetAudio()
    {
        using var tdb = new PaymentTestDb();

        await NewService(tdb).RecordAsync(TokenReq());

        var row = await tdb.Db.AiUsageLogs.SingleAsync();
        Assert.Equal(0.30m + 2.50m, row.CostUsd);
        // null ≠ 0: "lượt này không phải chép lời" khác "chép lời 0 giây".
        Assert.Null(row.AudioSeconds);
        Assert.Null(row.PricePerMinuteUsd);
    }

    /// <summary>
    /// <c>audioSeconds = 0</c> là "có chép lời, độ dài 0" ⇒ 0 đồng NHƯNG vẫn là lượt tính theo phút. Phân
    /// biệt được với null mới nói được "kỳ này có bao nhiêu lượt chép lời".
    /// </summary>
    [Fact]
    public async Task Record_Audio0Giay_Khac_KhongCoAudio()
    {
        using var tdb = new PaymentTestDb();

        await NewService(tdb).RecordAsync(AudioReq(seconds: 0));

        var row = await tdb.Db.AiUsageLogs.SingleAsync();
        Assert.Equal(0, row.AudioSeconds);
        Assert.NotNull(row.PricePerMinuteUsd);
        Assert.Equal(0m, row.CostUsd);
    }

    [Fact]
    public async Task Record_AudioAm_BiKep_KhongTruVaoTong()
    {
        using var tdb = new PaymentTestDb();

        await NewService(tdb).RecordAsync(AudioReq(seconds: -600));

        var row = await tdb.Db.AiUsageLogs.SingleAsync();
        Assert.Equal(0, row.AudioSeconds);
        // Giây âm lọt vào sẽ TRỪ vào tổng chi phí — báo cáo sai theo hướng có lợi cho ta.
        Assert.Equal(0m, row.CostUsd);
    }

    // ── Báo cáo cộng đủ hai đơn vị ───────────────────────────────────────────────────────────

    /// <summary>Seed TRỰC TIẾP với chi phí tính tay — cố ý KHÔNG gọi lại code sản xuất, nếu không thì một
    /// công thức sai sẽ "nhất quán" với chính nó và test báo cáo không bao giờ đỏ.</summary>
    private static async Task SeedAsync(PaymentTestDb tdb, string operation, DateTime at,
        int? audioSeconds = null, decimal cost = 0m, int tokens = 0)
    {
        tdb.Db.AiUsageLogs.Add(new AiUsageLog
        {
            Id = Guid.NewGuid(),
            Operation = operation,
            Model = audioSeconds.HasValue ? WhisperModel : FlashModel,
            PromptTokens = tokens,
            OutputTokens = 0,
            TotalTokens = tokens,
            InputPricePerMillionUsd = audioSeconds.HasValue ? 0m : 0.30m,
            OutputPricePerMillionUsd = audioSeconds.HasValue ? 0m : 2.50m,
            AudioSeconds = audioSeconds,
            PricePerMinuteUsd = audioSeconds.HasValue ? 0.006m : null,
            CostUsd = cost,
            CreatedAt = at
        });
        await tdb.Db.SaveChangesAsync();
    }

    [Fact]
    public async Task Report_CongDuChiPhi_CaToken_LanAudio()
    {
        using var tdb = new PaymentTestDb();
        await SeedAsync(tdb, "score", T0.AddHours(1), tokens: 1_000_000, cost: 0.30m);
        await SeedAsync(tdb, "transcribe", T0.AddHours(2), audioSeconds: 120, cost: 0.012m);
        await SeedAsync(tdb, "transcribe", T0.AddHours(3), audioSeconds: 60, cost: 0.006m);

        var report = await NewService(tdb).GetReportAsync(T0, T0.AddDays(1), AiUsageGranularity.Day);

        Assert.Equal(3, report.TotalCalls);
        // Bỏ sót dòng audio ở đây = chi phí transcribe biến mất khỏi báo cáo admin.
        Assert.Equal(0.30m + 0.012m + 0.006m, report.TotalCostUsd);
        // Khối lượng đo được, nếu không thì đọc báo cáo thấy "0 token mà mất tiền" và không kiểm chứng nổi.
        Assert.Equal(180, report.AudioSeconds);
    }

    [Fact]
    public async Task Report_ByOperation_DongChepLoiCoRieng()
    {
        using var tdb = new PaymentTestDb();
        await SeedAsync(tdb, "score", T0.AddHours(1), tokens: 1_000_000, cost: 0.30m);
        await SeedAsync(tdb, "transcribe", T0.AddHours(2), audioSeconds: 120, cost: 0.012m);

        var report = await NewService(tdb).GetReportAsync(T0, T0.AddDays(1), AiUsageGranularity.Day);

        var transcribe = report.ByOperation.Single(o => o.Operation == "transcribe");
        Assert.Equal(0.012m, transcribe.CostUsd);
        Assert.Equal(120, transcribe.AudioSeconds);
        Assert.Equal(0, transcribe.TotalTokens);

        // Endpoint sinh chữ không được dính giây audio của endpoint khác.
        Assert.Equal(0, report.ByOperation.Single(o => o.Operation == "score").AudioSeconds);
    }

    [Fact]
    public async Task Report_KyKhongCoLuotChepLoi_AudioSeconds0_KhongNo()
    {
        using var tdb = new PaymentTestDb();
        await SeedAsync(tdb, "score", T0.AddHours(1), tokens: 1_000_000, cost: 0.30m);

        var report = await NewService(tdb).GetReportAsync(T0, T0.AddDays(1), AiUsageGranularity.Day);

        Assert.Equal(0, report.AudioSeconds);
        Assert.Equal(0.30m, report.TotalCostUsd);
    }

    // ── Hợp đồng dây với AIService ───────────────────────────────────────────────────────────

    /// <summary>Đúng options MVC dùng để bind body (<c>JsonSerializerDefaults.Web</c> = camelCase).</summary>
    private static readonly JsonSerializerOptions WireOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// 🔴 KHOÁ JSON LÀ <c>audioSeconds</c> — hợp đồng dây với AIService (Python).
    ///
    /// Lệch tên field giữa hai ngôn ngữ KHÔNG ném lỗi: nó chỉ im lặng điền null, lượt chép lời rơi về công
    /// thức token và ra 0 đồng. Repo đã dính đúng kiểu này ba lần (<c>focusCriteria</c> bị pydantic nuốt ·
    /// <c>adaptiveMaxQuestions</c> vs <c>maxQuestions</c> làm mọi user nhận trần 0 câu · khoá
    /// <c>promptVersion</c> suýt để cột NULL vĩnh viễn).
    /// </summary>
    [Fact]
    public void Wire_KhoaJson_audioSeconds_BindDung()
    {
        const string body = """
        {"operation":"transcribe","model":"whisper-1","promptTokens":0,"outputTokens":0,
         "totalTokens":0,"audioSeconds":90}
        """;

        var req = JsonSerializer.Deserialize<RecordAiUsageRequest>(body, WireOptions)!;

        Assert.Equal(90, req.AudioSeconds);
    }

    /// <summary>
    /// Vế âm — chứng minh test trên thật sự NHẠY với tên khoá chứ không xanh vì lý do khác: khoá snake_case
    /// KHÔNG bind (và đó chính là hình dạng của sự cố nếu ai đó đổi tên một bên).
    /// </summary>
    [Fact]
    public void Wire_KhoaJsonSai_KhongBind_ChungMinhTestNhay()
    {
        const string body = """{"operation":"transcribe","model":"whisper-1","audio_seconds":90}""";

        var req = JsonSerializer.Deserialize<RecordAiUsageRequest>(body, WireOptions)!;

        Assert.Null(req.AudioSeconds);
    }

    /// <summary>
    /// Chuỗi đầy đủ: JSON THẬT như AIService gửi → endpoint nội bộ → dòng trong DB CÓ TIỀN. Test này bắt
    /// được cả hai kiểu hỏng cùng lúc — lệch tên khoá (bind hụt) và thiếu nhánh đơn vị (ra 0 đồng).
    /// </summary>
    [Fact]
    public async Task Wire_TuJsonThat_DenDongCoTien()
    {
        using var tdb = new PaymentTestDb();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Internal:Token"] = "secret-token" })
            .Build();
        var ctrl = new InternalAiUsageController(
            NewService(tdb), config, NullLogger<InternalAiUsageController>.Instance);

        const string body = """
        {"operation":"transcribe","model":"whisper-1","promptTokens":0,"outputTokens":0,
         "totalTokens":0,"audioSeconds":120}
        """;
        var req = JsonSerializer.Deserialize<RecordAiUsageRequest>(body, WireOptions)!;

        var result = await ctrl.RecordAsync(req, "secret-token");

        Assert.IsType<OkObjectResult>(result);
        var row = await tdb.Db.AiUsageLogs.SingleAsync();
        Assert.Equal(120, row.AudioSeconds);
        Assert.Equal(0.012m, row.CostUsd);
    }

    // ── Lớp phát hiện trên production khi hợp đồng dây lệch ──────────────────────────────────

    /// <summary>ILogger giả, gom message cảnh báo — đủ cho việc khẳng định "có kêu", không kéo Moq vào.</summary>
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Warnings { get; } = new();
        IDisposable? ILogger.BeginScope<TState>(TState state) => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex,
            Func<TState, Exception?, string> formatter)
        {
            if (level == LogLevel.Warning) Warnings.Add(formatter(state, ex));
        }
    }

    /// <summary>
    /// Ca nguy hiểm nhất còn lại SAU khi mọi test trên đã xanh: AIService đổi tên khoá JSON (hoặc quên gửi)
    /// ⇒ model bán theo phút rơi vào công thức token ⇒ 0 đồng. Test ở CI chỉ khoá được phía .NET, nên
    /// production cần một tiếng kêu — nếu không thì triệu chứng duy nhất là hoá đơn nhà cung cấp không khớp
    /// báo cáo, và phải có người tình cờ đi đối chiếu mới phát hiện.
    /// </summary>
    [Fact]
    public async Task Record_ModelTheoPhut_MaThieuAudioSeconds_ThiKeu()
    {
        using var tdb = new PaymentTestDb();
        var logger = new CapturingLogger<AiUsageService>();
        var svc = new AiUsageService(tdb.Db, Options.Create(Pricing()), logger);

        // Đúng payload sẽ nhận được nếu khoá JSON lệch: model chép lời, 0 token, KHÔNG audioSeconds.
        await svc.RecordAsync(new RecordAiUsageRequest { Operation = "transcribe", Model = WhisperModel });

        Assert.Contains(logger.Warnings, w => w.Contains("audioSeconds"));
    }

    /// <summary>Vế âm: lượt token bình thường KHÔNG được kêu, nếu không cảnh báo thành nhiễu và bị bỏ qua.</summary>
    [Fact]
    public async Task Record_LuotTokenBinhThuong_KhongKeu()
    {
        using var tdb = new PaymentTestDb();
        var logger = new CapturingLogger<AiUsageService>();

        await new AiUsageService(tdb.Db, Options.Create(Pricing()), logger).RecordAsync(TokenReq());

        Assert.DoesNotContain(logger.Warnings, w => w.Contains("audioSeconds"));
    }

    // ── Bảng giá thật trong appsettings ──────────────────────────────────────────────────────

    /// <summary>
    /// Bảng giá là CẤU HÌNH, nên nó im lặng khi thiếu: quên khai giá phút thì chi phí transcribe ghi 0 mà
    /// build vẫn xanh. Test đọc thẳng <c>appsettings.json</c> đang ship để chỗ đó không thể trống.
    ///
    /// ⚠ Chỉ khẳng định "CÓ khai và &gt; 0" — KHÔNG khoá con số. Giá là điểm xuất phát phải đối chiếu bảng
    /// giá nhà cung cấp; khoá cứng 0.006 sẽ biến mỗi lần nhà cung cấp đổi giá thành một test đỏ vô nghĩa.
    /// </summary>
    [Fact]
    public void AppSettings_CoDonGiaTheoPhut_ChoModelChepLoi()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        var settings = new ConfigurationBuilder().AddJsonFile(path).Build()
            .GetSection("AiPricing").Get<AiPricingSettings>();

        Assert.NotNull(settings);
        Assert.True(settings!.ResolvePerMinute(WhisperModel) > 0m,
            "AiPricing:Models:whisper-1:PricePerMinuteUsd chưa khai — chi phí chép lời sẽ ghi 0.");
        // Lưới cho model chép lời chưa kịp vào bảng.
        Assert.True(settings.Default.PricePerMinuteUsd > 0m);
    }
}
