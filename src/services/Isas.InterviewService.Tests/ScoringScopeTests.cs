using System.Net;
using System.Text;
using System.Text.Json;
using Isas.InterviewService.ApplicationDbContext;
using Isas.InterviewService.Data;
using Isas.InterviewService.DTOs;
using Isas.InterviewService.Entities;
using Isas.InterviewService.Enums;
using Isas.InterviewService.Models;
using Isas.InterviewService.Services;
using Isas.InterviewService.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace Isas.InterviewService.Tests;

/// <summary>
/// Phạm vi chấm theo câu hỏi: 4 tiêu chí CÁCH NÓI luôn chấm, tiêu chí NỘI DUNG chỉ chấm khi câu hỏi
/// nhắm tới. Khoá cả ba trạng thái nhãn (<c>null</c> / <c>[]</c> / non-empty) ở MỌI tầng — hợp đồng
/// dây với AIService, lưu trữ, và cả HAI đường đẩy job chấm.
/// </summary>
public class ScoringScopeTests
{
    // ── (1) Phân loại tiêu chí trong seed ────────────────────────────────────────────
    // Khoá tỉ lệ 4 CÁCH NÓI / 3 NỘI DUNG mỗi nghề mỗi ngôn ngữ. Đây là thứ duy nhất giữ cho phân
    // loại không trôi khi ai đó thêm/sửa tiêu chí seed — và trôi ở đây thì không có triệu chứng nào
    // ngoài việc điểm lặng lẽ đổi nghĩa.
    [Theory]
    [InlineData(JobCategory.BA)]
    [InlineData(JobCategory.BE)]
    [InlineData(JobCategory.FE)]
    public void Seed_EachJobCategory_Has4AlwaysAnd3WhenTargeted_InBothLanguages(JobCategory cat)
    {
        var seed = B2CRubricSeed.Build();
        foreach (var lang in new[] { "vi", "en" })
        {
            var forJob = seed.Where(c => c.JobCategory == cat && c.Language == lang).ToList();
            Assert.Equal(7, forJob.Count);
            Assert.Equal(4, forJob.Count(c => c.ScoringScope == ScoringScope.Always));
            Assert.Equal(3, forJob.Count(c => c.ScoringScope == ScoringScope.WhenTargeted));
        }
    }

    // 4 tiêu chí CÁCH NÓI đúng là 4 tiêu chí team đã chốt. Nhận diện bằng TÊN chỉ hợp lệ TRONG TEST
    // (seed do ta viết ra, tên là hằng số ở đây); production KHÔNG được khớp tên — rubric có 2 ngôn
    // ngữ và candidate tự đặt tên rubric riêng (BC16).
    [Fact]
    public void Seed_AlwaysScored_AreExactlyTheFourDeliveryCriteria()
    {
        var names = B2CRubricSeed.Build()
            .Where(c => c.Language == "vi" && c.ScoringScope == ScoringScope.Always)
            .Select(c => c.Name)
            .Distinct()
            .OrderBy(n => n)
            .ToList();

        Assert.Equal(
            new[] { "Giao tiếp & trình bày", B2CRubricSeed.FluencyName, B2CRubricSeed.LanguageName, B2CRubricSeed.TerminologyName }
                .OrderBy(n => n).ToList(),
            names);
    }

    // Hai ngôn ngữ của CÙNG một tiêu chí không được lệch phạm vi chấm (id 'en' = id 'vi' xor byte đầu).
    [Fact]
    public void Seed_EnglishInheritsScopeFromVietnamese()
    {
        var seed = B2CRubricSeed.Build();
        var vi = seed.Where(c => c.Language == "vi").ToDictionary(c => c.Id, c => c.ScoringScope);
        var en = seed.Where(c => c.Language == "en").ToList();
        Assert.NotEmpty(en);

        foreach (var e in en)
        {
            var bytes = e.Id.ToByteArray();
            bytes[0] ^= 0x11;
            Assert.Equal(vi[new Guid(bytes)], e.ScoringScope);
        }
    }

    // Tiêu chí tạo ra mà KHÔNG khai phạm vi (rubric riêng BC16 · tiêu chí campaign B2B materialize ·
    // mọi đường ghi tương lai) phải rơi về `Always` = chấm mọi câu = hành vi hôm nay. Chiều mặc định
    // an toàn là "chấm thừa"; lật nó thành WhenTargeted sẽ khiến những tiêu chí đó im lặng BIẾN MẤT
    // khỏi mọi lượt chấm mà không endpoint nào báo lỗi.
    [Fact]
    public void NewCriterion_WithoutExplicitScope_DefaultsToAlwaysScored()
        => Assert.Equal(ScoringScope.Always, new RubricCriterion().ScoringScope);

    // B2B: tiêu chí campaign materialize qua đường internal cũng phải là Always ⇒ chấm y như trước
    // (Campaign không gửi nhãn nên câu B2B không có nhãn, nhưng nếu ai đó bật nhãn cho B2B sau này
    // thì mặc định này vẫn giữ cho tiêu chí HR khai không bị bỏ chấm).
    [Fact]
    public async Task CampaignCriteria_AreMaterializedAsAlwaysScored()
    {
        using var t = new TestDb();
        var gen = new Mock<IAiServiceQuestionGenerator>();
        var campaignId = Guid.NewGuid();
        var req = new CreateCampaignSessionRequest(
            campaignId, Guid.NewGuid(), JobCategory.BE,
            Questions: new[] { "Q1" },
            Criteria: new[] { new CampaignCriterionInput("Technical depth", null, 1.0m, 5) });

        await Practicing(t, gen).CreateCampaignSessionAsync(Guid.NewGuid(), req);

        var criteria = await t.Db.RubricCriteria.AsNoTracking()
            .Where(c => c.CampaignId == campaignId).ToListAsync();
        Assert.NotEmpty(criteria);
        Assert.All(criteria, c => Assert.Equal(ScoringScope.Always, c.ScoringScope));
    }

    // ── (2) ScoringScopeFilter — 3 trạng thái nhãn ───────────────────────────────────
    private static RubricCriterion C(string name, ScoringScope scope)
        => new()
        {
            Id = Guid.NewGuid(), Name = name, Weight = 0.1m, MaxScore = 5,
            IsActive = true, JobCategory = JobCategory.BE, Language = "vi",
            ScoringScope = scope
        };

    private static List<RubricCriterion> SevenCriteria(out List<RubricCriterion> content)
    {
        content =
        [
            C("Chiều sâu kỹ thuật", ScoringScope.WhenTargeted),
            C("Thiết kế hệ thống & CSDL", ScoringScope.WhenTargeted),
            C("Giải quyết vấn đề & thuật toán", ScoringScope.WhenTargeted),
        ];
        var delivery = new List<RubricCriterion>
        {
            C("Giao tiếp & trình bày", ScoringScope.Always),
            C(B2CRubricSeed.FluencyName, ScoringScope.Always),
            C(B2CRubricSeed.LanguageName, ScoringScope.Always),
            C(B2CRubricSeed.TerminologyName, ScoringScope.Always),
        };
        return delivery.Concat(content).ToList();
    }

    // null = CHƯA HỎI → lùi an toàn, chấm ĐỦ rubric (hành vi trước thay đổi này).
    [Fact]
    public void Filter_NullLabel_KeepsAllCriteria()
    {
        var all = SevenCriteria(out _);
        Assert.Equal(7, ScoringScopeFilter.Apply(all, null).Count);
    }

    // [] = ĐÃ HỎI, câu không nhắm tiêu chí nội dung nào (câu xã giao) → CHỈ 4 tiêu chí cách nói.
    // ⚠ Ca này PHẢI khác ca null. Gộp hai ca làm tính năng vô hiệu đúng ở nhóm câu cần nó nhất.
    [Fact]
    public void Filter_EmptyLabel_KeepsOnlyAlwaysScoredCriteria()
    {
        var all = SevenCriteria(out _);
        var scoped = ScoringScopeFilter.Apply(all, Array.Empty<Guid>());

        Assert.Equal(4, scoped.Count);
        Assert.All(scoped, c => Assert.Equal(ScoringScope.Always, c.ScoringScope));
    }

    [Fact]
    public void Filter_TargetedLabel_KeepsAlwaysPlusTargetedOnly()
    {
        var all = SevenCriteria(out var content);
        var scoped = ScoringScopeFilter.Apply(all, new[] { content[1].Id });   // chỉ "Thiết kế hệ thống"

        Assert.Equal(5, scoped.Count);
        Assert.Contains(scoped, c => c.Id == content[1].Id);
        Assert.DoesNotContain(scoped, c => c.Id == content[0].Id);
        Assert.DoesNotContain(scoped, c => c.Id == content[2].Id);
    }

    // Lọc ra rỗng (rubric riêng BC16 sửa giữa buổi → id trong nhãn không còn active) → chấm ĐỦ.
    // Trả bộ rỗng thì caller bỏ publish ⇒ answer không bao giờ được chấm ⇒ mất 1 credit (PAY-13).
    [Fact]
    public void Filter_NoCriterionSurvives_FallsBackToFullRubric()
    {
        var onlyContent = new List<RubricCriterion>
        {
            C("Chiều sâu kỹ thuật", ScoringScope.WhenTargeted),
        };
        var scoped = ScoringScopeFilter.Apply(onlyContent, new[] { Guid.NewGuid() });   // id lạ hoàn toàn
        Assert.Single(scoped);
    }

    // ── (3) Hợp đồng dây với AIService ───────────────────────────────────────────────
    private sealed class CaptureHandler(string json) : HttpMessageHandler
    {
        public string? LastBody { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        }
    }

    private static (AiServiceQuestionGenerator gen, CaptureHandler handler) Generator(string responseJson)
    {
        var handler = new CaptureHandler(responseJson);
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://aiapi:8000") };
        var config = new ConfigurationBuilder().Build();
        return (new AiServiceQuestionGenerator(http, config, NullLogger<AiServiceQuestionGenerator>.Instance), handler);
    }

    // 🔒 KHOÁ DÂY GỬI ĐI: `criteria: [{criterionId, name}]` camelCase. Lệch tên KHÔNG ném lỗi ở đâu —
    // Python chỉ im lặng bỏ trường (repo đã dính 3 lần). Assert vào chuỗi JSON THẬT.
    [Fact]
    public async Task Wire_SendsCriteriaAsCamelCaseCriterionIdAndName()
    {
        var (gen, handler) = Generator("""{"questions":["Q1"]}""");
        var id = Guid.NewGuid();

        await gen.GenerateQuestionsAsync(
            "BE", null, null, null, null, null, "vi",
            new[] { new QuestionTargetCriterionDto(id, "Thiết kế hệ thống & CSDL") });

        var body = handler.LastBody!;
        using var doc = JsonDocument.Parse(body);
        var criteria = doc.RootElement.GetProperty("criteria");
        Assert.Equal(1, criteria.GetArrayLength());
        Assert.Equal(id.ToString(), criteria[0].GetProperty("criterionId").GetString());
        Assert.Equal("Thiết kế hệ thống & CSDL", criteria[0].GetProperty("name").GetString());
    }

    // Không có tiêu chí nội dung nào → gửi null ⇒ AIService không gắn nhãn (hành vi cũ).
    [Fact]
    public async Task Wire_NoCriteria_SendsNull()
    {
        var (gen, handler) = Generator("""{"questions":["Q1"]}""");
        await gen.GenerateQuestionsAsync("BE", null, null, null, null, null, "vi", criteria: null);

        using var doc = JsonDocument.Parse(handler.LastBody!);
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("criteria").ValueKind);
    }

    // 🔒 KHOÁ DÂY NHẬN VỀ: `targetCriteria` là mảng SONG SONG theo index với `questions`.
    // Thiếu property này trên DTO đọc response thì System.Text.Json bỏ qua KHÔNG MỘT TIẾNG ĐỘNG.
    [Fact]
    public async Task Wire_ParsesTargetCriteria_ByIndex()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var json = $$"""
            {"questions":["Q1","Q2"],
             "targetCriteria":[["{{a}}"],["{{a}}","{{b}}"]]}
            """;
        var (gen, _) = Generator(json);

        var result = await gen.GenerateQuestionsAsync(
            "BE", null, null, null, null, null, "vi",
            new[] { new QuestionTargetCriterionDto(a, "A"), new QuestionTargetCriterionDto(b, "B") });

        Assert.Equal(new[] { a }, result.Questions[0].TargetCriterionIds);
        Assert.Equal(new[] { a, b }, result.Questions[1].TargetCriterionIds);
    }

    // `targetCriteria[i] == []` → GIỮ mảng rỗng (câu xã giao), KHÔNG quy về null.
    [Fact]
    public async Task Wire_EmptyTargetForQuestion_StaysEmpty_NotNull()
    {
        var a = Guid.NewGuid();
        var (gen, _) = Generator($$"""{"questions":["Chào bạn"],"targetCriteria":[[]]}""");

        var result = await gen.GenerateQuestionsAsync(
            "BE", null, null, null, null, null, "vi",
            new[] { new QuestionTargetCriterionDto(a, "A") });

        Assert.NotNull(result.Questions[0].TargetCriterionIds);
        Assert.Empty(result.Questions[0].TargetCriterionIds!);
    }

    // AIService không trả `targetCriteria` (image cũ) → null cho mọi câu ⇒ chấm đủ rubric.
    [Fact]
    public async Task Wire_MissingTargetCriteria_YieldsNull()
    {
        var (gen, _) = Generator("""{"questions":["Q1"]}""");
        var result = await gen.GenerateQuestionsAsync(
            "BE", null, null, null, null, null, "vi",
            new[] { new QuestionTargetCriterionDto(Guid.NewGuid(), "A") });

        Assert.Null(result.Questions[0].TargetCriterionIds);
    }

    // GUARD by-construction: id KHÔNG nằm trong tập ta gửi đi bị DROP (AIService không bịa được tiêu
    // chí ngoài rubric để lái phạm vi chấm). Toàn id lạ → null (chấm đủ) chứ KHÔNG phải [] — "gọi tên
    // sai" khác hẳn lời khẳng định "câu này không nhắm tiêu chí nào".
    [Fact]
    public async Task Wire_DropsCriterionIdNotInProvidedSet()
    {
        var known = Guid.NewGuid();
        var ghost = Guid.NewGuid();
        var (gen, _) = Generator($$"""{"questions":["Q1","Q2"],"targetCriteria":[["{{known}}","{{ghost}}"],["{{ghost}}"]]}""");

        var result = await gen.GenerateQuestionsAsync(
            "BE", null, null, null, null, null, "vi",
            new[] { new QuestionTargetCriterionDto(known, "A") });

        Assert.Equal(new[] { known }, result.Questions[0].TargetCriterionIds);
        Assert.Null(result.Questions[1].TargetCriterionIds);   // toàn id lạ → không đủ tin để thu hẹp
    }

    // ── (4) Đường publish lúc upload (AnswerService) ─────────────────────────────────
    private static (AnswerService svc, Mock<IScoringJobPublisher> pub) Answering(TestDb t)
    {
        var storage = new Mock<IStorageService>();
        storage.Setup(s => s.UploadAsync(
                It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<Guid>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("answer-audio/x.webm");
        var pub = new Mock<IScoringJobPublisher>();
        var svc = new AnswerService(
            t.Db, storage.Object, pub.Object, new Mock<ISessionScoringNotifier>().Object,
            Options.Create(new ScoringOptions()), NullLogger<AnswerService>.Instance);
        return (svc, pub);
    }

    // Dựng buổi B2C với 7 tiêu chí (4 Always + 3 WhenTargeted) và 1 câu hỏi mang nhãn cho trước.
    private static (PracticeSession session, PracticeQuestion question, List<RubricCriterion> content)
        SeedSession(TestDb t, List<Guid>? labels)
    {
        var candidate = Guid.NewGuid();
        var session = TestDb.Session(candidate, SessionStatus.Ready, JobCategory.BE);
        var question = TestDb.Question(session.Id);
        question.TargetCriterionIds = labels;

        var all = SevenCriteria(out var content);
        t.Db.AddRange(session, question);
        t.Db.RubricCriteria.AddRange(all);
        t.Db.SaveChanges();
        return (session, question, content);
    }

    private static async Task<ScoringJob> UploadAndCapture(
        TestDb t, PracticeSession session, PracticeQuestion question)
    {
        var (svc, pub) = Answering(t);
        ScoringJob? captured = null;
        pub.Setup(p => p.PublishAsync(It.IsAny<ScoringJob>(), It.IsAny<CancellationToken>()))
            .Callback<ScoringJob, CancellationToken>((j, _) => captured = j)
            .Returns(Task.CompletedTask);

        await svc.UploadAnswerAsync(
            session.Id, question.Id, session.CandidateId,
            new MemoryStream([1, 2, 3]), "audio/webm", 30);

        Assert.NotNull(captured);
        return captured!;
    }

    [Fact]
    public async Task Publish_QuestionWithLabel_SendsFourAlwaysPlusTargetedOnly()
    {
        using var t = new TestDb();
        var (session, question, content) = SeedSession(t, null);
        question.TargetCriterionIds = [content[1].Id];   // "Thiết kế hệ thống & CSDL"
        await t.Db.SaveChangesAsync();

        var job = await UploadAndCapture(t, session, question);

        Assert.Equal(5, job.Criteria.Count);
        Assert.Contains(job.Criteria, c => c.CriterionId == content[1].Id);
        Assert.DoesNotContain(job.Criteria, c => c.CriterionId == content[0].Id);
        Assert.DoesNotContain(job.Criteria, c => c.CriterionId == content[2].Id);
    }

    // LÙI AN TOÀN: câu KHÔNG nhãn → đủ 7 như trước (buổi cũ · B2B · câu thích ứng).
    [Fact]
    public async Task Publish_QuestionWithoutLabel_SendsAllSevenCriteria()
    {
        using var t = new TestDb();
        var (session, question, _) = SeedSession(t, null);

        var job = await UploadAndCapture(t, session, question);

        Assert.Equal(7, job.Criteria.Count);
    }

    // Câu xã giao (nhãn []) → chỉ 4 tiêu chí cách nói, KHÔNG tiêu chí nội dung nào.
    [Fact]
    public async Task Publish_QuestionWithEmptyLabel_SendsOnlyFourDeliveryCriteria()
    {
        using var t = new TestDb();
        var (session, question, content) = SeedSession(t, []);

        var job = await UploadAndCapture(t, session, question);

        Assert.Equal(4, job.Criteria.Count);
        Assert.All(content, c => Assert.DoesNotContain(job.Criteria, x => x.CriterionId == c.Id));
    }

    // ── (5) Đường republisher — PHẢI lọc y hệt ───────────────────────────────────────
    // Hai đường lệch luật = answer nào phải nhờ republisher cứu sẽ được chấm theo luật khác answer
    // chạy trơn tru, mà không có gì báo. Đúng hạng lỗi F11 đã dính ở CHÍNH cặp đường này.
    private static async Task<ScoringJob> RepublishAndCapture(TestDb t)
    {
        var services = new ServiceCollection();
        services.AddDbContext<InterviewDbContext>(o =>
            o.UseSqlite(t.Connection).UseSnakeCaseNamingConvention());
        var provider = services.BuildServiceProvider();

        var pub = new Mock<IScoringJobPublisher>();
        ScoringJob? captured = null;
        pub.Setup(p => p.PublishAsync(It.IsAny<ScoringJob>(), It.IsAny<CancellationToken>()))
            .Callback<ScoringJob, CancellationToken>((j, _) => captured = j)
            .Returns(Task.CompletedTask);

        var republisher = new StuckAnswerRepublisher(
            provider.GetRequiredService<IServiceScopeFactory>(), pub.Object,
            Options.Create(new RepublisherSettings { BatchSize = 200 }),
            Options.Create(new ScoringOptions()),
            NullLogger<StuckAnswerRepublisher>.Instance);

        var mi = typeof(StuckAnswerRepublisher).GetMethod(
            "ScanOnceAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        await (Task)mi.Invoke(republisher, [CancellationToken.None])!;

        Assert.NotNull(captured);
        return captured!;
    }

    // Answer kẹt: Uploaded, chưa publish lần nào, tạo đã lâu → republisher nhặt.
    private static void SeedStuckAnswer(TestDb t, PracticeSession session, PracticeQuestion question)
    {
        session.Status = SessionStatus.InProgress;
        t.Db.PracticeAnswers.Add(TestDb.Answer(
            session.Id, question.Id, AnswerStatus.Uploaded,
            DateTime.UtcNow.AddMinutes(-30), lastPublished: null));
        t.Db.SaveChanges();
    }

    [Fact]
    public async Task Republisher_QuestionWithLabel_AppliesSameScopeFilter()
    {
        using var t = new TestDb();
        var (session, question, content) = SeedSession(t, null);
        question.TargetCriterionIds = [content[1].Id];
        SeedStuckAnswer(t, session, question);

        var job = await RepublishAndCapture(t);

        Assert.Equal(5, job.Criteria.Count);
        Assert.Contains(job.Criteria, c => c.CriterionId == content[1].Id);
        Assert.DoesNotContain(job.Criteria, c => c.CriterionId == content[0].Id);
    }

    [Fact]
    public async Task Republisher_QuestionWithEmptyLabel_SendsOnlyFourDeliveryCriteria()
    {
        using var t = new TestDb();
        var (session, question, content) = SeedSession(t, []);
        SeedStuckAnswer(t, session, question);

        var job = await RepublishAndCapture(t);

        Assert.Equal(4, job.Criteria.Count);
        Assert.All(content, c => Assert.DoesNotContain(job.Criteria, x => x.CriterionId == c.Id));
    }

    [Fact]
    public async Task Republisher_QuestionWithoutLabel_SendsAllSevenCriteria()
    {
        using var t = new TestDb();
        var (session, question, _) = SeedSession(t, null);
        SeedStuckAnswer(t, session, question);

        var job = await RepublishAndCapture(t);

        Assert.Equal(7, job.Criteria.Count);
    }

    // ── (6) PracticeService — lưu nhãn + đóng dấu phiên bản ──────────────────────────
    private static PracticeService Practicing(TestDb t, Mock<IAiServiceQuestionGenerator> gen)
    {
        var reservation = new Mock<ICreditReservationClient>();
        reservation.Setup(r => r.ReserveAsync(
                It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CreditReservationResult(Guid.NewGuid(), 1));

        return new PracticeService(
            t.Db, new Mock<IStorageService>().Object, gen.Object,
            new Mock<ISessionScoringNotifier>().Object, reservation.Object,
            NullLogger<PracticeService>.Instance);
    }

    // Rubric của candidate (seed mặc định = candidate_id NULL) có 3 tiêu chí nội dung.
    private static List<RubricCriterion> SeedRubric(TestDb t)
    {
        var all = SevenCriteria(out var content);
        t.Db.RubricCriteria.AddRange(all);
        t.Db.SaveChanges();
        return content;
    }

    [Fact]
    public async Task Create_SendsOnlyContentCriteria_AndStoresLabelsPerQuestion()
    {
        using var t = new TestDb();
        var content = SeedRubric(t);
        var candidate = Guid.NewGuid();

        IReadOnlyList<QuestionTargetCriterionDto>? sent = null;
        var gen = new Mock<IAiServiceQuestionGenerator>();
        gen.Setup(g => g.GenerateQuestionsAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<IReadOnlyList<string>?>(), It.IsAny<int?>(),
                It.IsAny<IReadOnlyList<GroundingChunk>?>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<QuestionTargetCriterionDto>?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback((string _, string? _, string? _, IReadOnlyList<string>? _, int? _,
                       IReadOnlyList<GroundingChunk>? _, string _,
                       IReadOnlyList<QuestionTargetCriterionDto>? c, string _, CancellationToken _) => sent = c)
            .ReturnsAsync(new GeneratedQuestionsResult(
                [
                    new GeneratedQuestion { Content = "Câu nhắm thiết kế", TargetCriterionIds = [content[1].Id] },
                    new GeneratedQuestion { Content = "Giới thiệu bản thân", TargetCriterionIds = Array.Empty<Guid>() },
                    new GeneratedQuestion { Content = "Không nhãn" },
                ],
                Array.Empty<QuestionCitationDto>()));

        var resp = await Practicing(t, gen).CreateSessionAsync(
            candidate, new CreatePracticeSessionRequest(null, null, JobCategory.BE));

        // CHỈ tiêu chí NỘI DUNG được gửi đi — 4 tiêu chí cách nói không có việc gì ở đây.
        Assert.NotNull(sent);
        Assert.Equal(3, sent!.Count);
        Assert.All(sent, s => Assert.Contains(content, c => c.Id == s.CriterionId));

        // 3 trạng thái nhãn đi vào DB NGUYÊN VẸN (không quy [] về null).
        var stored = await t.Db.PracticeQuestions.AsNoTracking()
            .Where(q => q.SessionId == resp.Id).OrderBy(q => q.OrderNo).ToListAsync();
        Assert.Equal(3, stored.Count);
        Assert.Equal(new[] { content[1].Id }, stored[0].TargetCriterionIds);
        Assert.NotNull(stored[1].TargetCriterionIds);
        Assert.Empty(stored[1].TargetCriterionIds!);
        Assert.Null(stored[2].TargetCriterionIds);
    }

    // Con dấu: buổi CÓ nhãn → 2 ("chứng minh được khác thước đo").
    [Fact]
    public async Task Create_WithLabels_StampsScopeVersion2()
    {
        using var t = new TestDb();
        var content = SeedRubric(t);
        var gen = new Mock<IAiServiceQuestionGenerator>();
        gen.Setup(g => g.GenerateQuestionsAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<IReadOnlyList<string>?>(), It.IsAny<int?>(),
                It.IsAny<IReadOnlyList<GroundingChunk>?>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<QuestionTargetCriterionDto>?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GeneratedQuestionsResult(
                [new GeneratedQuestion { Content = "Q", TargetCriterionIds = [content[0].Id] }],
                Array.Empty<QuestionCitationDto>()));

        var resp = await Practicing(t, gen).CreateSessionAsync(
            Guid.NewGuid(), new CreatePracticeSessionRequest(null, null, JobCategory.BE));

        var s = await t.Db.PracticeSessions.AsNoTracking().FirstAsync(x => x.Id == resp.Id);
        Assert.Equal(2, s.ScoringScopeVersion);
    }

    // Buổi mà nhãn TOÀN LÀ `[]` (mọi câu đều là câu xã giao) vẫn phải đóng dấu 2: các câu đó chỉ được
    // chấm 4 tiêu chí cách nói ⇒ điểm buổi này KHÔNG cùng thước đo với buổi cũ. Đóng dấu 1 ở đây là
    // giấu mất đúng thứ con dấu sinh ra để nói.
    [Fact]
    public async Task Create_WithOnlyEmptyLabels_StillStampsScopeVersion2()
    {
        using var t = new TestDb();
        SeedRubric(t);
        var gen = new Mock<IAiServiceQuestionGenerator>();
        gen.Setup(g => g.GenerateQuestionsAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<IReadOnlyList<string>?>(), It.IsAny<int?>(),
                It.IsAny<IReadOnlyList<GroundingChunk>?>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<QuestionTargetCriterionDto>?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GeneratedQuestionsResult(
                [new GeneratedQuestion { Content = "Giới thiệu bản thân", TargetCriterionIds = Array.Empty<Guid>() }],
                Array.Empty<QuestionCitationDto>()));

        var resp = await Practicing(t, gen).CreateSessionAsync(
            Guid.NewGuid(), new CreatePracticeSessionRequest(null, null, JobCategory.BE));

        var s = await t.Db.PracticeSessions.AsNoTracking().FirstAsync(x => x.Id == resp.Id);
        Assert.Equal(2, s.ScoringScopeVersion);
    }

    // Buổi KHÔNG có nhãn nào (AIService không gắn được) → 1 = "đã biết: chấm toàn rubric".
    // KHÔNG đóng 2: nó sẽ báo động giả cho BC15/F14/CAMP-10 về một buổi chấm y hệt trước đây.
    [Fact]
    public async Task Create_WithoutLabels_StampsScopeVersion1()
    {
        using var t = new TestDb();
        SeedRubric(t);
        var gen = new Mock<IAiServiceQuestionGenerator>();
        gen.Setup(g => g.GenerateQuestionsAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<IReadOnlyList<string>?>(), It.IsAny<int?>(),
                It.IsAny<IReadOnlyList<GroundingChunk>?>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<QuestionTargetCriterionDto>?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GeneratedQuestionsResult(
                [new GeneratedQuestion { Content = "Q" }], Array.Empty<QuestionCitationDto>()));

        var resp = await Practicing(t, gen).CreateSessionAsync(
            Guid.NewGuid(), new CreatePracticeSessionRequest(null, null, JobCategory.BE));

        var s = await t.Db.PracticeSessions.AsNoTracking().FirstAsync(x => x.Id == resp.Id);
        Assert.Equal(1, s.ScoringScopeVersion);
    }

    // Rubric KHÔNG có tiêu chí nội dung nào (rubric riêng BC16 chưa phân loại) → không gửi `criteria`,
    // giữ NGUYÊN đường gọi cũ (overload 4 tham số) ⇒ hành vi B2C hiện có không đổi một chút nào.
    [Fact]
    public async Task Create_RubricWithoutContentCriteria_KeepsLegacyCallShape()
    {
        using var t = new TestDb();
        t.Db.RubricCriteria.Add(C("Giao tiếp & trình bày", ScoringScope.Always));
        await t.Db.SaveChangesAsync();

        var gen = new Mock<IAiServiceQuestionGenerator>(MockBehavior.Strict);
        gen.Setup(g => g.GenerateQuestionsAsync(
                "BE", It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new GeneratedQuestion { Content = "Q" }]);

        var resp = await Practicing(t, gen).CreateSessionAsync(
            Guid.NewGuid(), new CreatePracticeSessionRequest(null, null, JobCategory.BE));

        gen.VerifyAll();   // MockBehavior.Strict: gọi overload khác là ném ngay
        var s = await t.Db.PracticeSessions.AsNoTracking().FirstAsync(x => x.Id == resp.Id);
        Assert.Equal(1, s.ScoringScopeVersion);
    }
}
