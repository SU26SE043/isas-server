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
            It.IsAny<string?>(),
            It.IsAny<IReadOnlyList<QuestionTargetCriterionDto>?>(), It.IsAny<string>(),
            It.IsAny<RoadmapMode>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<IReadOnlyList<RoadmapMistake>?>()));
        if (capture is not null)
            setup.Callback<string, string, IReadOnlyList<RoadmapWeakness>?, string?,
                    IReadOnlyList<QuestionTargetCriterionDto>?, string,
                    RoadmapMode, CancellationToken,
                    IReadOnlyList<RoadmapMistake>?>(
                    (_, _, w, _, _, _, mode, _, _) => capture(new Captured(mode, w)))
                .ReturnsAsync(Sample());
        else
            setup.ReturnsAsync(Sample());
        return m;
    }

    // MIS1-B6 — `minSessions`/`RoadmapOptions.ReinforceMinSessions` đã GỠ HẲN (Guard 1 thay thế,
    // sàn CỐ ĐỊNH ≥1 buổi, không cấu hình được nữa). Tham số này XOÁ khỏi helper — 3 call site cũ
    // truyền `minSessions:` đã được cập nhật cùng lúc (xem mục "(4) Ngưỡng KHÔNG còn cấu hình được").
    private static RoadmapsController Controller(TestDb t, IAiServiceRoadmapGenerator gen, Guid userId)
    {
        var service = new RoadmapService(
            t.Db, new Mock<IStorageService>().Object, gen, NullLogger<RoadmapService>.Instance);
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

    /// <summary>Seed 1 buổi B2C đã Scored + các tiêu chí có điểm (BC9) → nguồn baseline/điểm yếu.
    /// MIS1-B6 — thân hàm chuyển vào TestSeed.ScoredSessionWithAnswers (dùng chung 3 file); giữ
    /// NGUYÊN chữ ký để 12 call site trong file này không phải sửa. `seedContentMistakes: true` vì
    /// mọi test ở đây gọi CreateAsync (Guard 3 nay đòi ≥1 lỗi nội dung — xem TestSeed.cs).</summary>
    private static Guid SeedScoredSession(
        TestDb t, Guid candidateId, params (string name, decimal pct, bool needsImprovement)[] criteria)
        => TestSeed.ScoredSessionWithAnswers(t, candidateId, JobCategory.BE, seedContentMistakes: true, criteria);

    private static CreateRoadmapRequest Req(
        string? mode, IReadOnlyList<Guid>? sessionIds = null)
        => new(JobCategory.BE, RoadmapLevel.Junior, null,
            SessionIds: sessionIds, Mode: mode);

    private static List<Guid> TwoWeakSessions(TestDb t, Guid user) =>
    [
        SeedScoredSession(t, user, ("Tư duy giải quyết vấn đề", 40m, true)),
        SeedScoredSession(t, user, ("Tư duy giải quyết vấn đề", 45m, true))
    ];

    // ── (1) LevelUp — MIS1-B6: nay CŨNG đòi buổi luyện + điểm yếu + lỗi nội dung (Guard 1/2/3) ──

    [Fact]
    public async Task ModeVangMat_MacDinh_LevelUp()
    {
        // MIS1-B6 — Guard 1 nay đòi ≥1 buổi BẤT KỂ mode; test này đo MẶC ĐỊNH MODE khi client
        // không gửi field `mode` (không còn đo được "0 buổi vẫn tạo được" — ca đó nay bị Guard 1
        // chặn, xem LevelUp_KhongCoBuoiLuyenNao_400_KhongTaoDuocNua ngay dưới). Seed 1 buổi hợp lệ
        // chỉ để VƯỢT QUA Guard 1/2/3, không phải trọng tâm của test này.
        using var t = new TestDb();
        var user = Guid.NewGuid();
        Captured? seen = null;
        var gen = GenMock(c => seen = c);
        var one = SeedScoredSession(t, user, ("Tư duy giải quyết vấn đề", 40m, true));

        var res = await Controller(t, gen.Object, user).Create(Req(null, [one]), default);

        var created = Assert.IsType<CreatedResult>(res);
        var body = Assert.IsType<RoadmapResponse>(created.Value);
        Assert.Equal(nameof(RoadmapMode.LevelUp), body.Mode);
        Assert.Equal(RoadmapMode.LevelUp, seen!.Mode);
        Assert.Equal(RoadmapMode.LevelUp, (await t.Db.Roadmaps.SingleAsync()).Mode);
    }

    [Fact]
    public async Task LevelUp_KhongCoBuoiLuyenNao_400_KhongTaoDuocNua()
    {
        // 🔴 MIS1-B6 — ĐẢO BẤT BIẾN CÓ CHỦ ĐÍCH (yêu cầu tường minh của task, tên cũ:
        // `LevelUp_KhongCanBuoiLuyenNao_VanTaoDuoc`). Bài gốc khoá ĐÚNG CHIỀU NGƯỢC LẠI, với lý do
        // viết sẵn: "ngưỡng buổi tối thiểu CHỈ áp cho chế độ ôn tập. Áp nhầm cho LevelUp sẽ chặn
        // đúng nhóm người dùng mới — nhóm đông nhất, và là nhóm chưa có buổi nào để chọn."
        //
        // VÌ SAO ĐẢO: roadmap nay XÂY TỪ LỖI THẬT trích từ buổi luyện đã chấm (RoadmapMistakeLoader,
        // MIS1-B4/B5) — không còn nhánh "roadmap CHUẨN theo level" để LevelUp rơi vào khi thiếu dữ
        // liệu. Guard 1 (ROADMAP_SESSIONS_REQUIRED) áp CHO CẢ HAI mode, không riêng Reinforce.
        //
        // AI QUYẾT: chốt trong đặc tả MIS1-B6 ("Bối cảnh": "không có buổi luyện đã chấm thì không
        // có gì để xây") — không phải quyết định tại chỗ của người viết code.
        //
        // CÁCH BÙ ĐẮP nhóm người dùng mới (đúng lo ngại của bài test gốc, KHÔNG bị bỏ qua): frontend
        // phải DẪN người dùng CHƯA CÓ buổi nào đi LUYỆN MỘT BUỔI TỰ DO trước, rồi mới cho vào wizard
        // tạo roadmap — không còn đường "tạo roadmap trước, luyện sau" làm bước khởi động.
        using var t = new TestDb();
        var gen = GenMock();

        var res = await Controller(t, gen.Object, Guid.NewGuid())
            .Create(Req(nameof(RoadmapMode.LevelUp)), default);

        var bad = Assert.IsType<BadRequestObjectResult>(res);
        Assert.Contains("ROADMAP_SESSIONS_REQUIRED", bad.Value!.ToString());
        Assert.Equal(0, await t.Db.Roadmaps.CountAsync());
        gen.VerifyNoOtherCalls();
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
    //
    // MIS1-B6 — "thiếu dữ liệu" nay tách thành 3 GUARD RIÊNG trong RoadmapService.CreateAsync,
    // áp CHO CẢ HAI mode (không riêng Reinforce nữa): Guard 1 (thiếu buổi) · Guard 2 (thiếu điểm
    // yếu) · Guard 3 (có điểm yếu nhưng không trích được lỗi NỘI DUNG nào). Các test dưới đây vẫn
    // seed ở mode Reinforce (không đổi để giữ phạm vi file), nhưng nay đang xác nhận hành vi CỦA
    // GUARD UNIVERSAL, không phải một cơ chế riêng của Reinforce.

    [Fact]
    public async Task Reinforce_KhongChonBuoiNao_400_KhongTaoVaKhongGoiAI()
    {
        // Trước MIS1-B6: đây là guard RIÊNG của Reinforce (chosenCount < ReinforceMinSessions).
        // Nay: chính Guard 1 (ROADMAP_SESSIONS_REQUIRED, áp cho MỌI mode) nổ ở đây — assertion
        // "chứa ít nhất" vẫn đúng vì câu chữ mới cũng dùng cụm đó, nên KHÔNG cần đổi assert.
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
    public async Task Reinforce_ChiMotBuoiCoDiemYeuVaLoiNoiDung_DuDeTao()
    {
        // 🔴 MIS1-B6 — ĐẢO TIỀN ĐỀ, LÝ DO: bài gốc (`..._DuoiNguong_400_KhongGoiAI`) mong 1 buổi bị
        // từ chối vì DƯỚI ngưỡng `ReinforceMinSessions=2` (đã GỠ — mục 5). Guard 1 thay thế chỉ đòi
        // ≥1 buổi; 1 buổi có điểm yếu + `SeedScoredSession` (qua TestSeed) nay LUÔN kèm 1 content
        // mistake cho tiêu chí yếu ⇒ Guard 2 VÀ Guard 3 đều qua ⇒ tạo THÀNH CÔNG. Không còn gì để
        // "dưới ngưỡng" — repurpose bài test thành đối chứng dương cho đúng ca này.
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var gen = GenMock();
        var one = SeedScoredSession(t, user, ("Tư duy giải quyết vấn đề", 40m, true));

        var res = await Controller(t, gen.Object, user)
            .Create(Req(nameof(RoadmapMode.Reinforce), [one]), default);

        Assert.IsType<CreatedResult>(res);
        Assert.Equal(1, await t.Db.Roadmaps.CountAsync());
    }

    [Fact]
    public async Task Reinforce_TrungIdKhongDuocTinhThanhHaiBuoi()
    {
        // 🔴 MIS1-B6 — ĐẢO TIỀN ĐỀ, LÝ DO: bài gốc gửi TRÙNG id để "lách" xuống dưới ngưỡng tối
        // thiểu 2 buổi PHÂN BIỆT (đã GỠ cùng `ReinforceMinSessions`, mục 5) — không còn khái niệm
        // đó để lách. Giữ lại test, đổi mục tiêu sang bất biến CÒN Ý NGHĨA: gửi trùng id vẫn ĐỦ
        // (Guard 1 chỉ đòi ≥1 buổi) NHƯNG dedup đúng — `ResolvedFrom.SessionIds` phải ghi 1 id,
        // không nhân đôi thành [one, one].
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var one = SeedScoredSession(t, user, ("Tư duy giải quyết vấn đề", 40m, true));

        var res = await Controller(t, GenMock().Object, user)
            .Create(Req(nameof(RoadmapMode.Reinforce), [one, one]), default);

        var created = Assert.IsType<CreatedResult>(res);
        var body = Assert.IsType<RoadmapResponse>(created.Value);
        Assert.Equal([one], body.ResolvedFrom.SessionIds);
    }

    [Fact]
    public async Task Reinforce_DuBuoiNhungKhongCoDiemYeu_400_ThongDiepKhac()
    {
        // Người vừa luyện rất tốt: đủ buổi nhưng không tiêu chí nào cần cải thiện. Phải nói ĐÚNG
        // lý do — bảo họ "chưa luyện đủ" là sai sự thật và họ sẽ đi luyện thêm vô ích.
        //
        // MIS1-B6 — câu chữ Guard 2 đổi ("không có gì để ôn lại" → "không có gì để xây lộ trình",
        // vì guard nay áp CẢ LevelUp lẫn Reinforce — "ôn lại" không còn đúng cho cả hai mode).
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
        Assert.Contains("không có gì để xây lộ trình", msg);
        Assert.DoesNotContain("ít nhất", msg);   // KHÔNG được đổ cho "chưa đủ buổi"
        Assert.Equal(0, await t.Db.Roadmaps.CountAsync());
        gen.VerifyNoOtherCalls();
    }

    // ── (4) Ngưỡng KHÔNG còn cấu hình được — ReinforceMinSessions đã GỠ (mục 5 MIS1-B6) ─────
    //
    // Toàn bộ 3 test dưới đây từng xoay quanh việc CẤU HÌNH `ReinforceMinSessions`. Property đó
    // không còn tồn tại — Guard 1 có sàn CỐ ĐỊNH ≥1 buổi, không qua config nào. Giữ NGUYÊN 3
    // phương thức test (KHÔNG xoá, đúng CẤM) nhưng đổi mục tiêu mỗi bài sang một bất biến CÒN
    // ĐÚNG trong thế giới mới, ghi rõ lý do đảo tại từng chỗ.

    [Fact]
    public async Task KhongConNguongCauHinh_HaiBuoiCoDiemYeu_LuonTaoDuoc()
    {
        // 🔴 MIS1-B6 — ĐẢO TIỀN ĐỀ, LÝ DO: bài gốc (`NguongCauHinhDuoc_Nang_Len_3_...`) cấu hình
        // `minSessions=3` để CHỦ Ý làm 2 buổi hợp lệ bị từ chối. Không còn cách nào "nâng ngưỡng"
        // — 2 buổi có điểm yếu (kèm content mistake mặc định của TestSeed) nay LUÔN đủ, bất kể số
        // lượng. Đổi thành đối chứng dương cho đúng thực tế đó.
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var res = await Controller(t, GenMock().Object, user)
            .Create(Req(nameof(RoadmapMode.Reinforce), TwoWeakSessions(t, user)), default);
        Assert.IsType<CreatedResult>(res);
    }

    [Fact]
    public void KhongConCauHinhReinforceMinSessions()
    {
        // 🔴 MIS1-B6 — ĐẢO TIỀN ĐỀ, LÝ DO: bài gốc (`NguongCauHinhDuoc_Ha_Xuong_1_...`) cấu hình
        // `minSessions=1` để chứng minh 1 buổi ĐỦ khi hạ ngưỡng. Guard 1 nay mặc định ≥1 buổi VÔ
        // ĐIỀU KIỆN (xem Reinforce_ChiMotBuoiCoDiemYeuVaLoiNoiDung_DuDeTao ở trên cho vế hành vi
        // "1 buổi đủ" — trùng lặp không tránh được vì cùng một sự thật, không còn hai cơ chế khác
        // nhau để tách biệt). Bài test này đổi vai trò thành ANCHOR chống hồi sinh property đã gỡ:
        // nếu ai đó thêm lại `ReinforceMinSessions`, bài test đỏ ngay, nhắc lại quyết định đã chốt.
        Assert.Null(typeof(RoadmapOptions).GetProperty("ReinforceMinSessions"));
    }

    [Fact]
    public async Task KhongConNguongCauHinh_VanKhongBoQuaGuardDiemYeu()
    {
        // 🔴 MIS1-B6 — ĐẢO TIỀN ĐỀ, LÝ DO: bài gốc (`NguongBang0_VanKhongBoQuaGuardDiemYeu`) dùng
        // `minSessions=0` (đã GỠ) + sessionIds RỖNG để chứng minh "hạ ngưỡng buổi về 0 không lách
        // được guard điểm yếu". Với sessionIds rỗng, THỨ TỰ guard MỚI cho Guard 1
        // (ROADMAP_SESSIONS_REQUIRED) nổ TRƯỚC — không còn tới được nhánh muốn kiểm (đây là 1
        // trong 2 test "đỏ vì thứ tự guard đổi, không phải nội dung" task đã cảnh báo). Đổi cách
        // tiếp cận: chọn ĐỦ Guard 1 (1 buổi) nhưng KHÔNG điểm yếu — xác nhận Guard 2 vẫn chặn dù
        // chỉ 1 buổi (không phụ thuộc SỐ buổi, chỉ phụ thuộc CÓ điểm yếu hay không).
        using var t = new TestDb();
        var user = Guid.NewGuid();
        var strong = SeedScoredSession(t, user, ("Tư duy giải quyết vấn đề", 95m, false));

        var res = await Controller(t, GenMock().Object, user)
            .Create(Req(nameof(RoadmapMode.Reinforce), [strong]), default);

        Assert.Contains("không có gì để xây lộ trình",
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
                It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyList<RoadmapMistake>?>()))
            .Callback<string, string, string, IReadOnlyList<string>, IReadOnlyList<string>?,
                IReadOnlyList<GroundingChunk>?, IReadOnlyList<CriterionEvidence>?, RoadmapMode,
                CancellationToken, IReadOnlyList<RoadmapMistake>?>((_, _, _, _, _, _, _, m, _, _) => seen = m)
            .ReturnsAsync(new LessonTheoryResult("## Lý thuyết\n\nNội dung đủ dài để dùng được.", []));

        var lessonService = new RoadmapLessonService(
            t.Db, new Mock<IPracticeService>().Object, gen.Object,
            NullLogger<RoadmapLessonService>.Instance);

        await lessonService.OpenLessonAsync(user, roadmap.Id, lesson.Id);

        Assert.Equal(mode, seen);
    }
}
