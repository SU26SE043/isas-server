using Isas.InterviewService.DTOs;
using Isas.InterviewService.Entities;
using Isas.InterviewService.Enums;
using Isas.InterviewService.Models;
using Isas.InterviewService.Services;
using Isas.InterviewService.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace Isas.InterviewService.Tests;

// INT-17b — mỗi CÂU GỐC có chuỗi đào sâu riêng (tối đa `MaxDeepPerQuestion` tầng), XEN KẼ ngay sau nó,
// thay cho mô hình cũ "trả lời hết mọi câu (frontier) rồi mới đào sâu, ngân sách tính theo buổi".
//
// `MaxDeepPerQuestion = 0` = kill-switch → chạy nguyên đường cũ (khoá bằng test ở cuối file).
public class AdaptiveChainDepthInt17bTests
{
    private static AnswerService BuildAdaptive(
        TestDb t, Mock<IAiServiceInterviewDecider> decider, int maxFailuresPerSession = 3)
    {
        var publisher = new Mock<IScoringJobPublisher>();
        publisher
            .Setup(p => p.PublishAsync(It.IsAny<ScoringJob>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var storage = new Mock<IStorageService>();
        storage
            .Setup(s => s.UploadAsync(
                It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<Guid>(),
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("answer-audio/seed.webm");

        return new AnswerService(
            t.Db, storage.Object, publisher.Object,
            new Mock<ISessionScoringNotifier>().Object, TestDb.ScoringOpts(),
            NullLogger<AnswerService>.Instance, decider.Object,
            Options.Create(new AdaptiveOptions { MaxFailuresPerSession = maxFailuresPerSession }));
    }

    private static Mock<IAiServiceInterviewDecider> Decider(DecideNextResult result)
    {
        var d = new Mock<IAiServiceInterviewDecider>();
        d.Setup(x => x.DecideNextAsync(
                It.IsAny<AdaptiveDecisionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(result);
        return d;
    }

    private static Mock<IAiServiceInterviewDecider> ThrowingDecider()
    {
        var d = new Mock<IAiServiceInterviewDecider>();
        d.Setup(x => x.DecideNextAsync(
                It.IsAny<AdaptiveDecisionRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AiServiceException("AIService sập"));
        return d;
    }

    // Buổi chế độ CHUỖI. maxQuestions rộng để không vô tình bó trước trần độ sâu.
    private static PracticeSession ChainSession(
        Guid candidate, Guid? campaignId = null, int maxDeep = 3, int maxQuestions = 20)
    {
        var s = TestDb.Session(candidate, SessionStatus.Ready, campaignId: campaignId);
        s.AdaptiveEnabled = true;
        s.MaxQuestions = maxQuestions;
        s.MaxFollowUps = 0;             // trần buổi TẮT — nếu để 3 nó bó chặt hơn trần theo câu (xem AdaptiveOptions)
        s.MaxDeepPerQuestion = maxDeep;
        return s;
    }

    private static PracticeQuestion Seed(Guid sessionId, int orderNo, string content = "Câu gốc")
        => new()
        {
            Id = Guid.NewGuid(),
            SessionId = sessionId,
            OrderNo = orderNo,
            Content = content,
            TimeLimitSec = 120,
            Kind = QuestionKind.Seed,
            Depth = 0,
            RootQuestionId = null
        };

    private static async Task<UploadAnswerResult> UploadAsync(AnswerService svc, Guid sessionId, Guid questionId, Guid candidate)
    {
        using var audio = new MemoryStream(new byte[] { 1 });
        return await svc.UploadAnswerAsync(sessionId, questionId, candidate, audio, "audio/webm", 30);
    }

    // ── Câu đào sâu chèn ĐÚNG khoảng trống ngay sau câu cha, KHÔNG rơi xuống cuối danh sách ─────────
    // Đây là điều kiện đủ để cả FE B2C (dùng thứ tự mảng BE) lẫn FE B2B (tự sắp theo orderNo) hiện đúng
    // thứ tự hội thoại mà không phải sửa gì.
    [Fact]
    public async Task DaoSau_ChenNgaySauCauCha_KhongRoiXuongCuoi()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var session = ChainSession(candidate);
        // Câu gốc đánh số có khoảng trống (stride = 1 + maxDeep = 4): 1, 5, 9.
        var q1 = Seed(session.Id, 1, "Gốc 1");
        var q2 = Seed(session.Id, 5, "Gốc 2");
        var q3 = Seed(session.Id, 9, "Gốc 3");
        t.Db.AddRange(session, q1, q2, q3, TestDb.Criterion(session.JobCategory));
        await t.Db.SaveChangesAsync();

        var svc = BuildAdaptive(t, Decider(new DecideNextResult("follow_up", "Đào sâu gốc 1", "ts", null)));
        var res = await UploadAsync(svc, session.Id, q1.Id, candidate);

        var qs = await t.Db.PracticeQuestions.AsNoTracking()
            .Where(x => x.SessionId == session.Id).OrderBy(x => x.OrderNo).ToListAsync();

        // Sắp theo OrderNo ra đúng thứ tự hội thoại: Gốc 1 → câu đào sâu của nó → Gốc 2 → Gốc 3.
        Assert.Equal(
            new[] { "Gốc 1", "Đào sâu gốc 1", "Gốc 2", "Gốc 3" },
            qs.Select(x => x.Content).ToArray());

        var child = qs[1];
        Assert.Equal(2, child.OrderNo);            // = OrderNo cha + 1, nằm trong khoảng trống đã chừa
        Assert.Equal(1, child.Depth);
        Assert.Equal(q1.Id, child.RootQuestionId);
        Assert.Equal(res.AnswerId, child.GeneratedFromAnswerId);
        Assert.False(res.InterviewComplete);       // còn Gốc 2, Gốc 3 chưa trả lời
    }

    // ── Chuỗi mọc tới đúng trần rồi DỪNG, không gọi AI thêm ─────────────────────────────────────────
    [Fact]
    public async Task Chuoi_MocToiTran_RoiDung_KhongGoiAiThem()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var session = ChainSession(candidate, maxDeep: 3);
        var root = Seed(session.Id, 1);
        t.Db.AddRange(session, root, TestDb.Criterion(session.JobCategory));
        await t.Db.SaveChangesAsync();

        var decider = Decider(new DecideNextResult("follow_up", "sâu hơn nữa", "ts", null));
        var svc = BuildAdaptive(t, decider);

        // Trả lời câu gốc rồi trả lời lần lượt từng câu đào sâu → chuỗi dài dần.
        var current = root.Id;
        for (var i = 0; i < 3; i++)
        {
            await UploadAsync(svc, session.Id, current, candidate);
            current = (await t.Db.PracticeQuestions.AsNoTracking()
                .Where(x => x.SessionId == session.Id)
                .OrderByDescending(x => x.Depth).FirstAsync()).Id;
        }

        var all = await t.Db.PracticeQuestions.AsNoTracking()
            .Where(x => x.SessionId == session.Id).OrderBy(x => x.OrderNo).ToListAsync();
        Assert.Equal(4, all.Count);                                  // 1 gốc + 3 tầng
        Assert.Equal(new[] { 0, 1, 2, 3 }, all.Select(x => x.Depth).ToArray());
        Assert.Equal(new[] { 1, 2, 3, 4 }, all.Select(x => x.OrderNo).ToArray());
        Assert.All(all.Skip(1), x => Assert.Equal(root.Id, x.RootQuestionId));

        // Trả lời câu ở tầng cuối → chạm trần: KHÔNG gọi AI nữa, và vì hết câu chưa trả lời nên buổi xong.
        decider.Invocations.Clear();
        var last = await UploadAsync(svc, session.Id, current, candidate);
        decider.Verify(x => x.DecideNextAsync(
            It.IsAny<AdaptiveDecisionRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Equal(4, await t.Db.PracticeQuestions.CountAsync(x => x.SessionId == session.Id));
        Assert.True(last.InterviewComplete);
    }

    // ── Trả lời LỆCH THỨ TỰ vẫn mọc 2 chuỗi ĐỘC LẬP (BE không ép tuần tự) ───────────────────────────
    [Fact]
    public async Task TraLoiLechThuTu_MocHaiChuoiDocLap()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var session = ChainSession(candidate);
        var q1 = Seed(session.Id, 1, "Gốc 1");
        var q2 = Seed(session.Id, 5, "Gốc 2");
        t.Db.AddRange(session, q1, q2, TestDb.Criterion(session.JobCategory));
        await t.Db.SaveChangesAsync();

        var svc = BuildAdaptive(t, Decider(new DecideNextResult("clarify", "làm rõ", "ts", null)));

        // Trả lời câu GỐC 2 trước, rồi mới tới GỐC 1.
        await UploadAsync(svc, session.Id, q2.Id, candidate);
        await UploadAsync(svc, session.Id, q1.Id, candidate);

        var children = await t.Db.PracticeQuestions.AsNoTracking()
            .Where(x => x.SessionId == session.Id && x.Depth > 0)
            .OrderBy(x => x.OrderNo).ToListAsync();

        Assert.Equal(2, children.Count);
        Assert.Equal(q1.Id, children[0].RootQuestionId);   // chuỗi của gốc 1 nằm ở khe 2
        Assert.Equal(2, children[0].OrderNo);
        Assert.Equal(q2.Id, children[1].RootQuestionId);   // chuỗi của gốc 2 nằm ở khe 6
        Assert.Equal(6, children[1].OrderNo);
    }

    // ── Hết chuỗi mà CÒN câu gốc chưa trả lời → KHÔNG báo hoàn tất, và KHÔNG trả action "end" ───────
    // Lý do phần action: FE ánh xạ `end` → "AI đã hỏi xong — bạn có thể nộp bài." Báo end lúc còn 2 câu
    // gốc chưa làm là giục ứng viên nộp bài giữa chừng ⇒ mất 1 credit cho buổi làm dở.
    [Fact]
    public async Task HetChuoi_ConCauGocChuaTraLoi_KhongBaoHoanTat()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var session = ChainSession(candidate);
        var q1 = Seed(session.Id, 1, "Gốc 1");
        var q2 = Seed(session.Id, 5, "Gốc 2");
        t.Db.AddRange(session, q1, q2, TestDb.Criterion(session.JobCategory));
        await t.Db.SaveChangesAsync();

        var svc = BuildAdaptive(t, Decider(new DecideNextResult("end", null, "ts", "chủ đề đã đủ")));
        var res = await UploadAsync(svc, session.Id, q1.Id, candidate);

        Assert.False(res.InterviewComplete);
        Assert.Null(res.NextAction);      // KHÔNG phải "end" — xem lý do ở chú thích trên
        Assert.Null(res.NextQuestion);
        Assert.Equal(2, await t.Db.PracticeQuestions.CountAsync(x => x.SessionId == session.Id));
    }

    // ── Câu gốc CUỐI hết chuỗi → mới báo hoàn tất (kèm action "end" để FE mời nộp bài) ──────────────
    [Fact]
    public async Task HetChuoi_CauGocCuoiCung_BaoHoanTat()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var session = ChainSession(candidate);
        var only = Seed(session.Id, 1);
        t.Db.AddRange(session, only, TestDb.Criterion(session.JobCategory));
        await t.Db.SaveChangesAsync();

        var svc = BuildAdaptive(t, Decider(new DecideNextResult("end", null, "ts", null)));
        var res = await UploadAsync(svc, session.Id, only.Id, candidate);

        Assert.True(res.InterviewComplete);
        Assert.Equal("end", res.NextAction);
    }

    // ── `new_question` KHÔNG append ở chế độ chuỗi (chủ đề mới đã có sẵn trong danh sách câu gốc) ───
    [Fact]
    public async Task NewQuestion_ChangAppend_OChoDoChuoi()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var session = ChainSession(candidate);
        var q1 = Seed(session.Id, 1, "Gốc 1");
        var q2 = Seed(session.Id, 5, "Gốc 2");
        t.Db.AddRange(session, q1, q2, TestDb.Criterion(session.JobCategory));
        await t.Db.SaveChangesAsync();

        var svc = BuildAdaptive(t, Decider(new DecideNextResult("new_question", "Chủ đề khác hẳn?", "ts", null)));
        await UploadAsync(svc, session.Id, q1.Id, candidate);

        // Không sinh câu nào — câu "đổi chủ đề" mà nằm trong chuỗi của Gốc 1 là sai ngữ nghĩa.
        Assert.Equal(2, await t.Db.PracticeQuestions.CountAsync(x => x.SessionId == session.Id));
    }

    // ── Ngữ cảnh gửi cho AI: câu gốc làm mỏ neo + các chủ đề khác + đúng tầng hiện tại ──────────────
    [Fact]
    public async Task GuiChoAi_KemCauGoc_ChuDeKhac_VaDoSau()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var session = ChainSession(candidate, maxDeep: 3);
        var q1 = Seed(session.Id, 1, "Gốc 1");
        var q2 = Seed(session.Id, 5, "Gốc 2");
        // Câu đã đào sâu 1 tầng từ Gốc 1 — trả lời chính nó thì AI phải thấy currentDepth = 1.
        var child = new PracticeQuestion
        {
            Id = Guid.NewGuid(), SessionId = session.Id, OrderNo = 2, Content = "Sâu 1",
            TimeLimitSec = 120, Kind = QuestionKind.FollowUp, Depth = 1, RootQuestionId = q1.Id
        };
        t.Db.AddRange(session, q1, q2, child, TestDb.Criterion(session.JobCategory));
        await t.Db.SaveChangesAsync();

        AdaptiveDecisionRequest? captured = null;
        var decider = new Mock<IAiServiceInterviewDecider>();
        decider.Setup(x => x.DecideNextAsync(
                It.IsAny<AdaptiveDecisionRequest>(), It.IsAny<CancellationToken>()))
            .Callback<AdaptiveDecisionRequest, CancellationToken>((r, _) => captured = r)
            .ReturnsAsync(new DecideNextResult("end", null, "ts", null));

        await UploadAsync(BuildAdaptive(t, decider), session.Id, child.Id, candidate);

        Assert.NotNull(captured);
        Assert.Equal("Gốc 1", captured!.RootQuestion);          // mỏ neo chủ đề
        Assert.Equal(1, captured.CurrentDepth);
        Assert.Equal(3, captured.MaxDepth);
        Assert.Equal(["Gốc 2"], captured.OtherTopics);          // chủ đề khác, để AI không hỏi trùng
        // Lịch sử chỉ gồm CHUỖI hiện tại (câu gốc), KHÔNG kéo theo Gốc 2 của chủ đề khác.
        Assert.Equal(["Gốc 1"], captured.History.Select(h => h.Question).ToArray());
    }

    // ── AIService hỏng liên tục → sau `MaxFailuresPerSession` lần thì THÔI gọi (chống chờ chết) ─────
    [Fact]
    public async Task AiHongLienTuc_ChamTranLoi_ThoiGoiDecider()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var session = ChainSession(candidate);
        var seeds = Enumerable.Range(0, 5).Select(i => Seed(session.Id, i * 4 + 1, $"Gốc {i}")).ToList();
        t.Db.AddRange(session);
        t.Db.AddRange(seeds);
        t.Db.Add(TestDb.Criterion(session.JobCategory));
        await t.Db.SaveChangesAsync();

        var decider = ThrowingDecider();
        var svc = BuildAdaptive(t, decider, maxFailuresPerSession: 2);

        // 2 lần đầu vẫn gọi (và lỗi) → bộ đếm chạm trần; lần 3 và 4 không gọi nữa.
        for (var i = 0; i < 4; i++)
            await UploadAsync(svc, session.Id, seeds[i].Id, candidate);

        decider.Verify(x => x.DecideNextAsync(
            It.IsAny<AdaptiveDecisionRequest>(), It.IsAny<CancellationToken>()), Times.Exactly(2));

        var s = await t.Db.PracticeSessions.AsNoTracking().FirstAsync(x => x.Id == session.Id);
        Assert.Equal(2, s.AdaptiveFailures);
        // Degrade sạch: upload vẫn thành công, answer vẫn lưu đủ.
        Assert.Equal(4, await t.Db.PracticeAnswers.CountAsync(a => a.SessionId == session.Id));
    }

    // ── Re-upload câu đã "đẻ" con → KHÔNG gọi lại decider, KHÔNG sinh trùng ────────────────────────
    // Ở chế độ cũ việc này do frontier gánh; bỏ frontier rồi thì `generated_from_answer_id` là chốt duy nhất.
    [Fact]
    public async Task ReUpload_CauDaCoCon_KhongGoiLai_KhongSinhTrung()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var session = ChainSession(candidate);
        var q1 = Seed(session.Id, 1, "Gốc 1");
        var q2 = Seed(session.Id, 5, "Gốc 2");
        t.Db.AddRange(session, q1, q2, TestDb.Criterion(session.JobCategory));
        await t.Db.SaveChangesAsync();

        var decider = Decider(new DecideNextResult("follow_up", "sâu hơn", "ts", null));
        var svc = BuildAdaptive(t, decider);

        await UploadAsync(svc, session.Id, q1.Id, candidate);
        decider.Invocations.Clear();
        await UploadAsync(svc, session.Id, q1.Id, candidate);   // ghi đè cùng answer (INT-3)

        decider.Verify(x => x.DecideNextAsync(
            It.IsAny<AdaptiveDecisionRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Equal(3, await t.Db.PracticeQuestions.CountAsync(x => x.SessionId == session.Id));
    }

    // ── KILL-SWITCH: MaxDeepPerQuestion = 0 → chạy nguyên đường CŨ (frontier theo buổi) ─────────────
    [Fact]
    public async Task KillSwitch_MaxDeepBang0_ChayDuongCu_Frontier()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var session = ChainSession(candidate, maxDeep: 0);
        session.MaxFollowUps = 3;                     // ngân sách theo buổi như chế độ cũ
        var q1 = Seed(session.Id, 1, "Gốc 1");
        var q2 = Seed(session.Id, 2, "Gốc 2");        // chế độ cũ đánh số liền nhau (stride = 1)
        t.Db.AddRange(session, q1, q2, TestDb.Criterion(session.JobCategory));
        await t.Db.SaveChangesAsync();

        var decider = Decider(new DecideNextResult("follow_up", "sâu hơn", "ts", null));
        var svc = BuildAdaptive(t, decider);

        // Chưa trả lời hết → frontier chặn: không gọi AI, không append.
        await UploadAsync(svc, session.Id, q1.Id, candidate);
        decider.Verify(x => x.DecideNextAsync(
            It.IsAny<AdaptiveDecisionRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Equal(2, await t.Db.PracticeQuestions.CountAsync(x => x.SessionId == session.Id));

        // Trả lời nốt câu còn lại → tới frontier: gọi AI và append ở ĐUÔI (max + 1).
        await UploadAsync(svc, session.Id, q2.Id, candidate);
        decider.Verify(x => x.DecideNextAsync(
            It.IsAny<AdaptiveDecisionRequest>(), It.IsAny<CancellationToken>()), Times.Once);
        var appended = await t.Db.PracticeQuestions.AsNoTracking()
            .Where(x => x.SessionId == session.Id).OrderByDescending(x => x.OrderNo).FirstAsync();
        Assert.Equal(3, appended.OrderNo);
        Assert.Null(appended.RootQuestionId);    // chế độ cũ không nối chuỗi
    }
    // ── Chấm-theo-phạm-vi: câu đào sâu thừa kế nhãn tiêu chí của câu cha ──────────────────────
    //
    // Vì sao cần: `/decide-next` KHÔNG trả nhãn tiêu chí. Không thừa kế thì mọi câu đào sâu có
    // `TargetCriterionIds = null` ⇒ chấm cả rubric. Prod chạy chế độ chuỗi nên phần lớn câu trong
    // một buổi là câu đào sâu ⇒ tính năng chấm-theo-phạm-vi gần như KHÔNG có hiệu lực.

    /// `follow_up` đào sâu vào chính câu trả lời vừa rồi ⇒ vẫn là chủ đề của câu cha ⇒ thừa kế ĐÚNG.
    [Fact]
    public async Task DaoSau_FollowUp_ThuaKeNhanTieuChiCuaCauCha()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var session = ChainSession(candidate);
        var nhan = new List<Guid> { Guid.NewGuid(), Guid.NewGuid() };
        var root = Seed(session.Id, 1);
        root.TargetCriterionIds = nhan;
        t.Db.AddRange(session, root, TestDb.Criterion(session.JobCategory));
        await t.Db.SaveChangesAsync();

        var svc = BuildAdaptive(t, Decider(new DecideNextResult("follow_up", "Đào sâu", "ts", null)));
        await UploadAsync(svc, session.Id, root.Id, candidate);

        var con = await t.Db.PracticeQuestions.AsNoTracking()
            .SingleAsync(q => q.SessionId == session.Id && q.Kind == QuestionKind.FollowUp);
        Assert.Equal(nhan, con.TargetCriterionIds);
    }

    /// `clarify` cũng bám chính câu trả lời đó ⇒ thừa kế.
    [Fact]
    public async Task DaoSau_Clarify_CungThuaKeNhan()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var session = ChainSession(candidate);
        var nhan = new List<Guid> { Guid.NewGuid() };
        var root = Seed(session.Id, 1);
        root.TargetCriterionIds = nhan;
        t.Db.AddRange(session, root, TestDb.Criterion(session.JobCategory));
        await t.Db.SaveChangesAsync();

        var svc = BuildAdaptive(t, Decider(new DecideNextResult("clarify", "Làm rõ", "ts", null)));
        await UploadAsync(svc, session.Id, root.Id, candidate);

        var con = await t.Db.PracticeQuestions.AsNoTracking()
            .SingleAsync(q => q.SessionId == session.Id && q.Kind == QuestionKind.Clarify);
        Assert.Equal(nhan, con.TargetCriterionIds);
    }

    /// Câu cha KHÔNG có nhãn (buổi cũ / AIService fail-open) → con cũng null = chấm đủ rubric.
    /// `null` phải giữ nghĩa "không biết", KHÔNG được biến thành `[]` (= "không nhắm tiêu chí nào").
    [Fact]
    public async Task DaoSau_ChaKhongCoNhan_ConVanNull_KhongPhaiMangRong()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var session = ChainSession(candidate);
        var root = Seed(session.Id, 1);   // TargetCriterionIds = null
        t.Db.AddRange(session, root, TestDb.Criterion(session.JobCategory));
        await t.Db.SaveChangesAsync();

        var svc = BuildAdaptive(t, Decider(new DecideNextResult("follow_up", "Đào sâu", "ts", null)));
        await UploadAsync(svc, session.Id, root.Id, candidate);

        var con = await t.Db.PracticeQuestions.AsNoTracking()
            .SingleAsync(q => q.SessionId == session.Id && q.Kind == QuestionKind.FollowUp);
        Assert.Null(con.TargetCriterionIds);
    }

    /// Câu cha nhắm 0 tiêu chí nội dung (`[]`, ví dụ "giới thiệu bản thân") → con giữ `[]`, KHÔNG
    /// được rơi về null: hai giá trị này mang nghĩa khác hẳn nhau ở tầng chấm.
    [Fact]
    public async Task DaoSau_ChaCoNhanRong_ConGiuMangRong_KhongRoiVeNull()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var session = ChainSession(candidate);
        var root = Seed(session.Id, 1);
        root.TargetCriterionIds = new List<Guid>();
        t.Db.AddRange(session, root, TestDb.Criterion(session.JobCategory));
        await t.Db.SaveChangesAsync();

        var svc = BuildAdaptive(t, Decider(new DecideNextResult("follow_up", "Đào sâu", "ts", null)));
        await UploadAsync(svc, session.Id, root.Id, candidate);

        var con = await t.Db.PracticeQuestions.AsNoTracking()
            .SingleAsync(q => q.SessionId == session.Id && q.Kind == QuestionKind.FollowUp);
        Assert.NotNull(con.TargetCriterionIds);
        Assert.Empty(con.TargetCriterionIds!);
    }

    /// CHẾ ĐỘ FRONTIER (`MaxDeepPerQuestion = 0`, kill-switch INT-17b): `new_question` KHÔNG bị chặn
    /// nên nó tới được chỗ append — và đó là câu ĐỔI CHỦ ĐỀ, thừa kế nhãn của câu cha sẽ chấm nhầm
    /// tiêu chí. Ca này là lý do tồn tại của vế `is "follow_up" or "clarify"`; thiếu test này thì
    /// mutation "thừa kế cả new_question" đi qua sạch (đã đo).
    [Fact]
    public async Task Frontier_NewQuestion_KHONG_ThuaKeNhan_ViDaDoiChuDe()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var session = ChainSession(candidate, maxDeep: 0);   // 0 = frontier, không phải chế độ chuỗi
        var root = Seed(session.Id, 1);
        root.TargetCriterionIds = new List<Guid> { Guid.NewGuid() };
        t.Db.AddRange(session, root, TestDb.Criterion(session.JobCategory));
        await t.Db.SaveChangesAsync();

        var svc = BuildAdaptive(t, Decider(new DecideNextResult("new_question", "Chủ đề khác hẳn", "ts", null)));
        await UploadAsync(svc, session.Id, root.Id, candidate);

        var con = await t.Db.PracticeQuestions.AsNoTracking()
            .SingleAsync(q => q.SessionId == session.Id && q.Kind == QuestionKind.NewQuestion);
        Assert.Null(con.TargetCriterionIds);
    }

}
