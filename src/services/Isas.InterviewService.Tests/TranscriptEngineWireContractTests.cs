using System.Net;
using System.Text;
using System.Text.Json;
using Isas.InterviewService.DTOs;
using Isas.InterviewService.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Isas.InterviewService.Tests;

/// <summary>
/// Khoá HỢP ĐỒNG DÂY của con dấu engine giữa .NET và AIService (Python).
///
/// <para><b>Vì sao bộ test này tồn tại:</b> lệch tên khoá giữa hai ngôn ngữ KHÔNG ném lỗi ở đâu cả —
/// .NET bind hụt thì property về <c>null</c>, pydantic <c>extra='ignore'</c> thì nuốt field lạ.
/// Hai bên vẫn xanh 100% vì mỗi bên chỉ assert hợp đồng của CHÍNH mình. Repo đã dính đúng kiểu này
/// ba lần: <c>focusCriteria</c> bị pydantic nuốt (BC14 hỏng nhiều tuần), <c>adaptiveMaxQuestions</c>
/// vs <c>maxQuestions</c> khiến mọi gói trả phí nhận trần 0 câu, và <c>promptVersion</c> suýt để cột
/// NULL vĩnh viễn (BK23 — khe đó được bịt bằng đúng loại test này).</para>
///
/// <para><b>⚠ CÓ HAI DÂY, HAI QUY ƯỚC ĐẶT TÊN KHÁC NHAU</b> — chỗ dễ vấp nhất:</para>
/// <list type="number">
/// <item>HTTP (callback chấm · response <c>/decide-next</c>) → <b>camelCase</b>
/// (<c>transcriptEngine</c>). ASP.NET dùng <c>JsonSerializerDefaults.Web</c>, và
/// <see cref="AiServiceInterviewDecider"/> khai options camelCase + case-insensitive.</item>
/// <item>RabbitMQ (<see cref="ScoringJob"/> .NET → worker Python) → <b>PascalCase</b>
/// (<c>TranscriptEngine</c>), vì <c>ScoringJobPublisher</c> gọi
/// <c>JsonSerializer.Serialize(job)</c> KHÔNG kèm options ⇒ lấy
/// <c>JsonSerializerOptions.Default</c> chứ không phải Web defaults. Worker Python vốn đã phòng thủ
/// bằng <c>body.get("x") or body.get("X")</c> cho <c>transcript</c>/<c>deliveryMetrics</c> — con dấu
/// engine PHẢI được đọc theo đúng mẫu đó.</item>
/// </list>
/// </summary>
public sealed class TranscriptEngineWireContractTests
{
    private const string EngineSentinel = "ENGINE_SENTINEL_v1";

    // ⚠ Sentinel ASCII có chủ đích: System.Text.Json escape non-ASCII, nên assert chuỗi tiếng Việt
    // vào JSON đã serialize sẽ xanh/đỏ một cách vô nghĩa (bài học F17, đã quét toàn repo).
    private const string TranscriptSentinel = "TRANSCRIPT_SENTINEL";

    // ── (1) Callback chấm: Python POST → .NET bind ──────────────────────────────
    // Đây là chiều mà .NET là bên NHẬN, nên bằng chứng đúng nhất là "cho .NET ăn đúng JSON mà Python
    // gửi, rồi xem property có được điền không" — không phải đọc tên property rồi tự suy.

    private static readonly JsonSerializerOptions AspNetLike = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Callback_KhoaCamelCase_BindDuocVaoTranscriptEngine()
    {
        // Khoá `transcriptEngine` là thứ AIService gửi. Đổi tên property C# (hoặc gắn
        // [JsonPropertyName] khác) là gãy hợp đồng IM LẶNG — cột NULL vĩnh viễn, y nguyên con bug
        // BK23 đã phải đi sửa.
        var json = $$"""
            {"transcript":"{{TranscriptSentinel}}","transcriptEngine":"{{EngineSentinel}}",
             "rubricVersion":1,"attemptNo":1,"scores":[]}
            """;

        var req = JsonSerializer.Deserialize<AnswerScoreCallbackRequest>(json, AspNetLike);

        Assert.NotNull(req);
        Assert.Equal(EngineSentinel, req!.TranscriptEngine);
        Assert.Equal(TranscriptSentinel, req.Transcript);
    }

    [Fact]
    public void Callback_KhoaSnakeCase_KHONG_bindDuoc_DayLaLyDoPhaiKhoaTen()
    {
        // Vế chứng minh rủi ro là THẬT, không phải lo hão: `transcript_engine` đi qua trót lọt trên
        // dây, không lỗi ở bên nào, và kết quả là NULL. Nếu ai đó bên Python "chuẩn hoá" sang
        // snake_case thì đây chính xác là thứ sẽ xảy ra — không có lỗi nào để lần theo.
        var json = $$"""
            {"transcript":"{{TranscriptSentinel}}","transcript_engine":"{{EngineSentinel}}",
             "rubricVersion":1,"attemptNo":1,"scores":[]}
            """;

        var req = JsonSerializer.Deserialize<AnswerScoreCallbackRequest>(json, AspNetLike);

        Assert.NotNull(req);
        Assert.Null(req!.TranscriptEngine);
    }

    [Fact]
    public void Callback_VangKhoa_RaNull_KhongNem()
    {
        // Worker/image CŨ không gửi field. Phải parse được bình thường (cột kiểm toán không được
        // biến thành đường làm answer Failed = mất credit, PAY-13).
        var json = $$"""
            {"transcript":"{{TranscriptSentinel}}","rubricVersion":1,"attemptNo":1,"scores":[]}
            """;

        var req = JsonSerializer.Deserialize<AnswerScoreCallbackRequest>(json, AspNetLike);

        Assert.NotNull(req);
        Assert.Null(req!.TranscriptEngine);
    }

    // ── (2) Response /decide-next: Python trả → .NET đọc ────────────────────────

    private sealed class StubHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
    }

    private static async Task<DecideNextResult> DecideWithBodyAsync(string body)
    {
        var http = new HttpClient(new StubHandler(body)) { BaseAddress = new Uri("http://aiservice.test") };
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Internal:Token"] = "TKN" })
            .Build();
        var sut = new AiServiceInterviewDecider(http, config, NullLogger<AiServiceInterviewDecider>.Instance);

        return await sut.DecideNextAsync(new AdaptiveDecisionRequest(
            AudioObjectKey: "answer-audio/x.webm", JobCategory: "BE", CurrentQuestion: "q",
            History: [], AskedCount: 1, FollowUpCount: 0, MaxQuestions: 6, MaxFollowUps: 3,
            Criteria: []));
    }

    [Fact]
    public async Task DecideNext_DocDuocConDauTuResponse()
    {
        // Đường thích ứng là lần chép DUY NHẤT (worker sau đó bỏ Whisper) ⇒ rơi con dấu ở đây là
        // mất vĩnh viễn lai lịch của đúng bản chép đã dùng để chấm.
        var result = await DecideWithBodyAsync($$"""
            {"action":"end","nextQuestion":null,"transcript":"{{TranscriptSentinel}}",
             "reason":"r","transcriptEngine":"{{EngineSentinel}}"}
            """);

        Assert.Equal(EngineSentinel, result.TranscriptEngine);
        Assert.Equal(TranscriptSentinel, result.Transcript);
    }

    [Fact]
    public async Task DecideNext_AiServiceCuKhongTraConDau_RaNull_KhongNem()
    {
        // Deploy lệch nhịp là chuyện thường ở đây; thiếu dấu không được làm hỏng vòng thích ứng.
        var result = await DecideWithBodyAsync($$"""
            {"action":"end","transcript":"{{TranscriptSentinel}}","reason":"r"}
            """);

        Assert.Null(result.TranscriptEngine);
        Assert.Equal(TranscriptSentinel, result.Transcript);
    }

    // ── (3) ScoringJob trên RabbitMQ: .NET gửi → worker Python đọc ──────────────

    [Fact]
    public void ScoringJob_MangKhoaConDau_TheoDungCachPublisherSerialize()
    {
        // Serialize y hệt `ScoringJobPublisher`: `JsonSerializer.Serialize(job)` KHÔNG kèm options
        // ⇒ JsonSerializerOptions.Default ⇒ PascalCase. Đây là điểm khác biệt so với dây HTTP ở trên,
        // và là chỗ dễ hiểu nhầm nhất khi đấu dây phía Python.
        var job = new ScoringJob
        {
            AnswerId = Guid.NewGuid(),
            SessionId = Guid.NewGuid(),
            QuestionId = Guid.NewGuid(),
            AudioObjectKey = "answer-audio/x.webm",
            QuestionContent = "q",
            JobCategory = "BE",
            RubricVersion = 1,
            Transcript = TranscriptSentinel,
            TranscriptEngine = EngineSentinel,
        };

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(job));

        // Chấp nhận CẢ HAI casing: worker Python đọc `body.get("x") or body.get("X")` nên đổi
        // convention serialize KHÔNG phá production ⇒ không đỏ oan. Nhưng xoá field hay đổi sang
        // `transcript_engine` thì rơi khỏi tập này ⇒ ĐỎ, đúng thứ cần bắt.
        var key = doc.RootElement.EnumerateObject()
            .Select(p => p.Name)
            .FirstOrDefault(n => n is "transcriptEngine" or "TranscriptEngine");

        Assert.True(key is not null,
            "ScoringJob không mang con dấu engine dưới tên worker Python đọc được " +
            "('transcriptEngine' hoặc 'TranscriptEngine'). Worker bỏ Whisper khi job có transcript " +
            "nên nó KHÔNG tự biết engine — thiếu khoá này là con dấu chết ở đường republisher.");
        Assert.Equal(EngineSentinel, doc.RootElement.GetProperty(key!).GetString());

        // Con dấu vô nghĩa nếu bản chép nó mô tả không đi cùng chuyến.
        var tKey = doc.RootElement.EnumerateObject()
            .Select(p => p.Name)
            .First(n => n is "transcript" or "Transcript");
        Assert.Equal(TranscriptSentinel, doc.RootElement.GetProperty(tKey).GetString());
    }
}
