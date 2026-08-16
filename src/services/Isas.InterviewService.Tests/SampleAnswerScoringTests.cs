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

/// <summary>
/// Đáp án mẫu HR soạn (B2B) — snapshot xuống buổi thi rồi đi vào lượt chấm.
///
/// <para>Vì sao SNAPSHOT chứ không đọc live từ CampaignService lúc chấm: đáp án là một phần THƯỚC ĐO.
/// Đọc live thì hai ứng viên cùng chiến dịch có thể bị chấm theo hai bản đáp án khác nhau nếu ai đó
/// sửa ở giữa, trong khi điểm vẫn đem xếp hạng chung (CAMP-10). Snapshot cũng giữ cho việc chấm không
/// phụ thuộc một service khác còn sống hay không.</para>
///
/// Khoá các hành vi:
/// (a) đáp án chép xuống practice_questions theo ĐÚNG chỉ số câu;
/// (b) bản Campaign cũ không gửi gì → mọi câu null, không vỡ (cửa sổ deploy);
/// (c) buổi thi chấp nhận TRỘN câu có / không có đáp án (câu đào sâu AI sinh lúc thi không ai soạn);
/// (d) kill-switch mặc định BẬT.
/// </summary>
public class SampleAnswerScoringTests
{
    private static PracticeService Build(TestDb t)
    {
        var notifier = new Mock<ISessionScoringNotifier>();
        notifier.Setup(n => n.NotifySessionScoredAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var reservation = new Mock<ICreditReservationClient>();
        reservation.Setup(r => r.ReserveAsync(
                It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CreditReservationResult(Guid.NewGuid(), 1));

        return new PracticeService(t.Db, new Mock<IStorageService>().Object,
            new Mock<IAiServiceQuestionGenerator>().Object,
            notifier.Object, reservation.Object,
            NullLogger<PracticeService>.Instance,
            capacityOptions: Options.Create(new CapacityOptions { MaxConcurrentSessions = 0 }));
    }

    private static CreateCampaignSessionRequest Request(
        Guid campaignId,
        IReadOnlyList<string> questions,
        IReadOnlyList<CampaignQuestionInput>? details = null)
        => new(campaignId, Guid.NewGuid(), JobCategory.BE, questions,
            new[] { new CampaignCriterionInput("Chiều sâu kỹ thuật", "Hiểu bản chất", 1.0m, 5) },
            QuestionDetails: details);

    // ───────────────── (a) snapshot đúng chỉ số ─────────────────

    [Fact]
    public async Task Tao_session_B2B_thi_chep_dap_an_mau_theo_dung_chi_so_cau()
    {
        using var t = new TestDb();
        var campaignId = Guid.NewGuid();

        var res = await Build(t).CreateCampaignSessionAsync(Guid.NewGuid(), Request(
            campaignId,
            new[] { "Câu 1", "Câu 2", "Câu 3" },
            new[]
            {
                new CampaignQuestionInput("Câu 1", "Đáp án 1"),
                new CampaignQuestionInput("Câu 2", null),
                new CampaignQuestionInput("Câu 3", "Đáp án 3"),
            }));

        await using var read = t.NewContext();
        var rows = await read.PracticeQuestions.AsNoTracking()
            .Where(q => q.SessionId == res.Id).OrderBy(q => q.OrderNo).ToListAsync();

        Assert.Equal(3, rows.Count);
        Assert.Equal("Đáp án 1", rows[0].SampleAnswer);
        Assert.Null(rows[1].SampleAnswer);       // HR chưa soạn cho câu này
        Assert.Equal("Đáp án 3", rows[2].SampleAnswer);
    }

    // ───────────────── (b) bản Campaign cũ không gửi gì ─────────────────

    [Fact]
    public async Task Khong_gui_questionDetails_thi_moi_cau_null_va_khong_vo()
    {
        // Cửa sổ deploy: hai service không khởi động lại cùng lúc, nên bản Campaign cũ — chưa biết
        // field này — vẫn phải tạo được session, chỉ là không có đáp án mẫu.
        using var t = new TestDb();

        var res = await Build(t).CreateCampaignSessionAsync(
            Guid.NewGuid(), Request(Guid.NewGuid(), new[] { "Câu 1", "Câu 2" }));

        await using var read = t.NewContext();
        var rows = await read.PracticeQuestions.AsNoTracking()
            .Where(q => q.SessionId == res.Id).ToListAsync();

        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Null(r.SampleAnswer));
    }

    // ───────────────── (c) trộn có / không ─────────────────

    [Fact]
    public async Task Buoi_thi_chap_nhan_tron_cau_co_va_khong_co_dap_an()
    {
        // Câu ĐÀO SÂU do AI sinh lúc thi không ai soạn đáp án trước — đó là lý do prompt phải nói rõ
        // đáp án mẫu là "MỘT đáp án tốt", không phải đáp án duy nhất đúng: nếu không, câu có đáp án
        // bị chấm gắt hơn câu không có, ngay trong cùng một bài.
        using var t = new TestDb();

        var res = await Build(t).CreateCampaignSessionAsync(Guid.NewGuid(), Request(
            Guid.NewGuid(),
            new[] { "Câu 1", "Câu 2" },
            new[]
            {
                new CampaignQuestionInput("Câu 1", "Đáp án 1"),
                new CampaignQuestionInput("Câu 2", null),
            }));

        await using var read = t.NewContext();
        var rows = await read.PracticeQuestions.AsNoTracking()
            .Where(q => q.SessionId == res.Id).ToListAsync();

        Assert.Contains(rows, r => r.SampleAnswer is not null);
        Assert.Contains(rows, r => r.SampleAnswer is null);
    }

    // ───────────────── (d) kill-switch ─────────────────

    [Fact]
    public void Kill_switch_dap_an_mau_mac_dinh_BAT()
    {
        // Khác tiền lệ Grounding/Tiering/CvScreening (đều mặc định tắt): những cái đó là tính năng mới
        // bật thăm dò, còn đây là dữ liệu HR CHỦ ĐỘNG soạn với mục đích duy nhất là để AI chấm theo.
        // Mặc định tắt thì HR nhập đáp án xong tính năng im lặng vô hiệu.
        Assert.True(new ScoringOptions().UseSampleAnswer);
    }

    // ───────────────── (e) đáp án THẬT SỰ đi vào lượt chấm ─────────────────
    //
    // Bốn test dưới đây tồn tại vì một phép mutation: gỡ hẳn `_scoring.UseSampleAnswer ?` khỏi
    // AnswerService mà bộ test vẫn XANH — tức kill-switch là thứ ta TƯỞNG mình có. Kiểm giá trị mặc
    // định của cờ (test ngay trên) không chứng minh được cờ đó chặn được gì.

    private static (AnswerService svc, Mock<IScoringJobPublisher> pub) Answering(
        TestDb t, bool useSampleAnswer)
    {
        var storage = new Mock<IStorageService>();
        storage.Setup(s => s.UploadAsync(
                It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<Guid>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("answer-audio/x.webm");

        var pub = new Mock<IScoringJobPublisher>();
        var svc = new AnswerService(
            t.Db, storage.Object, pub.Object, new Mock<ISessionScoringNotifier>().Object,
            Options.Create(new ScoringOptions { UseSampleAnswer = useSampleAnswer }),
            NullLogger<AnswerService>.Instance);
        return (svc, pub);
    }

    private static async Task<ScoringJob> UploadAndCapture(bool useSampleAnswer, string? sampleAnswer)
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var session = TestDb.Session(candidate, SessionStatus.Ready, JobCategory.BE);
        var question = TestDb.Question(session.Id);
        question.SampleAnswer = sampleAnswer;

        t.Db.AddRange(session, question);
        t.Db.RubricCriteria.Add(new RubricCriterion
        {
            Id = Guid.NewGuid(), Name = "Chiều sâu kỹ thuật", MaxScore = 5, Weight = 1.0m,
            IsActive = true, JobCategory = JobCategory.BE, Language = "vi", Version = 1,
        });
        t.Db.SaveChanges();

        var (svc, pub) = Answering(t, useSampleAnswer);
        ScoringJob? captured = null;
        pub.Setup(p => p.PublishAsync(It.IsAny<ScoringJob>(), It.IsAny<CancellationToken>()))
            .Callback<ScoringJob, CancellationToken>((j, _) => captured = j)
            .Returns(Task.CompletedTask);

        await svc.UploadAnswerAsync(
            session.Id, question.Id, candidate, new MemoryStream([1, 2, 3]), "audio/webm", 30);

        Assert.NotNull(captured);
        return captured!;
    }

    [Fact]
    public async Task Dap_an_mau_di_vao_job_cham_khi_kill_switch_BAT()
        => Assert.Equal("Đáp án chuẩn của công ty", (await UploadAndCapture(true, "Đáp án chuẩn của công ty")).SampleAnswer);

    [Fact]
    public async Task Kill_switch_TAT_thi_job_KHONG_mang_dap_an_du_cau_hoi_co()
    {
        // Đây mới là phép chứng minh kill-switch có tác dụng. Tắt cờ phải quay về ĐÚNG cách chấm
        // trước tính năng này, không cần deploy — vì đây là thay đổi THƯỚC ĐO mà chưa ai đo được
        // nó làm điểm lên hay xuống.
        var job = await UploadAndCapture(false, "Đáp án chuẩn của công ty");

        Assert.Null(job.SampleAnswer);
    }

    [Fact]
    public async Task Cau_khong_co_dap_an_thi_job_cung_khong_mang_gi()
        => Assert.Null((await UploadAndCapture(true, null)).SampleAnswer);
}
