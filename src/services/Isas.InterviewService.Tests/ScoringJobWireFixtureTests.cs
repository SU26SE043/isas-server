using System.Runtime.CompilerServices;
using System.Text.Json;
using Isas.InterviewService.DTOs;
using Isas.InterviewService.Entities;
using Isas.InterviewService.Services;

namespace Isas.InterviewService.Tests;

/// <summary>
/// E9 — file mẫu job chấm ĐÚNG như hàng đợi nhận, để test Python đọc lại ở ranh giới thật.
///
/// <para>🔴 Vì sao cần: job đi qua RabbitMQ được <see cref="ScoringJobPublisher.Serialize"/> tuần tự hoá
/// KHÔNG kèm options ⇒ PascalCase (<c>{"Score":0,"Descriptor":"…"}</c>), trong khi test Python trước đây
/// tự gõ dict camelCase. Mỗi bên xanh trên hợp đồng của CHÍNH MÌNH, nên prompt chấm thật in
/// "Mức None" suốt từ E9 (2026-07-12) mà không test nào đỏ. File mẫu này do CHÍNH hàm serialize của
/// publisher sinh ra, và <c>tests/test_scoring_job_pascal_wire_e9.py</c> phía AIService đọc nó.</para>
///
/// <para>Đổi hình dạng <see cref="ScoringJob"/> ⇒ test đầu ĐỎ. Muốn cập nhật file mẫu: chạy lại với
/// <c>ISAS_UPDATE_WIRE_FIXTURES=1</c> rồi chạy pytest AIService — đừng sửa tay file mẫu.</para>
/// </summary>
public class ScoringJobWireFixtureTests
{
    private const string UpdateEnv = "ISAS_UPDATE_WIRE_FIXTURES";

    [Fact]
    public void FileMau_KhopDungCachHangDoiTuanTuHoa()
    {
        var json = ScoringJobPublisher.Serialize(BuildB2BJob());
        var path = FixturePath();

        if (Environment.GetEnvironmentVariable(UpdateEnv) == "1")
            File.WriteAllText(path, json + "\n");

        Assert.True(File.Exists(path), $"Thiếu file mẫu {path} — chạy với {UpdateEnv}=1 để sinh.");
        Assert.Equal(json, File.ReadAllText(path).TrimEnd('\r', '\n'));
    }

    [Fact]
    public void HangDoi_LaPascalCase_ChoCaMocDiemLanSoDoCachNoi()
    {
        // Khoá đúng thứ worker Python phải đọc được. Nếu ai đó đổi publisher sang camelCase thì test
        // này đỏ — nhắc rằng đó là đổi hợp đồng với worker + republisher, không phải dọn code.
        using var doc = JsonDocument.Parse(ScoringJobPublisher.Serialize(BuildB2BJob()));
        var root = doc.RootElement;

        var level = root.GetProperty("Criteria")[0].GetProperty("Levels")[0];
        Assert.True(level.TryGetProperty("Score", out _));
        Assert.True(level.TryGetProperty("Descriptor", out _));
        Assert.True(root.GetProperty("Criteria")[0].GetProperty("Anchors")[0]
            .TryGetProperty("ExampleAnswer", out _));

        var metrics = root.GetProperty("DeliveryMetrics");
        Assert.True(metrics.TryGetProperty("SpeechRateWpm", out _));
        // Khoá trong FillerBreakdown là DỮ LIỆU (từ đệm) — serializer giữ nguyên, không đổi casing.
        Assert.True(metrics.GetProperty("FillerBreakdown").TryGetProperty("ừm", out _));
    }

    /// <summary>Job B2B như <c>AnswerService</c> đẩy cho câu đầu của một buổi thích ứng: tiêu chí
    /// dựng qua CHÍNH <see cref="ScoringCriteriaBuilder"/> (mốc HR khai {0,5,10} kèm câu neo), có
    /// transcript + số đo cách nói đo sẵn ở <c>/decide-next</c>, đáp án mẫu HR soạn.</summary>
    internal static ScoringJob BuildB2BJob()
    {
        var depth = new RubricCriterion
        {
            Id = Guid.Parse("e9000000-0000-0000-0000-000000000001"),
            Name = "Chiều sâu kỹ thuật",
            Description = "Hiểu cơ chế bên dưới và nêu được đánh đổi",
            MaxScore = 10,
            Weight = 0.6m,
            Language = "vi",
        };
        // Thêm lệch thứ tự để file mẫu phản ánh đúng việc builder sắp mốc tăng dần.
        depth.Levels.Add(Level(depth, 10, "Giải thích đúng cơ chế, có ví dụ từ dự án thật và phân tích đánh đổi.",
            "Bên em dùng Redis theo kiểu cache-aside, TTL 5 phút; đánh đổi là dữ liệu có thể cũ tối đa 5 phút."));
        depth.Levels.Add(Level(depth, 0, "Không trả lời được hoặc sai bản chất vấn đề.", "Em không biết ạ."));
        depth.Levels.Add(Level(depth, 5, "Nêu đúng cơ chế cốt lõi nhưng chưa có ví dụ thật hay đánh đổi."));

        var communication = new RubricCriterion
        {
            Id = Guid.Parse("e9000000-0000-0000-0000-000000000002"),
            Name = "Giao tiếp & trình bày",
            Description = "Mạch lạc, có cấu trúc",
            MaxScore = 5,
            Weight = 0.4m,
            Language = "vi",
        };
        communication.Levels.Add(Level(communication, 0, "Lan man, không có cấu trúc."));
        communication.Levels.Add(Level(communication, 2, "Có ý chính nhưng thiếu mạch lạc."));
        communication.Levels.Add(Level(communication, 5, "Mạch lạc, có mở-thân-kết rõ ràng."));

        return new ScoringJob
        {
            AnswerId = Guid.Parse("e9000000-0000-0000-0000-0000000000a1"),
            SessionId = Guid.Parse("e9000000-0000-0000-0000-0000000000b1"),
            QuestionId = Guid.Parse("e9000000-0000-0000-0000-0000000000c1"),
            AudioObjectKey = "answer-audio/e9-candidate/e9-question.webm",
            QuestionContent = "Anh/chị đã dùng cache ở đâu trong dự án và đánh đổi là gì?",
            SampleAnswer = "Dùng Redis cache-aside cho danh mục sản phẩm, TTL ngắn, chấp nhận dữ liệu cũ vài phút.",
            JobCategory = "BE",
            Language = "vi",
            RubricVersion = 2,
            Criteria = ScoringCriteriaBuilder.Build([depth, communication]),
            AttemptNo = 1,
            Temperature = 0,
            Transcript = "Dạ bên em dùng Redis, ừm, để cache danh mục sản phẩm, kiểu như TTL năm phút.",
            TranscriptEngine = "whisper-1",
            DeliveryMetrics = new DeliveryMetricsDto
            {
                AudioSec = 42.5,
                SpeechSec = 31.2,
                WordCount = 118,
                SpeechRateWpm = 226.9,
                LongestPauseSec = 2.4,
                PauseCount = 5,
                SilenceRatio = 0.266,
                FillerCount = 4,
                FillerPer100Words = 3.39,
                FillerBreakdown = new Dictionary<string, int> { ["ừm"] = 3, ["kiểu như"] = 1 },
                MetricsVersion = 2,
            },
            Seniority = null,   // B2B: không hiệu chỉnh theo cấp độ (CAMP-10)
            CampaignId = Guid.Parse("e9000000-0000-0000-0000-0000000000d1"),
            CandidateId = Guid.Parse("e9000000-0000-0000-0000-0000000000e1"),
        };
    }

    private static RubricLevel Level(RubricCriterion c, int score, string descriptor, params string[] examples)
        => new()
        {
            CriterionId = c.Id,
            Criterion = c,
            Score = score,
            Descriptor = descriptor,
            ExampleAnswers = examples.ToList(),
        };

    private static string FixturePath([CallerFilePath] string here = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "Isas.AIService",
            "tests", "fixtures", "scoring_job_pascal.json"));
}
