using System.Runtime.CompilerServices;
using System.Text.Json;
using Isas.InterviewService.Controllers;
using Isas.InterviewService.DTOs;
using Isas.InterviewService.Entities;
using Isas.InterviewService.Enums;
using Isas.InterviewService.Models;
using Isas.InterviewService.Services;
using Isas.InterviewService.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Isas.InterviewService.Tests;

/// <summary>
/// SC2 · W4 (Interview NHẬN) + W5 (Interview PHÁT) — chấm theo phạm vi câu hỏi cho B2B.
///
/// <para>Trước SC2, tiêu chí campaign materialize KHÔNG set <c>ScoringScope</c> (⇒ Always) và câu B2B
/// KHÔNG có nhãn (⇒ null) ⇒ bộ lọc INT-18 — vốn đã áp cho B2B ở <c>AnswerService</c>/
/// <c>StuckAnswerRepublisher</c> — chưa bao giờ thu hẹp được gì. Đo prod: 78 tiêu chí ngoài seed đều
/// <c>Always</c>, 0 <c>WhenTargeted</c>. Từ SC2, Campaign gửi <c>criteria[].scoringScope</c> +
/// <c>questionDetails[].targetCriterionIds</c> (id campaign_criteria) và Interview map sang
/// <c>rubric_criteria.id</c> qua <c>source_criterion_id</c>.</para>
///
/// <para>Bất biến khoá ở đây: I2 (<c>null</c> ≠ <c>[]</c>) · I5 (request cũ ⇒ hành vi y hệt hôm nay,
/// stamp 1) · I8 (DTO ứng viên KHÔNG mang nhãn) · hợp đồng tên khoá JSON (fixture
/// <c>Contracts/sc2-b2b-scoping.contract.md</c>).</para>
/// </summary>
public class CampaignScoringScopeSc2Tests
{
    // ── helpers ───────────────────────────────────────────────────────────────────────

    private static PracticeService Practicing(TestDb t)
    {
        var reservation = new Mock<ICreditReservationClient>();
        reservation.Setup(r => r.ReserveAsync(
                It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CreditReservationResult(Guid.NewGuid(), 1));
        return new PracticeService(
            t.Db, new Mock<IStorageService>().Object, new Mock<IAiServiceQuestionGenerator>().Object,
            new Mock<ISessionScoringNotifier>().Object, reservation.Object,
            NullLogger<PracticeService>.Instance);
    }

    /// <summary>2 tiêu chí campaign có id: A (nội dung, WhenTargeted) và B (cách nói, Always).</summary>
    private static (Guid a, Guid b, IReadOnlyList<CampaignCriterionInput> criteria) TwoCriteria(
        string? scopeA = "WhenTargeted", string? scopeB = "Always")
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var criteria = new[]
        {
            new CampaignCriterionInput("Chiều sâu kỹ thuật", null, 0.6m, 5, CriterionId: a, ScoringScope: scopeA),
            new CampaignCriterionInput("Giao tiếp", null, 0.4m, 5, CriterionId: b, ScoringScope: scopeB),
        };
        return (a, b, criteria);
    }

    private static CreateCampaignSessionRequest Request(
        Guid campaignId,
        IReadOnlyList<CampaignCriterionInput> criteria,
        params CampaignQuestionInput[] questions)
        => new(
            campaignId, Guid.NewGuid(), JobCategory.BE,
            Questions: questions.Select(q => q.Text).ToList(),
            Criteria: criteria,
            QuestionDetails: questions);

    private static async Task<(PracticeSession session, List<PracticeQuestion> questions, List<RubricCriterion> rubric)>
        Stored(TestDb t, Guid sessionId, Guid campaignId, int version = 1)
    {
        await using var read = t.NewContext();
        var session = await read.PracticeSessions.AsNoTracking().SingleAsync(s => s.Id == sessionId);
        var questions = await read.PracticeQuestions.AsNoTracking()
            .Where(q => q.SessionId == sessionId).OrderBy(q => q.OrderNo).ToListAsync();
        var rubric = await read.RubricCriteria.AsNoTracking()
            .Where(c => c.CampaignId == campaignId && c.Version == version).ToListAsync();
        return (session, questions, rubric);
    }

    private static Guid RubricIdOf(List<RubricCriterion> rubric, Guid sourceId)
        => rubric.Single(c => c.SourceCriterionId == sourceId).Id;

    // ── I5 — request CŨ (bản Campaign chưa gửi field) ⇒ hành vi y hệt hôm nay ─────────

    [Fact]
    public async Task OldRequest_NoScopeNoLabels_AllAlways_AllNull_Stamp1()
    {
        using var t = new TestDb();
        var campaignId = Guid.NewGuid();
        var req = new CreateCampaignSessionRequest(
            campaignId, Guid.NewGuid(), JobCategory.BE,
            Questions: new[] { "Q1", "Q2" },
            Criteria: new[]
            {
                new CampaignCriterionInput("Chiều sâu kỹ thuật", null, 0.6m, 5),
                new CampaignCriterionInput("Giao tiếp", null, 0.4m, 5),
            },
            // Bản Campaign cũ gửi QuestionDetails chỉ có đáp án mẫu, KHÔNG có nhãn.
            QuestionDetails: new[] { new CampaignQuestionInput("Q1", "đáp án"), new CampaignQuestionInput("Q2") });

        var res = await Practicing(t).CreateCampaignSessionAsync(Guid.NewGuid(), req);
        var (session, questions, rubric) = await Stored(t, res.Id, campaignId);

        Assert.All(rubric, c => Assert.Equal(ScoringScope.Always, c.ScoringScope));
        Assert.All(questions, q => Assert.Null(q.TargetCriterionIds));
        Assert.Equal(1, session.ScoringScopeVersion);
    }

    // Không gửi QuestionDetails luôn (bản cũ hơn nữa) ⇒ cũng null + stamp 1.
    [Fact]
    public async Task OldRequest_NoQuestionDetails_AllNull_Stamp1()
    {
        using var t = new TestDb();
        var campaignId = Guid.NewGuid();
        var (_, _, criteria) = TwoCriteria();
        var req = new CreateCampaignSessionRequest(
            campaignId, Guid.NewGuid(), JobCategory.BE, Questions: new[] { "Q1" }, Criteria: criteria);

        var res = await Practicing(t).CreateCampaignSessionAsync(Guid.NewGuid(), req);
        var (session, questions, _) = await Stored(t, res.Id, campaignId);

        Assert.Null(questions.Single().TargetCriterionIds);
        Assert.Equal(1, session.ScoringScopeVersion);
    }

    // ── scoringScope parse ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData("WhenTargeted", ScoringScope.WhenTargeted)]
    [InlineData("whentargeted", ScoringScope.WhenTargeted)]   // case-insensitive
    [InlineData(" WHENTARGETED ", ScoringScope.WhenTargeted)]  // trim
    [InlineData("Always", ScoringScope.Always)]
    [InlineData("always", ScoringScope.Always)]
    [InlineData(null, ScoringScope.Always)]                    // vắng ⇒ Always
    [InlineData("", ScoringScope.Always)]
    [InlineData("Sometimes", ScoringScope.Always)]             // lạ ⇒ Always (chấm thừa, không bỏ chấm)
    [InlineData("1", ScoringScope.Always)]                     // số ⇒ KHÔNG được hiểu là enum 1
    [InlineData("7", ScoringScope.Always)]
    public async Task ScoringScope_ParsedFromString_UnknownFallsBackToAlways(string? raw, ScoringScope expected)
    {
        using var t = new TestDb();
        var campaignId = Guid.NewGuid();
        var req = new CreateCampaignSessionRequest(
            campaignId, Guid.NewGuid(), JobCategory.BE, Questions: new[] { "Q1" },
            Criteria: new[] { new CampaignCriterionInput("X", null, 1.0m, 5, ScoringScope: raw) });

        await Practicing(t).CreateCampaignSessionAsync(Guid.NewGuid(), req);

        var stored = await t.NewContext().RubricCriteria.AsNoTracking().SingleAsync(c => c.CampaignId == campaignId);
        Assert.Equal(expected, stored.ScoringScope);
        // Giá trị lưu phải nằm TRONG enum — "7" mà lọt qua Enum.TryParse sẽ ghi (ScoringScope)7 xuống DB.
        Assert.True(Enum.IsDefined(stored.ScoringScope));
    }

    // ── map nhãn campaign_criteria.id → rubric_criteria.id ──────────────────────────

    [Fact]
    public async Task Labels_MappedThroughSourceCriterionId_NotByIndex()
    {
        using var t = new TestDb();
        var campaignId = Guid.NewGuid();
        var (a, b, criteria) = TwoCriteria();
        // Q1 nhắm B (tiêu chí THỨ HAI trong mảng), Q2 nhắm A — nếu map theo INDEX thay vì id thì hai
        // câu đổi chỗ nhãn cho nhau mà không lỗi nào nổ.
        var req = Request(campaignId, criteria,
            new CampaignQuestionInput("Q1", TargetCriterionIds: [b]),
            new CampaignQuestionInput("Q2", TargetCriterionIds: [a]));

        var res = await Practicing(t).CreateCampaignSessionAsync(Guid.NewGuid(), req);
        var (session, questions, rubric) = await Stored(t, res.Id, campaignId);

        Assert.Equal(2, rubric.Count);
        Assert.Equal([RubricIdOf(rubric, b)], questions[0].TargetCriterionIds);
        Assert.Equal([RubricIdOf(rubric, a)], questions[1].TargetCriterionIds);
        // Nhãn KHÔNG được là id campaign (id lỏng phía Campaign vô nghĩa với ScoringScopeFilter).
        Assert.DoesNotContain(a, questions[1].TargetCriterionIds!);
        Assert.DoesNotContain(b, questions[0].TargetCriterionIds!);
        Assert.Equal(2, session.ScoringScopeVersion);
    }

    [Fact]
    public async Task Labels_UnknownIdDropped_KnownIdsKept()
    {
        using var t = new TestDb();
        var campaignId = Guid.NewGuid();
        var (a, _, criteria) = TwoCriteria();
        var ghost = Guid.NewGuid();
        var req = Request(campaignId, criteria,
            new CampaignQuestionInput("Q1", TargetCriterionIds: [ghost, a]));

        var res = await Practicing(t).CreateCampaignSessionAsync(Guid.NewGuid(), req);
        var (_, questions, rubric) = await Stored(t, res.Id, campaignId);

        Assert.Equal([RubricIdOf(rubric, a)], questions[0].TargetCriterionIds);
    }

    // Có nhãn nhưng KHÔNG id nào map được ⇒ null (chấm đủ), KHÔNG phải [] — "[]" sẽ bỏ chấm mọi tiêu
    // chí nội dung của câu dựa trên một nhãn không đối chiếu được. Cùng luật với ParseTargets (AI wire).
    [Fact]
    public async Task Labels_AllUnknown_YieldsNull_NotEmpty()
    {
        using var t = new TestDb();
        var campaignId = Guid.NewGuid();
        var (_, _, criteria) = TwoCriteria();
        var req = Request(campaignId, criteria,
            new CampaignQuestionInput("Q1", TargetCriterionIds: [Guid.NewGuid()]),
            new CampaignQuestionInput("Q2"));

        var res = await Practicing(t).CreateCampaignSessionAsync(Guid.NewGuid(), req);
        var (session, questions, _) = await Stored(t, res.Id, campaignId);

        Assert.Null(questions[0].TargetCriterionIds);
        Assert.Null(questions[1].TargetCriterionIds);
        Assert.Equal(1, session.ScoringScopeVersion);   // không câu nào thu hẹp được ⇒ vẫn thước cũ
    }

    // I2 — `[]` là lời khẳng định "câu này không nhắm tiêu chí nội dung nào" ⇒ GIỮ [], stamp 2.
    [Fact]
    public async Task Labels_EmptyStaysEmpty_AndStamps2()
    {
        using var t = new TestDb();
        var campaignId = Guid.NewGuid();
        var (_, _, criteria) = TwoCriteria();
        var req = Request(campaignId, criteria,
            new CampaignQuestionInput("Giới thiệu bản thân", TargetCriterionIds: []),
            new CampaignQuestionInput("Q2"));   // null

        var res = await Practicing(t).CreateCampaignSessionAsync(Guid.NewGuid(), req);
        var (session, questions, _) = await Stored(t, res.Id, campaignId);

        Assert.NotNull(questions[0].TargetCriterionIds);
        Assert.Empty(questions[0].TargetCriterionIds!);
        Assert.Null(questions[1].TargetCriterionIds);
        Assert.Equal(2, session.ScoringScopeVersion);
    }

    [Fact]
    public async Task Labels_DuplicateIds_Deduplicated()
    {
        using var t = new TestDb();
        var campaignId = Guid.NewGuid();
        var (a, _, criteria) = TwoCriteria();
        var req = Request(campaignId, criteria,
            new CampaignQuestionInput("Q1", TargetCriterionIds: [a, a]));

        var res = await Practicing(t).CreateCampaignSessionAsync(Guid.NewGuid(), req);
        var (_, questions, rubric) = await Stored(t, res.Id, campaignId);

        Assert.Equal([RubricIdOf(rubric, a)], questions[0].TargetCriterionIds);
    }

    // ── đường idempotent: bộ đã materialize ở buổi trước, buổi 2 vẫn phải map đúng ───

    [Fact]
    public async Task SecondSession_SameVersion_MapsThroughExistingRubric()
    {
        using var t = new TestDb();
        var campaignId = Guid.NewGuid();
        var (a, b, criteria) = TwoCriteria();
        var svc = Practicing(t);

        var first = await svc.CreateCampaignSessionAsync(Guid.NewGuid(),
            Request(campaignId, criteria, new CampaignQuestionInput("Q1", TargetCriterionIds: [a])));
        var second = await svc.CreateCampaignSessionAsync(Guid.NewGuid(),
            Request(campaignId, criteria, new CampaignQuestionInput("Q1", TargetCriterionIds: [b])));

        var (_, q1, rubric1) = await Stored(t, first.Id, campaignId);
        var (s2, q2, rubric2) = await Stored(t, second.Id, campaignId);

        Assert.Equal(2, rubric2.Count);                                   // không nhân đôi bộ
        Assert.Equal(rubric1.Select(c => c.Id).Order(), rubric2.Select(c => c.Id).Order());
        Assert.Equal([RubricIdOf(rubric1, a)], q1[0].TargetCriterionIds);
        Assert.Equal([RubricIdOf(rubric1, b)], q2[0].TargetCriterionIds); // map vào bộ ĐÃ CÓ
        Assert.Equal(2, s2.ScoringScopeVersion);
    }

    // Bộ đã materialize bởi bản Campaign CŨ (không source_criterion_id) ⇒ không map được ⇒ null.
    [Fact]
    public async Task SecondSession_ExistingRubricWithoutSourceIds_LabelsFallBackToNull()
    {
        using var t = new TestDb();
        var campaignId = Guid.NewGuid();
        var svc = Practicing(t);

        var oldCriteria = new[] { new CampaignCriterionInput("Chiều sâu kỹ thuật", null, 1.0m, 5) }; // no CriterionId
        await svc.CreateCampaignSessionAsync(Guid.NewGuid(),
            new CreateCampaignSessionRequest(campaignId, Guid.NewGuid(), JobCategory.BE,
                Questions: new[] { "Q1" }, Criteria: oldCriteria));

        var (a, _, newCriteria) = TwoCriteria();
        var second = await svc.CreateCampaignSessionAsync(Guid.NewGuid(),
            Request(campaignId, newCriteria, new CampaignQuestionInput("Q1", TargetCriterionIds: [a])));

        var (s2, q2, rubric) = await Stored(t, second.Id, campaignId);
        Assert.Single(rubric);                       // idempotent: KHÔNG materialize lại bộ mới
        Assert.Null(q2[0].TargetCriterionIds);
        Assert.Equal(1, s2.ScoringScopeVersion);
    }

    // Phiên bản MỚI (rubricVersion bump) ⇒ materialize bộ mới, nhãn map vào bộ mới chứ không bộ cũ.
    [Fact]
    public async Task NewRubricVersion_LabelsMapIntoNewSet()
    {
        using var t = new TestDb();
        var campaignId = Guid.NewGuid();
        var (a, _, criteria) = TwoCriteria();
        var svc = Practicing(t);

        await svc.CreateCampaignSessionAsync(Guid.NewGuid(),
            Request(campaignId, criteria, new CampaignQuestionInput("Q1", TargetCriterionIds: [a])));

        var v2 = Request(campaignId, criteria, new CampaignQuestionInput("Q1", TargetCriterionIds: [a]))
            with { RubricVersion = 2 };
        var second = await svc.CreateCampaignSessionAsync(Guid.NewGuid(), v2);

        var (_, q2, rubricV2) = await Stored(t, second.Id, campaignId, version: 2);
        var rubricV1 = await t.NewContext().RubricCriteria.AsNoTracking()
            .Where(c => c.CampaignId == campaignId && c.Version == 1).ToListAsync();

        Assert.Equal([RubricIdOf(rubricV2, a)], q2[0].TargetCriterionIds);
        Assert.DoesNotContain(RubricIdOf(rubricV1, a), q2[0].TargetCriterionIds!);
    }

    // ── controller: SanitizeCriterionLevels (`with { Levels = null }`) không được làm rụng scope ──

    [Fact]
    public async Task Controller_ForwardsScopeAndLabels_EvenWhenLevelsSanitized()
    {
        var a = Guid.NewGuid();
        CreateCampaignSessionRequest? forwarded = null;
        var svc = new Mock<IPracticeService>();
        svc.Setup(s => s.GetOrCreateCampaignSessionAsync(
                It.IsAny<Guid>(), It.IsAny<CreateCampaignSessionRequest>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, CreateCampaignSessionRequest, CancellationToken>((_, r, _) => forwarded = r)
            .ReturnsAsync(new PracticeSessionResponse(
                Guid.NewGuid(), "Ready", "BE", "vi", null, null, DateTime.UtcNow, null, []));
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Internal:Token"] = "tok" }).Build();
        var controller = new InternalSessionsController(svc.Object, config, NullLogger<InternalSessionsController>.Instance);

        var req = new CreateCampaignSessionInternalRequest(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "BE",
            Questions: new[] { "Q1" },
            Criteria: new[]
            {
                // Thang MÉO (score > maxScore) ⇒ Sanitize bỏ Levels bằng `with` — scope phải sống sót.
                new CampaignCriterionInput("X", null, 1.0m, 5,
                    Levels: [new CampaignCriterionLevelInput(9, "vượt thang")],
                    CriterionId: a, ScoringScope: "WhenTargeted")
            },
            QuestionDetails: new[] { new CampaignQuestionInput("Q1", TargetCriterionIds: [a]) });

        await controller.CreateOrGetCampaignSession(req, "tok", default);

        Assert.NotNull(forwarded);
        Assert.Null(forwarded!.Criteria[0].Levels);
        Assert.Equal("WhenTargeted", forwarded.Criteria[0].ScoringScope);
        Assert.Equal([a], forwarded.QuestionDetails![0].TargetCriterionIds);
    }

    // ── W4 wire: khoá JSON camelCase qua JsonSerializerDefaults.Web ─────────────────

    private const string BaseJson =
        """
        "candidateId":"11111111-1111-1111-1111-111111111111","campaignId":"22222222-2222-2222-2222-222222222222",
        "orgId":"33333333-3333-3333-3333-333333333333","jobCategory":"BE","questions":["Q1","Q2"]
        """;

    [Fact]
    public void Wire_DeserializesScopeAndLabels_CamelCase_ThreeStates()
    {
        var a = Guid.NewGuid();
        var opts = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var json = $$"""
            {{{BaseJson}},
             "criteria":[{"name":"X","weight":1,"maxScore":5,"criterionId":"{{a}}","scoringScope":"WhenTargeted"}],
             "questionDetails":[{"text":"Q1","targetCriterionIds":["{{a}}"]},{"text":"Q2","targetCriterionIds":[]}]}
            """;

        var req = JsonSerializer.Deserialize<CreateCampaignSessionInternalRequest>(json, opts)!;

        Assert.Equal("WhenTargeted", req.Criteria[0].ScoringScope);
        Assert.Equal([a], req.QuestionDetails![0].TargetCriterionIds);
        Assert.NotNull(req.QuestionDetails[1].TargetCriterionIds);
        Assert.Empty(req.QuestionDetails[1].TargetCriterionIds!);   // [] giữ [], không thành null
    }

    // Bản Campaign cũ không gửi hai khoá ⇒ null (không phải chuỗi rỗng / mảng rỗng).
    [Fact]
    public void Wire_MissingKeys_AreNull()
    {
        var opts = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var json = $$"""
            {{{BaseJson}},
             "criteria":[{"name":"X","weight":1,"maxScore":5}],
             "questionDetails":[{"text":"Q1","sampleAnswer":"a"},{"text":"Q2"}]}
            """;

        var req = JsonSerializer.Deserialize<CreateCampaignSessionInternalRequest>(json, opts)!;

        Assert.Null(req.Criteria[0].ScoringScope);
        Assert.All(req.QuestionDetails!, q => Assert.Null(q.TargetCriterionIds));
    }

    // ── I8 — DTO ứng viên KHÔNG mang nhãn (lộ ra = ứng viên viết bài đánh trúng phạm vi chấm) ──

    [Fact]
    public void CandidateQuestionResponse_DoesNotExposeTargetCriterionIds()
    {
        var names = typeof(QuestionResponse).GetProperties().Select(p => p.Name).ToList();
        Assert.DoesNotContain(names, n => n.Contains("Target", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, n => n.Contains("Scope", StringComparison.OrdinalIgnoreCase));
    }

    // ── W5 — bộ chuẩn B2C trả scoringScope ─────────────────────────────────────────

    [Fact]
    public async Task B2CRubricEndpoint_ReturnsScoringScope_AsEnumString()
    {
        using var t = new TestDb();
        t.Db.RubricCriteria.AddRange(Data.B2CRubricSeed.Build());
        await t.Db.SaveChangesAsync();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Internal:Token"] = "tok" }).Build();
        var controller = new InternalRubricsController(t.Db, config, NullLogger<InternalRubricsController>.Instance);

        var ok = Assert.IsType<OkObjectResult>(await controller.GetB2CDefaultAsync("tok", JobCategory.BE, "vi", default));
        var criteria = ((System.Collections.IEnumerable)ok.Value!.GetType().GetProperty("criteria")!.GetValue(ok.Value)!)
            .Cast<object>().ToList();

        var scopes = criteria.Select(c => (string)c.GetType().GetProperty("scoringScope")!.GetValue(c)!).ToList();
        Assert.Equal(7, scopes.Count);
        Assert.Equal(4, scopes.Count(s => s == "Always"));         // 4 cách nói
        Assert.Equal(3, scopes.Count(s => s == "WhenTargeted"));   // 3 nội dung — chuỗi enum, KHÔNG số
        Assert.All(scopes, s => Assert.False(int.TryParse(s, out _)));
    }

    // ── Hợp đồng tên khoá: fixture chép nguyên văn W4/W5 của contracts.md ─────────────
    // Đọc file bằng [CallerFilePath] (worktree-safe, tiền lệ MigrationScaffoldingGuardTests) chứ
    // KHÔNG đọc scratchpad — file đó không tồn tại trên CI. Khoá được dẫn xuất từ CHÍNH tên property
    // của DTO qua camelCase policy ⇒ đổi tên property phía Interview là test đỏ, không phải chuỗi tay.

    private static string ContractText([CallerFilePath] string here = "")
        => File.ReadAllText(Path.Combine(Path.GetDirectoryName(here)!, "Contracts", "sc2-b2b-scoping.contract.md"));

    private static string WireKey(string propertyName) => JsonNamingPolicy.CamelCase.ConvertName(propertyName);

    [Fact]
    public void Contract_W4_CriteriaScoringScopeKey_MatchesDto()
    {
        var text = ContractText();
        var w4 = text[text.IndexOf("## W4", StringComparison.Ordinal)..text.IndexOf("## W5", StringComparison.Ordinal)];
        Assert.Contains($"criteria[].{WireKey(nameof(CampaignCriterionInput.ScoringScope))}", w4);
        Assert.Contains($"questionDetails[].{WireKey(nameof(CampaignQuestionInput.TargetCriterionIds))}", w4);
        // Tên mảng cha cũng phải khớp DTO gốc (đổi `QuestionDetails` là cả nhánh rụng im lặng).
        Assert.Contains($"{WireKey(nameof(CreateCampaignSessionInternalRequest.QuestionDetails))}[]", w4);
        Assert.Contains($"{WireKey(nameof(CreateCampaignSessionInternalRequest.Criteria))}[]", w4);
    }

    [Fact]
    public void Contract_W5_B2CRubricScoringScopeKey_MatchesEndpoint()
    {
        var text = ContractText();
        var w5 = text[text.IndexOf("## W5", StringComparison.Ordinal)..];
        Assert.Contains("scoringScope", w5);
        // Hai giá trị hợp đồng cho phép đúng là hai tên enum của Interview — đổi enum là đổi hợp đồng.
        foreach (var name in Enum.GetNames<ScoringScope>())
            Assert.Contains($"\"{name}\"", w5);
    }
}
