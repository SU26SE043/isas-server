using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using Isas.CampaignService.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Isas.CampaignService.Tests;

/// <summary>
/// E11c — hợp đồng chéo Campaign ⇄ Interview cho transcript + audio.
///
/// <para>Lớp bug đã cắn repo BỐN lần: khoá JSON lệch tên (focusCriteria · metricsVersion · adaptiveMaxQuestions ·
/// ScoreFallback) ⇒ field rơi về default IM LẶNG, test hai bên vẫn xanh vì mỗi bên chỉ assert hợp đồng của mình.
/// Hai lá chắn ở đây: (1) feed JSON đúng shape Interview <c>QuestionResponse</c>/<c>AnswerResponse</c>/
/// <c>DeliveryMetricsDto</c> vào client thật, assert mọi field mới KHÔNG rỗng sau map; (2) đọc THẲNG file DTO của
/// Interview và khẳng định từng tên field client dùng có mặt ở đó.</para>
/// </summary>
public class SessionTranscriptClientContractE11cTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;
        private readonly string _contentType;
        public string? CapturedUri { get; private set; }
        public string? CapturedToken { get; private set; }

        public StubHandler(HttpStatusCode status, string body, string contentType = "application/json")
        {
            _status = status; _body = body; _contentType = contentType;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            CapturedUri = request.RequestUri?.PathAndQuery;
            CapturedToken = request.Headers.TryGetValues("X-Internal-Token", out var v) ? string.Join(",", v) : null;
            return Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, Encoding.UTF8, _contentType)
            });
        }
    }

    private static CampaignSessionClient NewClient(StubHandler handler)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://interview.test") };
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Internal:Token"] = "tkn" })
            .Build();
        return new CampaignSessionClient(http, config, NullLogger<CampaignSessionClient>.Instance);
    }

    // JSON ĐÚNG SHAPE Interview trả (camelCase như JsonSerializerDefaults.Web của ASP.NET): 1 câu gốc đã chấm đủ
    // field E11c, 1 câu đào sâu im lặng (Skipped + no_speech + có audio), 1 câu chưa nộp (answer null).
    private const string InterviewAnswersJson = """
        [
          {
            "id": "11111111-1111-1111-1111-111111111111", "orderNo": 1, "content": "Câu gốc", "timeLimitSec": 120,
            "kind": "Seed",
            "answer": {
              "id": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", "status": "Scored", "durationSec": 84,
              "transcript": "Tôi bật logging để xem SQL…",
              "scores": [ { "criterionId": "cccccccc-cccc-cccc-cccc-cccccccccccc", "score": 8, "reasoning": "Trích: 'bật logging'",
                            "rubricVersion": 1, "levelMatched": 8, "criterionName": "Chiều sâu kỹ thuật", "maxScore": 10 } ],
              "needsReview": true,
              "sampleAnswer": "Câu trả lời mẫu mức tối đa…",
              "deliveryMetrics": { "audioSec": 90.5, "speechSec": 70.2, "wordCount": 210, "speechRateWpm": 299.3,
                                   "longestPauseSec": 2.1, "pauseCount": 3, "silenceRatio": 0.099, "fillerCount": 4,
                                   "fillerPer100Words": 1.9, "fillerBreakdown": { "ừm": 3, "à": 1 }, "metricsVersion": 2 },
              "audioUrl": "/api/v1/interview/practice/sessions/x/answers/aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa/audio",
              "rejectReason": null
            }
          },
          {
            "id": "22222222-2222-2222-2222-222222222222", "orderNo": 2, "content": "AI làm rõ", "timeLimitSec": 120,
            "kind": "Clarify",
            "answer": {
              "id": "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb", "status": "Skipped", "durationSec": 6, "transcript": null,
              "scores": [], "needsReview": false, "sampleAnswer": null, "deliveryMetrics": null,
              "audioUrl": "/api/v1/interview/practice/sessions/x/answers/bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb/audio",
              "rejectReason": "no_speech"
            }
          },
          { "id": "33333333-3333-3333-3333-333333333333", "orderNo": 3, "content": "Bỏ trống", "timeLimitSec": 120,
            "kind": "Seed", "answer": null }
        ]
        """;

    [Fact]
    public async Task Transcript_MapDuFieldE11c_TuJsonThatCuaInterview()
    {
        var handler = new StubHandler(HttpStatusCode.OK, InterviewAnswersJson);
        var sessionId = Guid.NewGuid();

        var result = await NewClient(handler).GetSessionTranscriptAsync(sessionId);

        Assert.Equal($"/internal/sessions/{sessionId}/answers", handler.CapturedUri);
        Assert.Equal("tkn", handler.CapturedToken);
        Assert.Equal(3, result.Questions.Count);

        var q1 = result.Questions[0];
        Assert.Equal(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), q1.AnswerId);
        Assert.Equal("Seed", q1.Kind);
        Assert.Equal("Scored", q1.AnswerStatus);
        Assert.Null(q1.RejectReason);
        Assert.Equal(84, q1.DurationSec);
        Assert.True(q1.HasAudio);
        Assert.Equal("Câu trả lời mẫu mức tối đa…", q1.SampleAnswer);
        Assert.True(q1.NeedsReview);
        Assert.NotNull(q1.DeliveryMetrics);
        Assert.Equal(299.3, q1.DeliveryMetrics!.SpeechRateWpm);
        Assert.Equal(3, q1.DeliveryMetrics.PauseCount);
        Assert.Equal(2.1, q1.DeliveryMetrics.LongestPauseSec);
        Assert.Equal(0.099, q1.DeliveryMetrics.SilenceRatio);
        Assert.Equal(4, q1.DeliveryMetrics.FillerCount);
        Assert.Equal(3, q1.DeliveryMetrics.FillerBreakdown["ừm"]);
        var s = Assert.Single(q1.Scores);
        Assert.Equal(8, s.LevelMatched);
        Assert.Equal("Chiều sâu kỹ thuật", s.CriterionName);

        // Im lặng: Skipped + no_speech + CÓ audio (khác "bỏ trống").
        var q2 = result.Questions[1];
        Assert.Equal("Clarify", q2.Kind);
        Assert.Equal("Skipped", q2.AnswerStatus);
        Assert.Equal("no_speech", q2.RejectReason);
        Assert.True(q2.HasAudio);
        Assert.Null(q2.DeliveryMetrics);   // null = chưa đo, KHÔNG phải bộ số 0

        // Chưa nộp: mọi thứ null/false, kind vẫn map.
        var q3 = result.Questions[2];
        Assert.Null(q3.AnswerId);
        Assert.Null(q3.AnswerStatus);
        Assert.False(q3.HasAudio);
        Assert.Null(q3.DurationSec);
        Assert.Equal("Seed", q3.Kind);
    }

    [Fact]
    public async Task Transcript_InterviewCuThieuFieldMoi_VanMapAnToan()
    {
        // Bản Interview trước E11c: không có kind/sampleAnswer/deliveryMetrics/audioUrl/rejectReason/levelMatched.
        const string legacy = """
            [ { "id": "11111111-1111-1111-1111-111111111111", "orderNo": 1, "content": "Q", "timeLimitSec": 120,
                "answer": { "id": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", "status": "Scored", "durationSec": 10,
                            "transcript": "t", "scores": [ { "criterionId": "cccccccc-cccc-cccc-cccc-cccccccccccc",
                            "score": 3, "reasoning": "r", "rubricVersion": 1 } ], "needsReview": false } } ]
            """;
        var q = Assert.Single((await NewClient(new StubHandler(HttpStatusCode.OK, legacy)).GetSessionTranscriptAsync(Guid.NewGuid())).Questions);
        Assert.Equal("Seed", q.Kind);
        Assert.False(q.HasAudio);
        Assert.Null(q.SampleAnswer);
        Assert.Null(q.DeliveryMetrics);
        Assert.Null(q.RejectReason);
        Assert.Null(Assert.Single(q.Scores).LevelMatched);
    }

    // Lá chắn (2): tên field client dùng PHẢI có trong DTO Interview. Đọc file nguồn thật — nếu Interview đổi tên
    // (vd. RejectReason → RejectedReason) thì test này ĐỎ, thay vì field rơi về default trong im lặng.
    [Theory]
    [InlineData("QuestionResponse", "Kind")]
    [InlineData("QuestionResponse", "Answer")]
    [InlineData("AnswerResponse", "Status")]
    [InlineData("AnswerResponse", "DurationSec")]
    [InlineData("AnswerResponse", "NeedsReview")]
    [InlineData("AnswerResponse", "SampleAnswer")]
    [InlineData("AnswerResponse", "DeliveryMetrics")]
    [InlineData("AnswerResponse", "AudioUrl")]
    [InlineData("AnswerResponse", "RejectReason")]
    [InlineData("AnswerScoreResponse", "LevelMatched")]
    [InlineData("AnswerScoreResponse", "CriterionName")]
    [InlineData("AnswerScoreResponse", "MaxScore")]
    public void InterviewDto_CoFieldMaClientDoc(string record, string field)
    {
        var dtoFile = Path.Combine(RepoRoot(), "src", "services", "Isas.InterviewService", "DTOs", "PracticeSession.cs");
        var src = File.ReadAllText(dtoFile);
        var start = src.IndexOf($"public record {record}(", StringComparison.Ordinal);
        Assert.True(start >= 0, $"Không thấy record {record} trong {dtoFile}");
        var end = src.IndexOf(");", start, StringComparison.Ordinal);
        var body = src[start..end];
        Assert.Contains($" {field}", body);
    }

    [Theory]
    [InlineData("SpeechRateWpm")]
    [InlineData("PauseCount")]
    [InlineData("LongestPauseSec")]
    [InlineData("SilenceRatio")]
    [InlineData("FillerCount")]
    [InlineData("FillerBreakdown")]
    public void InterviewDeliveryMetricsDto_CoFieldMaClientDoc(string field)
    {
        var dtoFile = Path.Combine(RepoRoot(), "src", "services", "Isas.InterviewService", "DTOs", "DeliveryMetrics.cs");
        var src = File.ReadAllText(dtoFile);
        var start = src.IndexOf("public class DeliveryMetricsDto", StringComparison.Ordinal);
        Assert.True(start >= 0);
        Assert.Contains($" {field} {{", src[start..]);
    }

    // ── audio ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Audio_200_TraStreamVaContentTypeCuaInterview()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "RIFFxxxx", "audio/wav");
        var sid = Guid.NewGuid(); var aid = Guid.NewGuid();

        var audio = await NewClient(handler).GetAnswerAudioAsync(sid, aid);

        Assert.NotNull(audio);
        Assert.Equal("audio/wav", audio!.ContentType);
        Assert.Equal($"/internal/sessions/{sid}/answers/{aid}/audio", handler.CapturedUri);
        Assert.Equal("tkn", handler.CapturedToken);
        using var reader = new StreamReader(audio.Content);
        Assert.Equal("RIFFxxxx", await reader.ReadToEndAsync());
    }

    [Fact]
    public async Task Audio_404_TraNull_KhongNem()
    {
        var audio = await NewClient(new StubHandler(HttpStatusCode.NotFound, "{\"error\":\"x\"}")).GetAnswerAudioAsync(Guid.NewGuid(), Guid.NewGuid());
        Assert.Null(audio);
    }

    [Fact]
    public async Task Audio_500_NemDownstream()
    {
        await Assert.ThrowsAsync<DownstreamServiceException>(() =>
            NewClient(new StubHandler(HttpStatusCode.InternalServerError, "boom")).GetAnswerAudioAsync(Guid.NewGuid(), Guid.NewGuid()));
    }

    private static string RepoRoot([CallerFilePath] string here = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", ".."));
}
