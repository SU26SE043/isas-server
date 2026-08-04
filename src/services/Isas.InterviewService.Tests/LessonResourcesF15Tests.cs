using System.Security.Claims;
using Isas.InterviewService.Controllers;
using Isas.InterviewService.DTOs;
using Isas.InterviewService.Entities;
using Isas.InterviewService.Enums;
using Isas.InterviewService.Services;
using Isas.InterviewService.Services.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Isas.InterviewService.Tests;

/// <summary>
/// F15 (FR09) — tài liệu học gợi ý cho lesson.
///
/// <para><b>Điểm cần khoá</b> không phải "có trả resources không" mà là:</para>
/// <list type="number">
///   <item>tài liệu sinh CÙNG lượt với lý thuyết và lưu CÙNG một lần ghi — không có trạng thái
///         "có theory mà chưa có resources" (guard idempotent chỉ nhìn <c>theory_content</c>);</item>
///   <item><b>url null vẫn là mục hợp lệ</b> — đó là hình dạng bình thường khi AIService loại link
///         không thuộc allowlist tên miền, KHÔNG phải dữ liệu hỏng;</item>
///   <item>resources rỗng KHÔNG phải lỗi (khác theoryMarkdown rỗng → 502).</item>
/// </list>
/// </summary>
public class LessonResourcesF15Tests
{
    private static Roadmap SeedRoadmap(TestDb t, Guid candidateId)
    {
        var roadmap = new Roadmap
        {
            Id = Guid.NewGuid(),
            CandidateId = candidateId,
            JobCategory = JobCategory.BE,
            Level = RoadmapLevel.Junior,
            Status = RoadmapStatus.Active,
            CreatedAt = DateTime.UtcNow,
            Milestones =
            [
                new RoadmapMilestone
                {
                    Id = Guid.NewGuid(),
                    OrderNo = 1,
                    Title = "Milestone 1",
                    FocusCriteria = ["Thiết kế CSDL"],
                    Status = MilestoneStatus.Pending,
                    Lessons =
                    [
                        new RoadmapLesson
                        {
                            Id = Guid.NewGuid(), OrderNo = 1,
                            Title = "Transaction", Status = LessonStatus.Theory
                        }
                    ]
                }
            ]
        };
        t.Db.Roadmaps.Add(roadmap);
        t.Db.SaveChanges();
        return roadmap;
    }

    private static Mock<IAiServiceRoadmapGenerator> GeneratorReturning(
        string theory, params LessonResource[] resources)
    {
        var gen = new Mock<IAiServiceRoadmapGenerator>();
        gen.Setup(g => g.GenerateLessonTheoryAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<IReadOnlyList<string>?>(),
                It.IsAny<IReadOnlyList<GroundingChunk>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LessonTheoryResult(theory, resources));
        return gen;
    }

    private static RoadmapsController Controller(TestDb t, IAiServiceRoadmapGenerator gen, Guid userId)
    {
        var lessonService = new RoadmapLessonService(
            t.Db, new Mock<IPracticeService>().Object, gen, NullLogger<RoadmapLessonService>.Instance);
        var controller = new RoadmapsController(
            new Mock<IRoadmapService>().Object, lessonService,
            new Mock<IRoadmapReportService>().Object, NullLogger<RoadmapsController>.Instance);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(ClaimTypes.NameIdentifier, userId.ToString())], "test"))
            }
        };
        return controller;
    }

    // (1) Mở lesson lần đầu → tài liệu được LƯU vào DB và TRẢ trong response.
    [Fact]
    public async Task OpenLesson_PersistsAndReturnsResources()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var roadmap = SeedRoadmap(t, user);
        var lessonId = roadmap.Milestones.First().Lessons.First().Id;

        var gen = GeneratorReturning("## Transaction",
            new LessonResource("PostgreSQL: Transactions", "Doc", "PostgreSQL",
                "https://www.postgresql.org/docs/current/tutorial-transactions.html"),
            new LessonResource("Designing Data-Intensive Applications", "Book", "O'Reilly", null));

        var ctrl = Controller(t, gen.Object, user);
        var ok = Assert.IsType<OkObjectResult>(await ctrl.OpenLesson(roadmap.Id, lessonId, default));
        var body = Assert.IsType<LessonResponse>(ok.Value);

        Assert.Equal(2, body.Resources.Count);
        Assert.Equal("PostgreSQL: Transactions", body.Resources[0].Title);
        Assert.Equal("Doc", body.Resources[0].Type);

        // Lưu thật xuống DB (jsonb round-trip), không chỉ nằm trong response.
        var saved = await t.Db.RoadmapLessons.AsNoTracking().FirstAsync(l => l.Id == lessonId);
        Assert.Equal(2, saved.Resources.Count);
        Assert.Equal("O'Reilly", saved.Resources[1].Publisher);
    }

    // (2) 🔑 url NULL là mục HỢP LỆ — hình dạng bình thường khi AIService loại link ngoài allowlist.
    //     Nếu chỗ nào đó "dọn" mục không có url thì người học mất luôn tài liệu chỉ vì thiếu link.
    [Fact]
    public async Task ResourceWithoutUrl_IsKept_NotDiscarded()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var roadmap = SeedRoadmap(t, user);
        var lessonId = roadmap.Milestones.First().Lessons.First().Id;

        var gen = GeneratorReturning("## Bài\n\nNội dung bài giảng.",
            new LessonResource("Sách hay, không có link tin cậy", "Book", null, null));

        var ctrl = Controller(t, gen.Object, user);
        var ok = Assert.IsType<OkObjectResult>(await ctrl.OpenLesson(roadmap.Id, lessonId, default));
        var body = Assert.IsType<LessonResponse>(ok.Value);

        var only = Assert.Single(body.Resources);
        Assert.Equal("Sách hay, không có link tin cậy", only.Title);
        Assert.Null(only.Url);            // không link — nhưng vẫn tra được theo tên
        Assert.Null(only.Publisher);

        var saved = await t.Db.RoadmapLessons.AsNoTracking().FirstAsync(l => l.Id == lessonId);
        Assert.Null(Assert.Single(saved.Resources).Url);   // null sống sót qua jsonb
    }

    // (3) AI không gợi ý được tài liệu nào → KHÔNG lỗi, lesson vẫn mở được với danh sách rỗng.
    [Fact]
    public async Task EmptyResources_IsNotAnError()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var roadmap = SeedRoadmap(t, user);
        var lessonId = roadmap.Milestones.First().Lessons.First().Id;

        var ctrl = Controller(t, GeneratorReturning("## Bài\n\nNội dung bài giảng.").Object, user);
        var ok = Assert.IsType<OkObjectResult>(await ctrl.OpenLesson(roadmap.Id, lessonId, default));
        var body = Assert.IsType<LessonResponse>(ok.Value);

        Assert.Empty(body.Resources);
        Assert.Equal("## Bài\n\nNội dung bài giảng.", body.TheoryContent);       // lý thuyết vẫn dùng được
    }

    // (4) 🔑 Lưu CÙNG lần ghi với theory: mở lại KHÔNG gọi AI lần 2 mà tài liệu vẫn còn.
    //     (Guard idempotent chỉ nhìn theory_content — nếu resources ghi ở bước riêng thì lần 2
    //      sẽ thấy lesson "có theory, resources rỗng" vĩnh viễn.)
    [Fact]
    public async Task ReopenLesson_KeepsResources_WithoutCallingAiAgain()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var roadmap = SeedRoadmap(t, user);
        var lessonId = roadmap.Milestones.First().Lessons.First().Id;

        var gen = GeneratorReturning("## Bài\n\nNội dung bài giảng.",
            new LessonResource("MDN", "Doc", "Mozilla", "https://developer.mozilla.org/"));
        var ctrl = Controller(t, gen.Object, user);

        await ctrl.OpenLesson(roadmap.Id, lessonId, default);
        var ok2 = Assert.IsType<OkObjectResult>(await ctrl.OpenLesson(roadmap.Id, lessonId, default));
        var body2 = Assert.IsType<LessonResponse>(ok2.Value);

        Assert.Equal("MDN", Assert.Single(body2.Resources).Title);
        gen.Verify(g => g.GenerateLessonTheoryAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<IReadOnlyList<string>>(), It.IsAny<IReadOnlyList<string>?>(),
            It.IsAny<IReadOnlyList<GroundingChunk>?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // (5) Lesson chưa mở → resources rỗng (chưa sinh), không null → FE duyệt @for an toàn.
    [Fact]
    public async Task UnopenedLesson_HasEmptyResources_NotNull()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var roadmap = SeedRoadmap(t, user);

        var svc = new RoadmapService(
            t.Db, new Mock<IStorageService>().Object,
            new Mock<IAiServiceRoadmapGenerator>().Object,
            NullLogger<RoadmapService>.Instance);

        var res = await svc.GetAsync(user, roadmap.Id);

        Assert.NotNull(res);
        var lesson = res!.Milestones.Single().Lessons.Single();
        Assert.NotNull(lesson.Resources);
        Assert.Empty(lesson.Resources);
    }
}
