using System.Security.Claims;
using Isas.InterviewService.Controllers;
using Isas.InterviewService.DTOs;
using Isas.InterviewService.Entities;
using Isas.InterviewService.Enums;
using Isas.InterviewService.Models;
using Isas.InterviewService.Services;
using Isas.InterviewService.Services.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace Isas.InterviewService.Tests;

// BC7 — POST/GET cv-analysis (mock AIService + storage). Test qua controller để chốt status code.
public class CvAnalysisTests
{
    private static FileRecord OwnedFile(Guid fileId, Guid ownerId, string type, string? parsed)
        => new()
        {
            Id = fileId,
            UserId = ownerId,
            FileType = type,
            OriginalName = $"{type}.pdf",
            StoragePath = $"{type}/{fileId}.pdf",
            StorageBucket = "isas-files",
            MimeType = "application/pdf",
            FileSize = 1024,
            ParsedText = parsed,
            ParseStatus = "done",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

    private static CvAnalysisAiResult SampleAi(bool withJdMatch)
        => new(
            Summary: "Ứng viên backend 3 năm C#/SQL.",
            Strengths: ["C#", "Kiến trúc microservice"],
            Weaknesses: ["Ít kinh nghiệm frontend"],
            Suggestions: ["Học thêm React"],
            JdMatch: withJdMatch ? new CvJdMatch(78, ["C#", "SQL"], ["Kubernetes"]) : null,
            CurrentLevel: "Middle");

    private static CvAnalysisAiResult SampleAiWithRequirements()
        => new(
            Summary: "Ứng viên backend 3 năm C#/SQL.",
            Strengths: ["C#"],
            Weaknesses: [],
            Suggestions: ["Bổ sung Kubernetes"],
            JdMatch: null,
            RequirementMatches:
            [
                new CvRequirementMatch("r1", "MustHave", ".NET", "Strong", "Skills: .NET"),
                new CvRequirementMatch("r2", "NiceToHave", "Kubernetes", "Weak", "Không thấy bằng chứng")
            ],
            CvSections: [new CvSectionAnchor("Skills", "skills", "Skills")],
            Citations: [new CvAnalysisCitation("chunk-1", "ASP.NET documentation", null, "Microsoft")]);

    private static CvAnalysisController Controller(
        TestDb t, IStorageService storage, IAiServiceCvAnalyzer ai, Guid userId,
        ICreditReservationClient? credits = null, int cvAnalysisCredits = 1,
        IKnowledgeService? knowledge = null, bool groundingEnabled = false,
        int maxGroundingChunks = 8)
    {
        // BC7b — config Billing:CvAnalysisCredits (mặc định 1 = tính phí); credits mock mặc định = reserve OK.
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Billing:CvAnalysisCredits"] = cvAnalysisCredits.ToString(),
            ["JdRequirements:MaxGroundingChunks"] = maxGroundingChunks.ToString()
        }).Build();
        var service = new CvAnalysisService(
            t.Db, storage, ai, credits ?? CreditsMock().Object, config,
            NullLogger<CvAnalysisService>.Instance, knowledge: knowledge,
            groundingOptions: Options.Create(new GroundingOptions { Enabled = groundingEnabled }));
        var controller = new CvAnalysisController(service, NullLogger<CvAnalysisController>.Instance);
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, userId.ToString())], "test"));
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = principal }
        };
        return controller;
    }

    private static Mock<IAiServiceCvAnalyzer> AiMock(CvAnalysisAiResult result)
    {
        var m = new Mock<IAiServiceCvAnalyzer>();
        m.Setup(x => x.AnalyzeAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(result);
        return m;
    }

    // BC7b — reservation client mặc định: reserve trả OK, consume/release no-op (Task.CompletedTask mặc định).
    private static Mock<ICreditReservationClient> CreditsMock()
    {
        var m = new Mock<ICreditReservationClient>();
        m.Setup(x => x.ReserveAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CreditReservationResult(Guid.NewGuid(), 1));
        return m;
    }

    // ── (a) POST → 201 + 1 row cv_analyses (không JD) ─────────────────────────────
    [Fact]
    public async Task Post_WithoutJd_Returns201_AndPersistsRow()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var cvId = Guid.NewGuid();

        var storage = new Mock<IStorageService>();
        storage.Setup(s => s.GetMetadata(cvId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(OwnedFile(cvId, user, "cv", "Nội dung CV..."));

        var ctrl = Controller(t, storage.Object, AiMock(SampleAi(withJdMatch: false)).Object, user);

        var result = await ctrl.Analyze(new CvAnalysisRequest(cvId, null, JobCategory.BE), default);

        var created = Assert.IsType<CreatedResult>(result);
        Assert.Equal(StatusCodes.Status201Created, created.StatusCode);
        var body = Assert.IsType<CvAnalysisResponse>(created.Value);
        Assert.Equal(cvId, body.CvId);
        Assert.Null(body.JdId);
        Assert.Equal("BE", body.JobCategory);
        Assert.Null(body.JdMatch);                       // không JD → không jdMatch
        Assert.Contains("C#", body.Strengths);

        var row = await t.Db.CvAnalyses.AsNoTracking().SingleAsync();
        Assert.Equal(user, row.CandidateId);
        Assert.Equal(cvId, row.CvId);
        Assert.Null(row.JdId);
        Assert.Equal(JobCategory.BE, row.JobCategory);
        Assert.Equal(2, row.Strengths.Count);            // jsonb round-trip
        Assert.Single(row.Weaknesses);
        Assert.Null(row.JdMatch);
        // 🔴 `CvAnalysisService` dựng entity bằng object initializer gán TAY từng trường. Quên một
        // dòng ở đó là bug IM LẶNG — AI trả đúng, deserialize đúng, DB có cột, mà giá trị luôn null;
        // không exception, không log. Đây là thứ duy nhất bắt được.
        Assert.Equal("Middle", row.CurrentLevel);
    }

    // POST có JD → jdMatch được lưu + trả về.
    [Fact]
    public async Task Post_WithJd_PersistsJdMatch()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var cvId = Guid.NewGuid();
        var jdId = Guid.NewGuid();

        var storage = new Mock<IStorageService>();
        storage.Setup(s => s.GetMetadata(cvId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(OwnedFile(cvId, user, "cv", "CV..."));
        storage.Setup(s => s.GetMetadata(jdId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(OwnedFile(jdId, user, "jd", "JD..."));

        var ctrl = Controller(t, storage.Object, AiMock(SampleAi(withJdMatch: true)).Object, user);

        var result = await ctrl.Analyze(new CvAnalysisRequest(cvId, jdId, JobCategory.BE), default);

        var created = Assert.IsType<CreatedResult>(result);
        var body = Assert.IsType<CvAnalysisResponse>(created.Value);
        Assert.Equal(jdId, body.JdId);
        Assert.NotNull(body.JdMatch);
        Assert.Equal(78, body.JdMatch!.Score);
        Assert.Contains("Kubernetes", body.JdMatch.MissingSkills);

        var row = await t.Db.CvAnalyses.AsNoTracking().SingleAsync();
        Assert.NotNull(row.JdMatch);
        Assert.Equal(78, row.JdMatch!.Score);            // jsonb value-object round-trip
    }

    [Fact]
    public async Task Post_WithRequirementData_PersistsAndMapsRequirementHistory()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var cvId = Guid.NewGuid();

        var storage = new Mock<IStorageService>();
        storage.Setup(s => s.GetMetadata(cvId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(OwnedFile(cvId, user, "cv", "Skills: .NET"));

        var ai = new Mock<IAiServiceCvAnalyzer>();
        ai.Setup(x => x.AnalyzeAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>(),
                It.IsAny<IReadOnlyList<CvRequirementInput>?>(),
                It.IsAny<IReadOnlyList<CvRequirementInput>?>(),
                It.IsAny<IReadOnlyList<GroundingChunk>?>()))
            .ReturnsAsync((string _, string _, string? _, CancellationToken _,
                IReadOnlyList<CvRequirementInput>? must,
                IReadOnlyList<CvRequirementInput>? nice,
                IReadOnlyList<GroundingChunk>? _) => new CvAnalysisAiResult(
                "Ứng viên backend 3 năm C#/SQL.", ["C#"], [], ["Bổ sung Kubernetes"], null,
                (must ?? []).Select(x => new CvRequirementMatch(
                    x.RequirementId, "MustHave", x.Text, "Strong", "Skills: .NET"))
                .Concat((nice ?? []).Select(x => new CvRequirementMatch(
                    x.RequirementId, "NiceToHave", x.Text, "Weak", "Không thấy bằng chứng")))
                .ToList(),
                [new CvSectionAnchor("Skills", "skills", "Skills")],
                [new CvAnalysisCitation("chunk-1", "ASP.NET documentation", null, "Microsoft")]));
        var ctrl = Controller(t, storage.Object, ai.Object, user);
        var result = await ctrl.Analyze(new CvAnalysisRequest(
            cvId, null, JobCategory.BE, null,
            [new CvRequirementInput(".NET")],
            [new CvRequirementInput("Kubernetes")]), default);

        var body = Assert.IsType<CvAnalysisResponse>(((CreatedResult)result).Value);
        Assert.Single(body.MustHaveMatches!);
        Assert.NotEqual("client-1", body.MustHaveMatches![0].RequirementId);
        Assert.False(string.IsNullOrWhiteSpace(body.MustHaveMatches[0].RequirementId));
        Assert.Single(body.NiceToHaveMatches!);
        Assert.Equal(1, body.RequirementSummary!.MustHave.Strong);
        Assert.Equal(1, body.RequirementSummary.NiceToHave.Weak);
        Assert.Equal("Skills", body.CvSections![0].Title);
        Assert.Equal(1, body.MustHaveMatches[0].Page);
        Assert.Equal("Skills", body.MustHaveMatches[0].SectionTitle);
        Assert.Equal("chunk-1", body.Citations![0].ChunkId);

        var row = await t.Db.CvAnalyses.AsNoTracking().SingleAsync();
        Assert.Equal(2, row.RequirementMatches!.Count); // jsonb value-object round-trip
        Assert.Equal("Skills: .NET", row.RequirementMatches[0].Evidence);
        Assert.Single(row.CvSections!);
        Assert.Single(row.Citations!);
    }

    [Fact]
    public async Task Post_RequirementGrounding_IsRoundRobinDeduplicatedAndGloballyCapped()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var cvId = Guid.NewGuid();
        var storage = new Mock<IStorageService>();
        storage.Setup(s => s.GetMetadata(cvId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(OwnedFile(cvId, user, "cv", "Skills: Python Docker PostgreSQL"));

        static GroundingChunk Chunk(string id) => new(id, $"content-{id}", $"url-{id}", $"title-{id}");
        var knowledge = new Mock<IKnowledgeService>();
        knowledge.Setup(x => x.RetrieveBatchAsync(
                "BE", It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                [Chunk("a1"), Chunk("a2"), Chunk("a3")],
                [Chunk("b1"), Chunk("b2")],
                [Chunk("c1"), Chunk("a1")]
            ]);

        IReadOnlyList<GroundingChunk>? sentGrounding = null;
        var ai = new Mock<IAiServiceCvAnalyzer>();
        ai.Setup(x => x.AnalyzeAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>(),
                It.IsAny<IReadOnlyList<CvRequirementInput>?>(),
                It.IsAny<IReadOnlyList<CvRequirementInput>?>(),
                It.IsAny<IReadOnlyList<GroundingChunk>?>()))
            .ReturnsAsync((string _, string _, string? _, CancellationToken _,
                IReadOnlyList<CvRequirementInput>? must,
                IReadOnlyList<CvRequirementInput>? nice,
                IReadOnlyList<GroundingChunk>? grounding) =>
            {
                sentGrounding = grounding;
                return new CvAnalysisAiResult(
                    "s", ["Python"], [], ["Giữ kết quả đo được"], null,
                    (must ?? []).Select(x => new CvRequirementMatch(
                        x.RequirementId!, "MustHave", x.Text, "Strong", "Python"))
                    .Concat((nice ?? []).Select(x => new CvRequirementMatch(
                        x.RequirementId!, "NiceToHave", x.Text, "Weak", "Không thấy bằng chứng")))
                    .ToList(), [], []);
            });

        var ctrl = Controller(
            t, storage.Object, ai.Object, user, cvAnalysisCredits: 0,
            knowledge: knowledge.Object, groundingEnabled: true, maxGroundingChunks: 4);
        var result = await ctrl.Analyze(new CvAnalysisRequest(
            cvId, null, JobCategory.BE, null,
            [new CvRequirementInput("Python", "client-a"), new CvRequirementInput("Docker", "client-b")],
            [new CvRequirementInput("PostgreSQL", "client-c")]), default);

        Assert.IsType<CreatedResult>(result);
        Assert.Equal(["a1", "b1", "c1", "a2"], sentGrounding!.Select(x => x.ChunkId));
    }

    [Fact]
    public async Task List_SummarizesRequirementEvidence_WhileDetailKeepsLocation()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var cvId = Guid.NewGuid();
        var storage = new Mock<IStorageService>();
        storage.Setup(s => s.GetMetadata(cvId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(OwnedFile(cvId, user, "cv", "Skills: .NET"));
        var ai = new Mock<IAiServiceCvAnalyzer>();
        ai.Setup(x => x.AnalyzeAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>(),
                It.IsAny<IReadOnlyList<CvRequirementInput>?>(),
                It.IsAny<IReadOnlyList<CvRequirementInput>?>(),
                It.IsAny<IReadOnlyList<GroundingChunk>?>()))
            .ReturnsAsync((string _, string _, string? _, CancellationToken _,
                IReadOnlyList<CvRequirementInput>? must,
                IReadOnlyList<CvRequirementInput>? _,
                IReadOnlyList<GroundingChunk>? _) => new CvAnalysisAiResult(
                "s", [], [], [], null,
                (must ?? []).Select(x => new CvRequirementMatch(
                    x.RequirementId, "MustHave", x.Text, "Strong", "Skills: .NET")).ToList(),
                [new CvSectionAnchor("Skills", "skills", "Skills")],
                [new CvAnalysisCitation("chunk-1", "source", null, "title")]));
        var ctrl = Controller(t, storage.Object, ai.Object, user, cvAnalysisCredits: 0);

        var created = Assert.IsType<CreatedResult>(await ctrl.Analyze(new CvAnalysisRequest(
            cvId, null, JobCategory.BE, null,
            [new CvRequirementInput(".NET", "client-1")], []), default));
        var detail = Assert.IsType<CvAnalysisResponse>(created.Value);

        var list = Assert.IsType<OkObjectResult>(await ctrl.List(default));
        var item = Assert.IsType<CvAnalysisListResponse>(Assert.Single(
            Assert.IsAssignableFrom<IReadOnlyList<CvAnalysisListResponse>>(list.Value)));

        var slim = Assert.Single(item.MustHaveMatches!);
        Assert.Equal("Strong", slim.Level);
        Assert.DoesNotContain("evidence", System.Text.Json.JsonSerializer.Serialize(item),
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Skills: .NET", detail.MustHaveMatches![0].Evidence);
        Assert.Equal(1, detail.MustHaveMatches[0].Page);
        Assert.Equal("Skills", detail.MustHaveMatches[0].SectionTitle);
        Assert.NotNull(detail.CvSections);
        Assert.NotNull(detail.Citations);
    }

    [Fact]
    public async Task Post_RequirementEvidence_UsesNormalizedOffsetForPage_AndPdfSeparators()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var cvId = Guid.NewGuid();
        var cvText = "Skills: identiﬁcation ﬂow\nASP.NET-Core\nProjects: micro-\nservices";
        var storage = new Mock<IStorageService>();
        storage.Setup(s => s.GetMetadata(cvId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(OwnedFile(cvId, user, "cv", cvText));
        var ai = new Mock<IAiServiceCvAnalyzer>();
        ai.Setup(x => x.AnalyzeAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>(),
                It.IsAny<IReadOnlyList<CvRequirementInput>?>(),
                It.IsAny<IReadOnlyList<CvRequirementInput>?>(),
                It.IsAny<IReadOnlyList<GroundingChunk>?>()))
            .ReturnsAsync((string _, string _, string? _, CancellationToken _,
                IReadOnlyList<CvRequirementInput>? must,
                IReadOnlyList<CvRequirementInput>? _,
                IReadOnlyList<GroundingChunk>? _) => new CvAnalysisAiResult(
                "s", [], [], [], null,
                (must ?? []).Select(x => new CvRequirementMatch(
                    x.RequirementId, "MustHave", x.Text, "Strong", "ASP.NET Core")).ToList(),
                [new CvSectionAnchor("Skills", "skills", "Skills")], []));

        var ctrl = Controller(t, storage.Object, ai.Object, user, cvAnalysisCredits: 0);
        var result = await ctrl.Analyze(new CvAnalysisRequest(
            cvId, null, JobCategory.BE, null,
            [new CvRequirementInput("ASP.NET Core", "client-1")], []), default);

        var body = Assert.IsType<CvAnalysisResponse>(((CreatedResult)result).Value);
        var match = Assert.Single(body.MustHaveMatches!);
        Assert.Equal("Strong", match.Level);
        Assert.Equal("ASP.NET Core", match.Evidence);
        Assert.Equal(2, match.Page);
    }

    [Theory]
    [InlineData("—")]
    [InlineData("---")]
    [InlineData("- -")]
    [InlineData("  -  ")]
    public async Task Post_RequirementEvidence_RejectsSeparatorOnlyEvidence(string evidence)
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var cvId = Guid.NewGuid();
        var storage = new Mock<IStorageService>();
        storage.Setup(s => s.GetMetadata(cvId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(OwnedFile(cvId, user, "cv", "Skills: PostgreSQL"));
        var ai = new Mock<IAiServiceCvAnalyzer>();
        ai.Setup(x => x.AnalyzeAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>(),
                It.IsAny<IReadOnlyList<CvRequirementInput>?>(),
                It.IsAny<IReadOnlyList<CvRequirementInput>?>(),
                It.IsAny<IReadOnlyList<GroundingChunk>?>()))
            .ReturnsAsync((string _, string _, string? _, CancellationToken _,
                IReadOnlyList<CvRequirementInput>? must,
                IReadOnlyList<CvRequirementInput>? _,
                IReadOnlyList<GroundingChunk>? _) => new CvAnalysisAiResult(
                "s", [], [], [], null,
                (must ?? []).Select(x => new CvRequirementMatch(
                    x.RequirementId, "MustHave", x.Text, "Strong", evidence)).ToList(),
                [], []));

        var ctrl = Controller(t, storage.Object, ai.Object, user, cvAnalysisCredits: 0);
        var result = await ctrl.Analyze(new CvAnalysisRequest(
            cvId, null, JobCategory.BE, null,
            [new CvRequirementInput("PostgreSQL", "client-1")], []), default);

        var body = Assert.IsType<CvAnalysisResponse>(((CreatedResult)result).Value);
        var match = Assert.Single(body.MustHaveMatches!);
        Assert.Equal("Weak", match.Level);
        Assert.Equal("Không thấy bằng chứng", match.Evidence);
        Assert.Null(match.Page);
        Assert.Null(match.SectionTitle);
    }

    [Fact]
    public async Task Post_RequirementMode_DeduplicatesTextAndMintsServerIds()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var cvId = Guid.NewGuid();
        var storage = new Mock<IStorageService>();
        storage.Setup(s => s.GetMetadata(cvId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(OwnedFile(cvId, user, "cv", "Skills: Docker"));
        var ai = new Mock<IAiServiceCvAnalyzer>();
        ai.Setup(x => x.AnalyzeAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>(),
                It.IsAny<IReadOnlyList<CvRequirementInput>?>(),
                It.IsAny<IReadOnlyList<CvRequirementInput>?>(),
                It.IsAny<IReadOnlyList<GroundingChunk>?>()))
            .ReturnsAsync((string _, string _, string? _, CancellationToken _,
                IReadOnlyList<CvRequirementInput>? must,
                IReadOnlyList<CvRequirementInput>? _,
                IReadOnlyList<GroundingChunk>? _) => new CvAnalysisAiResult(
                "s", [], [], [], null,
                (must ?? []).Select(x => new CvRequirementMatch(
                    x.RequirementId, "MustHave", x.Text, "Strong", "Skills: Docker")).ToList(),
                [], []));

        var ctrl = Controller(t, storage.Object, ai.Object, user, cvAnalysisCredits: 0);
        var result = await ctrl.Analyze(new CvAnalysisRequest(
            cvId, null, JobCategory.BE, null,
            [new CvRequirementInput(" Docker ", "client-a")],
            [new CvRequirementInput("docker", "client-b")]), default);

        var body = Assert.IsType<CvAnalysisResponse>(((CreatedResult)result).Value);
        Assert.Single(body.MustHaveMatches!);
        Assert.Empty(body.NiceToHaveMatches!);
        Assert.NotEqual("client-a", body.MustHaveMatches[0].RequirementId);
        Assert.Equal("Docker", body.MustHaveMatches[0].Text);
    }

    /// <summary>
    /// `mustHave: []` + `niceToHave: []` phải đi nhánh LEGACY, không phải "requirement mode với 0
    /// requirement".
    ///
    /// <para>Trước đây điều kiện là "mảng có mặt hay không" (<c>is not null</c>), nên client gửi hai
    /// mảng rỗng — chuyện xảy ra ngay khi bước tách JD không ra requirement nào — sẽ bật requirement
    /// mode: AI được gọi với danh sách rỗng, <c>jdMatch</c> bị vứt (requirement mode gate nó thành
    /// null) và <c>requirementMatches</c> cũng rỗng ⇒ báo cáo TRẮNG, mà 1 credit của user vẫn bị
    /// trừ. Không có use case hợp lệ nào cần hành vi đó.</para>
    ///
    /// <para>Mấu chốt của test: setup/verify chỉ liệt kê 4 tham số đầu. Moq điền default (null) cho
    /// optional bị bỏ, nên biểu thức này CHỈ khớp lời gọi legacy — vào requirement mode thì mock
    /// không khớp, trả null và test đỏ ngay.</para>
    /// </summary>
    [Fact]
    public async Task Post_EmptyRequirementArrays_FallsBackToLegacy_AndKeepsJdMatch()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var cvId = Guid.NewGuid();

        var storage = new Mock<IStorageService>();
        storage.Setup(s => s.GetMetadata(cvId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(OwnedFile(cvId, user, "cv", "Nội dung CV..."));

        var ai = AiMock(SampleAi(withJdMatch: true));
        var ctrl = Controller(t, storage.Object, ai.Object, user);

        var result = await ctrl.Analyze(new CvAnalysisRequest(
            cvId, null, JobCategory.BE, "JD: cần C# và SQL", [], []), default);

        var body = Assert.IsType<CvAnalysisResponse>(((CreatedResult)result).Value);
        Assert.NotNull(body.JdMatch);            // legacy giữ jdMatch…
        Assert.Null(body.MustHaveMatches);       // …và KHÔNG có nhánh requirement rỗng
        Assert.Null(body.NiceToHaveMatches);
        Assert.Null(body.RequirementSummary);

        ai.Verify(x => x.AnalyzeAsync(
            "BE", "Nội dung CV...", "JD: cần C# và SQL", It.IsAny<CancellationToken>()), Times.Once);
        ai.VerifyNoOtherCalls();   // chặn hẳn overload requirement-mode
    }

    /// <summary>
    /// Cửa hẹp hơn của cùng cái bẫy: mảng KHÔNG rỗng nhưng mọi text đều là khoảng trắng.
    ///
    /// <para>Đếm số phần tử gửi lên (thay vì số requirement dùng được) mới chỉ vá được
    /// `[] + []`. `[{ "text": "   " }]` vẫn là requirement mode với 0 requirement, và hậu quả y hệt:
    /// báo cáo trắng đổi lấy 1 credit. Quyết định phải dựa trên kết quả CHUẨN HOÁ, vì mọi cách viết
    /// "không có requirement nào" buộc phải ra cùng một nhánh.</para>
    /// </summary>
    [Fact]
    public async Task Post_RequirementToanKhoangTrang_FallsBackToLegacy_AndKeepsJdMatch()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var cvId = Guid.NewGuid();

        var storage = new Mock<IStorageService>();
        storage.Setup(s => s.GetMetadata(cvId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(OwnedFile(cvId, user, "cv", "Nội dung CV..."));

        var ai = AiMock(SampleAi(withJdMatch: true));
        var ctrl = Controller(t, storage.Object, ai.Object, user);

        var result = await ctrl.Analyze(new CvAnalysisRequest(
            cvId, null, JobCategory.BE, "JD: cần C# và SQL",
            [new CvRequirementInput("   ")], null), default);

        var body = Assert.IsType<CvAnalysisResponse>(((CreatedResult)result).Value);
        Assert.NotNull(body.JdMatch);
        Assert.Null(body.MustHaveMatches);
        Assert.Null(body.NiceToHaveMatches);
        Assert.Null(body.RequirementSummary);

        ai.Verify(x => x.AnalyzeAsync(
            "BE", "Nội dung CV...", "JD: cần C# và SQL", It.IsAny<CancellationToken>()), Times.Once);
        ai.VerifyNoOtherCalls();
    }

    /// <summary>
    /// Trần 20 requirement phải đo trên requirement THẬT: 25 dòng trắng là "không khai gì" nên về
    /// legacy êm ả, KHÔNG phải 400 "vượt quá 20" — báo lỗi giới hạn cho một danh sách rỗng thì
    /// người dùng không thể hiểu nổi mình sai ở đâu.
    /// </summary>
    [Fact]
    public async Task Post_NhieuDongTrangVuotTran_VanLaLegacy_KhongBao400()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var cvId = Guid.NewGuid();

        var storage = new Mock<IStorageService>();
        storage.Setup(s => s.GetMetadata(cvId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(OwnedFile(cvId, user, "cv", "Nội dung CV..."));

        var ai = AiMock(SampleAi(withJdMatch: false));
        var ctrl = Controller(t, storage.Object, ai.Object, user);
        var toanTrang = Enumerable.Range(0, 25)
            .Select(_ => new CvRequirementInput("   "))
            .ToList();

        var result = await ctrl.Analyze(new CvAnalysisRequest(
            cvId, null, JobCategory.BE, null, toanTrang, []), default);

        Assert.IsType<CreatedResult>(result);
        ai.Verify(x => x.AnalyzeAsync(
            "BE", "Nội dung CV...", null, It.IsAny<CancellationToken>()), Times.Once);
        ai.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Post_TooManyRequirements_Returns400BeforeCvStorageReserveOrAi()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var storage = new Mock<IStorageService>(MockBehavior.Strict);
        var ai = new Mock<IAiServiceCvAnalyzer>(MockBehavior.Strict);
        var credits = new Mock<ICreditReservationClient>(MockBehavior.Strict);
        var ctrl = Controller(t, storage.Object, ai.Object, user, credits.Object);
        var tooMany = Enumerable.Range(0, 21)
            .Select(i => new CvRequirementInput($"skill-{i}", $"client-{i}"))
            .ToList();

        var result = await ctrl.Analyze(new CvAnalysisRequest(
            Guid.NewGuid(), null, JobCategory.BE, null, tooMany, []), default);

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("20", bad.Value!.ToString());
        storage.VerifyNoOtherCalls();
        credits.VerifyNoOtherCalls();
        ai.VerifyNoOtherCalls();
    }

    // ── (b) GET của chủ → đọc đúng ────────────────────────────────────────────────
    [Fact]
    public async Task Get_Owner_ReturnsAnalysis()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var cvId = Guid.NewGuid();

        var storage = new Mock<IStorageService>();
        storage.Setup(s => s.GetMetadata(cvId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(OwnedFile(cvId, user, "cv", "CV..."));

        var ctrl = Controller(t, storage.Object, AiMock(SampleAi(false)).Object, user);
        var created = Assert.IsType<CreatedResult>(
            await ctrl.Analyze(new CvAnalysisRequest(cvId, null, JobCategory.FE), default));
        var id = ((CvAnalysisResponse)created.Value!).Id;

        var getResult = await ctrl.Get(id, default);

        var ok = Assert.IsType<OkObjectResult>(getResult);
        var body = Assert.IsType<CvAnalysisResponse>(ok.Value);
        Assert.Equal(id, body.Id);
        Assert.Equal("FE", body.JobCategory);
    }

    // ── (c) GET của người khác → 403 ──────────────────────────────────────────────
    [Fact]
    public async Task Get_OtherUser_Returns403()
    {
        using var t = new TestDb();
        var owner = Guid.NewGuid();
        var stranger = Guid.NewGuid();
        var cvId = Guid.NewGuid();

        var storage = new Mock<IStorageService>();
        storage.Setup(s => s.GetMetadata(cvId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(OwnedFile(cvId, owner, "cv", "CV..."));

        // owner tạo phân tích
        var ownerCtrl = Controller(t, storage.Object, AiMock(SampleAi(false)).Object, owner);
        var created = Assert.IsType<CreatedResult>(
            await ownerCtrl.Analyze(new CvAnalysisRequest(cvId, null, JobCategory.BE), default));
        var id = ((CvAnalysisResponse)created.Value!).Id;

        // stranger đọc → 403
        var strangerCtrl = Controller(t, storage.Object, AiMock(SampleAi(false)).Object, stranger);
        var getResult = await strangerCtrl.Get(id, default);

        var obj = Assert.IsType<ObjectResult>(getResult);
        Assert.Equal(StatusCodes.Status403Forbidden, obj.StatusCode);
    }

    // GET id không tồn tại → 404.
    [Fact]
    public async Task Get_NotFound_Returns404()
    {
        using var t = new TestDb();
        var ctrl = Controller(t, new Mock<IStorageService>().Object,
            AiMock(SampleAi(false)).Object, Guid.NewGuid());

        var getResult = await ctrl.Get(Guid.NewGuid(), default);

        Assert.IsType<NotFoundObjectResult>(getResult);
    }

    // ── (d) AIService lỗi → 502 + KHÔNG tạo row ───────────────────────────────────
    [Fact]
    public async Task Post_AiFails_Returns502_NoRow()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var cvId = Guid.NewGuid();

        var storage = new Mock<IStorageService>();
        storage.Setup(s => s.GetMetadata(cvId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(OwnedFile(cvId, user, "cv", "CV..."));

        var ai = new Mock<IAiServiceCvAnalyzer>();
        ai.Setup(x => x.AnalyzeAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AiServiceException("AIService /analyze-cv trả 500"));

        var ctrl = Controller(t, storage.Object, ai.Object, user);

        var result = await ctrl.Analyze(new CvAnalysisRequest(cvId, null, JobCategory.BE), default);

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status502BadGateway, obj.StatusCode);
        Assert.False(await t.Db.CvAnalyses.AsNoTracking().AnyAsync());   // không lưu khi AI lỗi
    }

    // POST cvId không tồn tại → 404.
    [Fact]
    public async Task Post_CvNotFound_Returns404()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var storage = new Mock<IStorageService>();
        storage.Setup(s => s.GetMetadata(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((FileRecord?)null);

        var ctrl = Controller(t, storage.Object, AiMock(SampleAi(false)).Object, user);

        var result = await ctrl.Analyze(new CvAnalysisRequest(Guid.NewGuid(), null, JobCategory.BE), default);

        Assert.IsType<NotFoundObjectResult>(result);
        Assert.False(await t.Db.CvAnalyses.AsNoTracking().AnyAsync());
    }

    // POST CV của người khác → 403.
    [Fact]
    public async Task Post_CvOwnedByOther_Returns403()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var cvId = Guid.NewGuid();
        var storage = new Mock<IStorageService>();
        storage.Setup(s => s.GetMetadata(cvId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(OwnedFile(cvId, Guid.NewGuid(), "cv", "CV..."));   // chủ khác

        var ctrl = Controller(t, storage.Object, AiMock(SampleAi(false)).Object, user);

        var result = await ctrl.Analyze(new CvAnalysisRequest(cvId, null, JobCategory.BE), default);

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status403Forbidden, obj.StatusCode);
    }

    // POST CV không parse được (parsed_text rỗng) → 400.
    [Fact]
    public async Task Post_CvUnreadable_Returns400()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var cvId = Guid.NewGuid();
        var storage = new Mock<IStorageService>();
        storage.Setup(s => s.GetMetadata(cvId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(OwnedFile(cvId, user, "cv", "   "));   // rỗng

        var ctrl = Controller(t, storage.Object, AiMock(SampleAi(false)).Object, user);

        var result = await ctrl.Analyze(new CvAnalysisRequest(cvId, null, JobCategory.BE), default);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.False(await t.Db.CvAnalyses.AsNoTracking().AnyAsync());
    }

    // GET list → chỉ phân tích của chính user (mới nhất trước).
    [Fact]
    public async Task List_ReturnsOnlyOwnAnalyses()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var other = Guid.NewGuid();
        var cvId = Guid.NewGuid();

        var storage = new Mock<IStorageService>();
        storage.Setup(s => s.GetMetadata(cvId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(OwnedFile(cvId, user, "cv", "CV..."));

        var userCtrl = Controller(t, storage.Object, AiMock(SampleAi(false)).Object, user);
        await userCtrl.Analyze(new CvAnalysisRequest(cvId, null, JobCategory.BE), default);
        await userCtrl.Analyze(new CvAnalysisRequest(cvId, null, JobCategory.BE), default);

        var listResult = await userCtrl.List(default);
        var ok = Assert.IsType<OkObjectResult>(listResult);
        var items = Assert.IsAssignableFrom<IReadOnlyList<CvAnalysisListResponse>>(ok.Value);
        Assert.Equal(2, items.Count);

        // user khác → rỗng
        var otherCtrl = Controller(t, storage.Object, AiMock(SampleAi(false)).Object, other);
        var otherList = Assert.IsType<OkObjectResult>(await otherCtrl.List(default));
        Assert.Empty(Assert.IsAssignableFrom<IReadOnlyList<CvAnalysisListResponse>>(otherList.Value));
    }

    // ── BC7b: CV analysis TÍNH PHÍ (BC-4/D22) — reserve/consume/release ────────────

    // Có credit → reserve (owner=User, khoá=Id row) TRƯỚC gọi AI, consume SAU khi lưu row; không release.
    [Fact]
    public async Task Post_WithCredit_ReservesBeforeAi_ConsumesAfter()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var cvId = Guid.NewGuid();

        var storage = new Mock<IStorageService>();
        storage.Setup(s => s.GetMetadata(cvId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(OwnedFile(cvId, user, "cv", "CV..."));

        // Ghi thứ tự gọi để chốt reserve TRƯỚC AI, consume SAU.
        var calls = new List<string>();
        var ai = new Mock<IAiServiceCvAnalyzer>();
        ai.Setup(x => x.AnalyzeAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback(() => calls.Add("ai"))
            .ReturnsAsync(SampleAi(withJdMatch: false));

        var credits = new Mock<ICreditReservationClient>();
        credits.Setup(x => x.ReserveAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Callback(() => calls.Add("reserve"))
            .ReturnsAsync(new CreditReservationResult(Guid.NewGuid(), 1));
        credits.Setup(x => x.ConsumeAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Callback(() => calls.Add("consume"))
            .Returns(Task.CompletedTask);

        var ctrl = Controller(t, storage.Object, ai.Object, user, credits.Object);

        var result = await ctrl.Analyze(new CvAnalysisRequest(cvId, null, JobCategory.BE), default);

        Assert.IsType<CreatedResult>(result);
        var row = await t.Db.CvAnalyses.AsNoTracking().SingleAsync();

        // reserve: owner=User, ownerId=candidate, khoá=Id row cv_analyses (operationId); consume cùng khoá.
        credits.Verify(x => x.ReserveAsync("User", user, row.Id, It.IsAny<CancellationToken>()), Times.Once);
        credits.Verify(x => x.ConsumeAsync(row.Id, It.IsAny<CancellationToken>()), Times.Once);
        credits.Verify(x => x.ReleaseAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Equal(new[] { "reserve", "ai", "consume" }, calls);   // reserve TRƯỚC AI, consume SAU
    }

    // Ví hết (reserve ném 402/Insufficient) → 402 + KHÔNG row + AI KHÔNG gọi + KHÔNG consume (PAY-5).
    [Fact]
    public async Task Post_InsufficientCredit_Returns402_NoRow_NoAiCall()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var cvId = Guid.NewGuid();

        var storage = new Mock<IStorageService>();
        storage.Setup(s => s.GetMetadata(cvId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(OwnedFile(cvId, user, "cv", "CV..."));

        var ai = AiMock(SampleAi(false));

        var credits = new Mock<ICreditReservationClient>();
        credits.Setup(x => x.ReserveAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InsufficientCreditException("Ví không đủ credit để phân tích CV"));

        var ctrl = Controller(t, storage.Object, ai.Object, user, credits.Object);

        var result = await ctrl.Analyze(new CvAnalysisRequest(cvId, null, JobCategory.BE), default);

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status402PaymentRequired, obj.StatusCode);
        Assert.False(await t.Db.CvAnalyses.AsNoTracking().AnyAsync());   // KHÔNG lưu khi ví hết
        ai.Verify(x => x.AnalyzeAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
        credits.Verify(x => x.ConsumeAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // AIService lỗi (502) SAU reserve → release chỗ giữ + KHÔNG row + KHÔNG consume.
    [Fact]
    public async Task Post_AiFailsAfterReserve_ReleasesCredit_NoRow_NoConsume()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var cvId = Guid.NewGuid();

        var storage = new Mock<IStorageService>();
        storage.Setup(s => s.GetMetadata(cvId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(OwnedFile(cvId, user, "cv", "CV..."));

        var ai = new Mock<IAiServiceCvAnalyzer>();
        ai.Setup(x => x.AnalyzeAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AiServiceException("AIService /analyze-cv trả 500"));

        var credits = new Mock<ICreditReservationClient>();
        credits.Setup(x => x.ReserveAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CreditReservationResult(Guid.NewGuid(), 1));

        var ctrl = Controller(t, storage.Object, ai.Object, user, credits.Object);

        var result = await ctrl.Analyze(new CvAnalysisRequest(cvId, null, JobCategory.BE), default);

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status502BadGateway, obj.StatusCode);
        Assert.False(await t.Db.CvAnalyses.AsNoTracking().AnyAsync());   // KHÔNG lưu khi AI lỗi
        credits.Verify(x => x.ReleaseAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Once);
        credits.Verify(x => x.ConsumeAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // Kill-switch Billing:CvAnalysisCredits=0 → miễn phí: KHÔNG chạm Payment nhưng vẫn lưu row.
    [Fact]
    public async Task Post_BillingDisabled_SkipsCredit_StillPersists()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var cvId = Guid.NewGuid();

        var storage = new Mock<IStorageService>();
        storage.Setup(s => s.GetMetadata(cvId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(OwnedFile(cvId, user, "cv", "CV..."));

        var credits = new Mock<ICreditReservationClient>(MockBehavior.Strict);   // gọi bất kỳ → fail

        var ctrl = Controller(t, storage.Object, AiMock(SampleAi(false)).Object, user,
            credits.Object, cvAnalysisCredits: 0);

        var result = await ctrl.Analyze(new CvAnalysisRequest(cvId, null, JobCategory.BE), default);

        Assert.IsType<CreatedResult>(result);
        Assert.True(await t.Db.CvAnalyses.AsNoTracking().AnyAsync());
        credits.VerifyNoOtherCalls();   // tính phí tắt → không đụng Payment
    }

    // ── BK6: jobCategory BẮT BUỘC ─────────────────────────────────────────────────
    // Thiếu jobCategory (null) → 400 TRƯỚC reserve: KHÔNG giữ credit, KHÔNG gọi AI, KHÔNG lưu row.
    [Fact]
    public async Task Post_MissingJobCategory_Returns400_NoReserve_NoAi_NoRow()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var cvId = Guid.NewGuid();

        var storage = new Mock<IStorageService>();
        storage.Setup(s => s.GetMetadata(cvId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(OwnedFile(cvId, user, "cv", "CV..."));

        var ai = AiMock(SampleAi(false));
        var credits = CreditsMock();

        var ctrl = Controller(t, storage.Object, ai.Object, user, credits.Object);

        // jobCategory null (thiếu trong request) → 400.
        var result = await ctrl.Analyze(new CvAnalysisRequest(cvId, null, null), default);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.False(await t.Db.CvAnalyses.AsNoTracking().AnyAsync());   // KHÔNG lưu row
        credits.Verify(x => x.ReserveAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        ai.Verify(x => x.AnalyzeAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
