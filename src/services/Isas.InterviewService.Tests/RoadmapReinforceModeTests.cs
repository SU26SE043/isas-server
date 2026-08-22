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
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace Isas.InterviewService.Tests;

/// <summary>
/// Chế độ lộ trình <c>Reinforce</c> (ôn tập lại) vs <c>LevelUp</c> (mặc định, hành vi cũ).
///
/// <para>🔴 Bất biến nặng nhất: thiếu dữ liệu điểm yếu thì phải <b>NÓI RA</b> (400), tuyệt đối
/// KHÔNG âm thầm rơi về <c>LevelUp</c> rồi trả về một lộ trình trông như đã ôn tập. Đây là lớp
/// lỗi "nén im lặng" đã cắn dự án nhiều lần.</para>
///
/// <para>Mọi ca từ chối còn khoá thêm <c>Times.Never</c> trên generator: guard phải chạy TRƯỚC
/// lời gọi AI, nếu không người dùng bị từ chối mà hệ thống vẫn đốt một lượt Gemini.</para>
/// </summary>
public class RoadmapReinforceModeTests
{
    private static RoadmapGenAiResult Sample()
        => new(new List<GeneratedMilestone>
        {
            new("M1", new List<string> { "Tư duy giải quyết vấn đề" },
                new List<GeneratedLesson> { new("L1") })
        });

    private sealed record Captured(RoadmapMode Mode, IReadOnlyList<RoadmapWeakness>? Weaknesses);

    private static Mock<IAiServiceRoadmapGenerator> GenMock(Action<Captured>? capture = null)
    {
        var m = new Mock<IAiServiceRoadmapGenerator>();
        var setup = m.Setup(x => x.GenerateAsync(
            It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<IReadOnlyList<RoadmapWeakness>?>(), 
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(),
            It.IsAny<IReadOnlyList<QuestionTargetCriterionDto>?>(), It.IsAny<string>(),
            It.IsAny<IReadOnlyList<CriterionEvidence>?>(), It.IsAny<RoadmapMode>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()));
        if (capture is not null)
            setup.Callback<string, string, IReadOnlyList<RoadmapWeakness>?, string?, string?, string?,
                    IReadOnlyList<QuestionTargetCriterionDto>?, string,
                    IReadOnlyList<CriterionEvidence>?, RoadmapMode, string?, CancellationToken>(
                    (_, _, w, _, _, _, _, _, _, mode, _, _) => capture(new Captured(mode, w)))
                .ReturnsAsync(Sample());
        else
            setup.ReturnsAsync(Sample());
        return m;
    }

    private static RoadmapsController Controller(
        TestDb t, IAiServiceRoadmapGenerator gen, Guid userId, int? minSessions = null)
    {
        var options = minSessions is null
            ? null
            : Options.Create(new RoadmapOptions { ReinforceMinSessions = minSessions.Value });
        var service = new RoadmapService(
            t.Db, new Mock<IStorageService>().Object, gen, NullLogger<RoadmapService>.Instance,
            roadmapOptions: options);
        var controller = new RoadmapsController(
            service, new Mock<IRoadmapLessonService>().Object,
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

    /// <summary>Seed 1 buổi B2C đã Scored + các tiêu chí có điểm (BC9) → nguồn baseline/điểm yếu.</summary>
    private static Guid SeedScoredSession(
        TestDb t, Guid candidateId, params (string name, decimal pct, bool needsImprovement)[] criteria)
    {
        var session = TestDb.Session(candidateId, SessionStatus.Scored, JobCategory.BE);
        t.Db.PracticeSessions.Add(session);
        foreach (var (name, pct, needs) in criteria)
        {
            // Production chỉ có MỘT bộ tiêu chí cho mỗi (nghề, ngôn ngữ) — mọi buổi trỏ vào chính nó.
            var crit = t.Db.RubricCriteria.Local
                    .FirstOrDefault(c => c.Name == name && c.CandidateId == null
                                         && c.JobCategory == JobCategory.BE)
                ?? t.Db.RubricCriteria.FirstOrDefault(
                    c => c.Name == name && c.CandidateId == null && c.JobCategory == JobCategory.BE);
            if (crit is null)
            {
                crit = TestDb.Criterion(JobCategory.BE, name: name);
                t.Db.RubricCriteria.Add(crit);
            }
            t.Db.SessionCriterionScores.Add(new SessionCriterionScore
            {
                Id = Guid.NewGuid(),
                SessionId = session.Id,
                CriterionId = crit.Id,
                CriterionName = name,
                AverageScore = 2m,
                MaxScore = 5,
                Percentage = pct,
                Weight = 1m,
                NeedsImprovement = needs,
                CreatedAt = DateTime.UtcNow
            });
        }
        t.Db.SaveChanges();
        return session.Id;
    }

    private static CreateRoadmapRequest Req(
        string? mode, IReadOnlyList<Guid>? sessionIds = null)
        => new(JobCategory.BE, RoadmapLevel.Junior, null,
            SessionIds: sessionIds, Mode: mode);

    private static List<Guid> TwoWeakSessions(TestDb t, Guid user) =>
    [
        SeedScoredSession(t, user, ("Tư duy giải quyết vấn đề", 40m, true)),
        SeedScoredSession(t, user, ("Tư duy giải quyết vấn đề", 45m, true))
    ];

    // ── (1) LevelUp — hành vi cũ, không đổi ─────────────────────────────────────────────────

    [Fact]
    public async Task ModeVangMat_MacDinh_LevelUp()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        Captured? seen = null;
        var gen = GenMock(c => seen = c);

        var res = await Controller(t, gen.Object, user).Create(Req(null), default);

        var created = Assert.IsType<CreatedResult>(res);
        var body = Assert.IsType<RoadmapResponse>(created.Value);
        Assert.Equal(nameof(RoadmapMode.LevelUp), body.Mode);
        Assert.Equal(RoadmapMode.LevelUp, seen!.Mode);
        Assert.Equal(RoadmapMode.LevelUp, (await t.Db.Roadmaps.SingleAsync()).Mode);
    }

    [Fact]
    public async Task LevelUp_KhongCanBuoiLuyenNao_VanTaoDuoc()
    {
        // Bất biến quan trọng: ngưỡng buổi tối thiểu CHỈ áp cho chế độ ôn tập. Áp nhầm cho LevelUp
        // sẽ chặn đúng nhóm người dùng mới — nhóm đông nhất, và là nhóm chưa có buổi nào để chọn.
        using var t = new TestDb();
        var res = await Controller(t, GenMock().Object, Guid.NewGuid())
            .Create(Req(nameof(RoadmapMode.LevelUp)), default);
        Assert.IsType<CreatedResult>(res);
    }

    // ── (2) Reinforce — đường hạnh phúc ─────────────────────────────────────────────────────

    [Fact]
    public async Task Reinforce_DuBuoiVaCoDiemYeu_TaoDuocVaLuuMode()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        Captured? seen = null;
        var gen = GenMock(c => seen = c);

        var res = await Controller(t, gen.Object, user)
            .Create(Req(nameof(RoadmapMode.Reinforce), TwoWeakSessions(t, user)), default);

        var created = Assert.IsType<CreatedResult>(res);
        var body = Assert.IsType<RoadmapResponse>(created.Value);
        Assert.Equal(nameof(RoadmapMode.Reinforce), body.Mode);
        Assert.Equal(RoadmapMode.Reinforce, (await t.Db.Roadmaps.SingleAsync()).Mode);

        // Chế độ ôn tập PHẢI mang điểm yếu đo được xuống AI — thiếu nó thì prompt rơi vào nhánh
        // "chưa có buổi luyện nào" = đúng hành vi LevelUp, chỉ khác cái nhãn.
        Assert.Equal(RoadmapMode.Reinforce, seen!.Mode);
        Assert.NotNull(seen.Weaknesses);
        Assert.NotEmpty(seen.Weaknesses!);
    }

    [Fact]
    public async Task Reinforce_TraVeMode_OCaChiTietLanDanhSach()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var ctrl = Controller(t, GenMock().Object, user);
        await ctrl.Create(Req(nameof(RoadmapMode.Reinforce), TwoWeakSessions(t, user)), default);
        var id = (await t.Db.Roadmaps.SingleAsync()).Id;

        var detail = Assert.IsType<RoadmapResponse>(
            Assert.IsType<OkObjectResult>(await ctrl.Get(id, default)).Value);
        Assert.Equal(nameof(RoadmapMode.Reinforce), detail.Mode);

        var list = Assert.IsType<OkObjectResult>(await ctrl.List(default));
        var rows = Assert.IsAssignableFrom<IEnumerable<RoadmapSummaryResponse>>(list.Value);
        Assert.Equal(nameof(RoadmapMode.Reinforce), Assert.Single(rows).Mode);
    }

    // ── (3) Thiếu dữ liệu ⇒ NÓI RA, không âm thầm rơi về LevelUp ────────────────────────────

    [Fact]
    public async Task Reinforce_KhongChonBuoiNao_400_KhongTaoVaKhongGoiAI()
    {
        using var t = new TestDb();
        var gen = GenMock();

        var res = await Controller(t, gen.Object, Guid.NewGuid())
            .Create(Req(nameof(RoadmapMode.Reinforce)), default);

        var bad = Assert.IsType<BadRequestObjectResult>(res);
        Assert.Contains("ít nhất", bad.Value!.ToString());
        Assert.Equal(0, await t.Db.Roadmaps.CountAsync());
        gen.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Reinforce_ChiMotBuoi_DuoiNguong_400_KhongGoiAI()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var gen = GenMock();
        var one = SeedScoredSession(t, user, ("Tư duy giải quyết vấn đề", 40m, true));

        var res = await Controller(t, gen.Object, user)
            .Create(Req(nameof(RoadmapMode.Reinforce), [one]), default);

        var bad = Assert.IsType<BadRequestObjectResult>(res);
        Assert.Contains("2 buổi", bad.Value!.ToString());
        Assert.Equal(0, await t.Db.Roadmaps.CountAsync());
        gen.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Reinforce_TrungIdKhongDuocTinhThanhHaiBuoi()
    {
        // Gửi cùng một id hai lần không tạo ra tín hiệu lặp lại nào — nó vẫn là MỘT buổi.
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var one = SeedScoredSession(t, user, ("Tư duy giải quyết vấn đề", 40m, true));

        var res = await Controller(t, GenMock().Object, user)
            .Create(Req(nameof(RoadmapMode.Reinforce), [one, one]), default);

        Assert.IsType<BadRequestObjectResult>(res);
        Assert.Equal(0, await t.Db.Roadmaps.CountAsync());
    }

    [Fact]
    public async Task Reinforce_DuBuoiNhungKhongCoDiemYeu_400_ThongDiepKhac()
    {
        // Người vừa luyện rất tốt: đủ buổi nhưng không tiêu chí nào cần cải thiện. Phải nói ĐÚNG
        // lý do — bảo họ "chưa luyện đủ" là sai sự thật và họ sẽ đi luyện thêm vô ích.
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var gen = GenMock();
        List<Guid> strong =
        [
            SeedScoredSession(t, user, ("Tư duy giải quyết vấn đề", 95m, false)),
            SeedScoredSession(t, user, ("Tư duy giải quyết vấn đề", 92m, false))
        ];

        var res = await Controller(t, gen.Object, user)
            .Create(Req(nameof(RoadmapMode.Reinforce), strong), default);

        var bad = Assert.IsType<BadRequestObjectResult>(res);
        var msg = bad.Value!.ToString()!;
        Assert.Contains("không có gì để ôn lại", msg);
        Assert.DoesNotContain("ít nhất", msg);   // KHÔNG được đổ cho "chưa đủ buổi"
        Assert.Equal(0, await t.Db.Roadmaps.CountAsync());
        gen.VerifyNoOtherCalls();
    }

    // ── (4) Ngưỡng cấu hình được ────────────────────────────────────────────────────────────

    [Fact]
    public async Task NguongCauHinhDuoc_Nang_Len_3_Thi_2_Buoi_Bi_Tu_Choi()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var res = await Controller(t, GenMock().Object, user, minSessions: 3)
            .Create(Req(nameof(RoadmapMode.Reinforce), TwoWeakSessions(t, user)), default);
        Assert.Contains("3 buổi", Assert.IsType<BadRequestObjectResult>(res).Value!.ToString());
    }

    [Fact]
    public async Task NguongCauHinhDuoc_Ha_Xuong_1_Thi_1_Buoi_Du()
    {
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var one = SeedScoredSession(t, user, ("Tư duy giải quyết vấn đề", 40m, true));
        var res = await Controller(t, GenMock().Object, user, minSessions: 1)
            .Create(Req(nameof(RoadmapMode.Reinforce), [one]), default);
        Assert.IsType<CreatedResult>(res);
    }

    [Fact]
    public async Task NguongBang0_VanKhongBoQuaGuardDiemYeu()
    {
        // Ngưỡng buổi là một mức chất lượng, hạ được. "Phải có điểm yếu" thì KHÔNG — không có
        // tiêu chí nào cần cải thiện nghĩa là chế độ ôn tập không có gì để ôn.
        using var t = new TestDb();
        var res = await Controller(t, GenMock().Object, Guid.NewGuid(), minSessions: 0)
            .Create(Req(nameof(RoadmapMode.Reinforce)), default);
        Assert.Contains("không có gì để ôn lại",
            Assert.IsType<BadRequestObjectResult>(res).Value!.ToString());
    }

    // ── (5) Giá trị mode sai ⇒ 400, KHÔNG âm thầm về mặc định (BK36) ────────────────────────

    [Theory]
    [InlineData("")]              // BK36 — chuỗi rỗng là GIÁ TRỊ SAI, không phải "không gửi"
    [InlineData("   ")]
    [InlineData("bogus")]
    [InlineData("reinforce")]     // sai hoa/thường — tập đóng case-sensitive, mẫu ValidateScope
    [InlineData("REINFORCE")]
    [InlineData("Level Up")]
    [InlineData("1")]             // Enum.TryParse sẽ NHẬN chuỗi số — ta cố ý không dùng nó
    [InlineData("0")]
    public async Task ModeLa_400_KhongRoiVeMacDinh(string mode)
    {
        using var t = new TestDb();
        var gen = GenMock();

        var res = await Controller(t, gen.Object, Guid.NewGuid()).Create(Req(mode), default);

        Assert.IsType<BadRequestObjectResult>(res);
        Assert.Equal(0, await t.Db.Roadmaps.CountAsync());
        gen.VerifyNoOtherCalls();
    }

    // ── (6) Lesson theory phải nhận mode CỦA CHÍNH LỘ TRÌNH ─────────────────────────────────

    [Theory]
    [InlineData(RoadmapMode.Reinforce)]
    [InlineData(RoadmapMode.LevelUp)]
    public async Task MoBaiHoc_ChuyenTiepModeCuaLoTrinh(RoadmapMode mode)
    {
        // Chỉ đổi cấu trúc roadmap mà để lý thuyết y như cũ thì tính năng chỉ đổi được tiêu đề
        // bài, còn thứ người học THẬT SỰ đọc vẫn là bài của chế độ tiến-lên.
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var lesson = new RoadmapLesson
        {
            Id = Guid.NewGuid(), OrderNo = 1, Title = "L1", Status = LessonStatus.Theory
        };
        var milestone = new RoadmapMilestone
        {
            Id = Guid.NewGuid(), OrderNo = 1, Title = "M1",
            FocusCriteria = ["Tư duy giải quyết vấn đề"], Status = MilestoneStatus.Pending
        };
        milestone.Lessons.Add(lesson);
        var roadmap = new Roadmap
        {
            Id = Guid.NewGuid(), CandidateId = user, JobCategory = JobCategory.BE,
            Level = RoadmapLevel.Junior, Mode = mode, Status = RoadmapStatus.Active
        };
        roadmap.Milestones.Add(milestone);
        t.Db.Roadmaps.Add(roadmap);
        await t.Db.SaveChangesAsync();

        RoadmapMode? seen = null;
        var gen = new Mock<IAiServiceRoadmapGenerator>();
        gen.Setup(g => g.GenerateLessonTheoryAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<IReadOnlyList<string>?>(),
                It.IsAny<IReadOnlyList<GroundingChunk>?>(),
                It.IsAny<IReadOnlyList<CriterionEvidence>?>(), It.IsAny<RoadmapMode>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, string, string, IReadOnlyList<string>, IReadOnlyList<string>?,
                IReadOnlyList<GroundingChunk>?, IReadOnlyList<CriterionEvidence>?, RoadmapMode,
                CancellationToken>((_, _, _, _, _, _, _, m, _) => seen = m)
            .ReturnsAsync(new LessonTheoryResult("## Lý thuyết\n\nNội dung đủ dài để dùng được.", []));

        var lessonService = new RoadmapLessonService(
            t.Db, new Mock<IPracticeService>().Object, gen.Object,
            NullLogger<RoadmapLessonService>.Instance);

        await lessonService.OpenLessonAsync(user, roadmap.Id, lesson.Id);

        Assert.Equal(mode, seen);
    }
}
