using Isas.InterviewService.DTOs;
using Isas.InterviewService.Entities;
using Isas.InterviewService.Enums;
using Isas.InterviewService.Models;
using Isas.InterviewService.Services;
using Isas.InterviewService.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Isas.InterviewService.Tests;

/// <summary>
/// ADP1 (BE-3) — CON DẤU CÁCH GỘP ĐIỂM (<c>practice_sessions.score_aggregation_version</c>).
///
/// <para><b>Vì sao con dấu tồn tại:</b> BE-2 đổi phép gộp từ "mỗi answer một phiếu" sang "mỗi CÂU GỐC
/// một phiếu". Hai cách cho ra HAI THANG KHÔNG SO SÁNH ĐƯỢC — đo thật trên cùng một buổi (chuỗi 4
/// answer @4đ + một gốc trần @1đ): cũ <b>34.00</b>, mới <b>25.00</b>. Mà CAMP-10 xếp hạng bằng cách so
/// điểm THẲNG giữa các ứng viên trong cùng campaign, còn BC15/F14 so điểm qua thời gian ⇒ một chiến
/// dịch đang tuyển vắt qua lần deploy này có ứng viên ở cả hai thang nằm chung MỘT bảng.</para>
///
/// <para><b>Lỗ mà bộ test này bịt (agent CODE tự khai):</b> sợi dây được kiểm bằng project throwaway,
/// nhưng phần Campaign dựng event BẰNG TAY ⇒ <b>đường sản xuất ghi con dấu chưa được phủ tự động</b>.
/// Xoá dòng gán trong <c>EnqueueSessionScoredAsync</c> thì Campaign vẫn biên dịch, vẫn chạy, chỉ âm
/// thầm nhận <c>null</c> — đúng hình dạng bug B10 (Interview phát <c>ScoreFallback</c> mà DTO Campaign
/// không khai property ⇒ System.Text.Json bỏ qua khoá lạ ⇒ mất, không lỗi không log).</para>
///
/// <para><b>Hai bất biến TÁCH RỜI, cố ý mỗi cái một <c>[Fact]</c>:</b>
/// (a) con dấu <b>xuống tới DB</b> — đọc lại bằng <c>DbContext</c> MỚI, không đọc từ identity map;
/// (b) con dấu <b>lên tới payload outbox</b> — đọc chuỗi JSON THÔ, không qua deserialize.
/// Gộp hai vế vào một test thì hai lỗi khác hẳn nhau (gán vào bản sao rời · quên gán lên event) chỉ
/// làm đỏ đúng một chỗ và không ai phân biệt được mình vừa hỏng cái nào.</para>
///
/// <para>⚠ <b>Vì sao (a) BẮT BUỘC đọc bằng context mới:</b> nếu ai đổi <c>FindAsync</c> thành
/// <c>AsNoTracking().FirstOrDefault</c> thì phép gán rơi vào một bản SAO RỜI ⇒ DB không có gì ⇒ nhưng
/// assert trên instance đang được theo dõi <b>vẫn xanh</b>. Đọc từ identity map là tự cho mình một
/// phép đo mù.</para>
/// </summary>
public class ScoreAggregationVersionStampAdp1Tests
{
    // Khoá JSON trên dây, viết NGUYÊN VĂN. Interview serialize bằng options mặc định (PascalCase);
    // Campaign deserialize case-insensitive. Đổi tên property một bên mà quên bên kia thì cả hai đầu
    // vẫn tự-nhất-quán và tự-xanh — chỉ chuỗi literal này bắt được. Cặp đối xứng của nó nằm ở
    // Campaign: RankingScoreAggregationVersionAdp1Tests.WireKey.
    private const string WireKey = "ScoreAggregationVersion";

    // ── Hạ tầng seed ────────────────────────────────────────────────────────────

    private static RubricCriterion Crit(Guid? campaignId = null)
    {
        var c = TestDb.Criterion(JobCategory.BE, version: 1, campaignId: campaignId, name: "Chiều sâu kỹ thuật");
        c.MaxScore = 5;
        c.Weight = 1.0m;
        return c;
    }

    private static AnswerScore Score(Guid answerId, Guid criterionId, decimal score = 4m)
        => new()
        {
            Id = Guid.NewGuid(),
            AnswerId = answerId,
            CriterionId = criterionId,
            AttemptNo = 1,
            Score = score,
            Reasoning = "ok",
            RubricVersion = 1,
            CreatedAt = DateTime.UtcNow
        };

    private static PracticeService Practice(TestDb t)
    {
        var credits = new Mock<ICreditReservationClient>();
        credits
            .Setup(r => r.ReserveAsync(
                It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CreditReservationResult(Guid.NewGuid(), 1));

        return new PracticeService(
            t.Db, new Mock<IStorageService>().Object,
            new Mock<IAiServiceQuestionGenerator>().Object,
            TestDb.Notifier(t.Db),          // notifier THẬT — đây chính là thứ đang được đo
            credits.Object,
            NullLogger<PracticeService>.Instance);
    }

    /// <summary>
    /// Buổi sẵn sàng cho <c>SubmitSessionAsync</c>: 1 câu, 1 answer đã <c>Scored</c> + có điểm ⇒ nộp
    /// bài đi thẳng nhánh "đóng-ngay" (mọi answer terminal, scoredCount &gt; 0) ⇒ Status = Scored ⇒
    /// <c>EnqueueSessionScoredAsync</c> chạy rồi caller <c>SaveChanges</c>.
    /// </summary>
    private static (Guid SessionId, Guid Candidate) SeedSubmittable(TestDb t, Guid? campaignId = null)
    {
        var candidate = Guid.NewGuid();
        var session = TestDb.Session(candidate, SessionStatus.InProgress, JobCategory.BE, campaignId: campaignId);
        var crit = Crit(campaignId);
        var q = TestDb.Question(session.Id);
        var a = TestDb.Answer(session.Id, q.Id, AnswerStatus.Scored, DateTime.UtcNow, DateTime.UtcNow);

        t.Db.AddRange(session, crit, q, a);
        t.Db.Add(Score(a.Id, crit.Id));
        t.Db.SaveChanges();
        return (session.Id, candidate);
    }

    // Đọc bản ĐÃ COMMIT — context MỚI, không phải `t.Db` (xem chú thích lớp).
    private static async Task<PracticeSession> ReadFromDbAsync(TestDb t, Guid sessionId)
        => await t.NewContext().PracticeSessions.AsNoTracking().SingleAsync(s => s.Id == sessionId);

    // Chuỗi JSON THÔ của outbox-row — không deserialize: deserialize dùng chính DTO Interview nên nó
    // xanh kể cả khi khoá trên dây đã bị đổi tên (và Campaign thì mất field, im lặng).
    private static async Task<string> ReadPayloadAsync(TestDb t, Guid sessionId)
        => (await t.NewContext().OutboxMessages.AsNoTracking()
                .SingleAsync(m => m.SessionId == sessionId && m.Type == OutboxMessage.SessionScoredType))
            .Payload;

    // ── (1) Đường SẢN XUẤT: nộp bài (PracticeService.SubmitSessionAsync) ─────────
    //
    // Đây là một trong HAI cửa duy nhất đóng buổi sang Scored; cửa kia ở (2). Cả hai gọi
    // EnqueueSessionScoredAsync rồi mới SaveChanges ⇒ con dấu commit CÙNG transaction với state-flip.

    [Fact]
    public async Task NopBai_DongDauXuongDB()
    {
        using var t = new TestDb();
        var (sessionId, candidate) = SeedSubmittable(t);

        await Practice(t).SubmitSessionAsync(candidate, sessionId);

        var saved = await ReadFromDbAsync(t, sessionId);
        Assert.Equal(SessionStatus.Scored, saved.Status);   // tiền đề: đã thật sự đi qua nhánh đóng buổi
        Assert.Equal(2, saved.ScoreAggregationVersion);     // 2 = gộp về CÂU GỐC (ADP1)
    }

    [Fact]
    public async Task NopBai_DongDauLenPayloadOutbox()
    {
        using var t = new TestDb();
        var (sessionId, candidate) = SeedSubmittable(t);

        await Practice(t).SubmitSessionAsync(candidate, sessionId);

        // Khoá cả TÊN KHOÁ lẫn GIÁ TRỊ trên dây, dạng nguyên văn Campaign sẽ đọc được.
        Assert.Contains($"\"{WireKey}\":2", await ReadPayloadAsync(t, sessionId));
    }

    // ── (2) Đường SẢN XUẤT: callback chấm xong (AnswerService.SaveResultAsync) ───
    //
    // Đây là cửa CHÍNH trên production (chấm dần — INT-4), buổi đóng khi answer cuối được chấm chứ
    // không phải khi user bấm nộp. Phủ cả hai cửa mới chứng minh được lời khẳng định "chokepoint duy
    // nhất, không buổi nào lọt" trong chú thích của bản vá.

    [Fact]
    public async Task CallbackChamXong_DongDauXuongDB()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var session = TestDb.Session(candidate, SessionStatus.Scoring, JobCategory.BE);
        var crit = Crit();
        var q = TestDb.Question(session.Id);
        var answer = TestDb.Answer(session.Id, q.Id, AnswerStatus.Scoring, DateTime.UtcNow, DateTime.UtcNow);
        t.Db.AddRange(session, crit, q, answer);
        await t.Db.SaveChangesAsync();

        var svc = new AnswerService(
            t.Db, new Mock<IStorageService>().Object, new Mock<IScoringJobPublisher>().Object,
            TestDb.Notifier(t.Db),          // notifier THẬT
            TestDb.ScoringOpts(), NullLogger<AnswerService>.Instance);

        await svc.SaveResultAsync(answer.Id, new AnswerScoreCallbackRequest
        {
            Transcript = "tôi nghĩ vậy",
            RubricVersion = 1,
            AttemptNo = 1,
            Scores = { new ScoreItemDto { CriterionId = crit.Id, Score = 4m, Reasoning = "ok" } }
        });

        var saved = await ReadFromDbAsync(t, session.Id);
        Assert.Equal(SessionStatus.Scored, saved.Status);
        Assert.Equal(2, saved.ScoreAggregationVersion);
    }

    [Fact]
    public async Task CallbackChamXong_DongDauLenPayloadOutbox()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var session = TestDb.Session(candidate, SessionStatus.Scoring, JobCategory.BE);
        var crit = Crit();
        var q = TestDb.Question(session.Id);
        var answer = TestDb.Answer(session.Id, q.Id, AnswerStatus.Scoring, DateTime.UtcNow, DateTime.UtcNow);
        t.Db.AddRange(session, crit, q, answer);
        await t.Db.SaveChangesAsync();

        var svc = new AnswerService(
            t.Db, new Mock<IStorageService>().Object, new Mock<IScoringJobPublisher>().Object,
            TestDb.Notifier(t.Db), TestDb.ScoringOpts(), NullLogger<AnswerService>.Instance);

        await svc.SaveResultAsync(answer.Id, new AnswerScoreCallbackRequest
        {
            Transcript = "tôi nghĩ vậy",
            RubricVersion = 1,
            AttemptNo = 1,
            Scores = { new ScoreItemDto { CriterionId = crit.Id, Score = 4m, Reasoning = "ok" } }
        });

        Assert.Contains($"\"{WireKey}\":2", await ReadPayloadAsync(t, session.Id));
    }

    // ── (3) Con dấu phải là HẰNG SỐ CỦA THUẬT TOÁN, không phải một số gõ tay ─────

    // Bảo vệ trước đúng hai cách làm con dấu NÓI DỐI, mà con dấu nói dối tệ hơn không có con dấu:
    // (a) đóng bằng số gõ tay ⇒ đổi thuật toán ở CriterionScoreAggregator mà con dấu đứng yên;
    // (b) đổi giá trị CurrentVersion cho "khớp" ⇒ cả hệ tự-nhất-quán mà vẫn sai so với dữ liệu đã ghi.
    [Fact]
    public async Task ConDau_LayTuHangSoCuaThuatToan_ChuKhongPhaiSoGoTay()
    {
        using var t = new TestDb();
        var (sessionId, candidate) = SeedSubmittable(t);

        await Practice(t).SubmitSessionAsync(candidate, sessionId);
        var saved = await ReadFromDbAsync(t, sessionId);

        // (a) con dấu đi theo hằng số của thuật toán
        Assert.Equal(CriterionScoreAggregator.CurrentVersion, saved.ScoreAggregationVersion);
        // (b) và hằng số đó vẫn đúng NGHĨA đã công bố — ghim giá trị tuyệt đối, không suy từ chính nó
        Assert.Equal(2, CriterionScoreAggregator.CurrentVersion);
        Assert.Equal(2, CriterionScoreAggregator.VersionPerRootQuestion);
        Assert.Equal(1, CriterionScoreAggregator.VersionPerAnswer);
        // 1 = "biết chắc là cách CŨ". Code hiện tại không bao giờ được ghi giá trị này: buổi chấm bằng
        // bản này gộp theo câu gốc, đóng dấu 1 là ghi một điều SAI mà tự tin.
        Assert.NotEqual(CriterionScoreAggregator.VersionPerAnswer, saved.ScoreAggregationVersion);
    }

    // Ô số 1 phải giữ nghĩa riêng, khác hẳn null. Nếu ai gộp hai thứ này thì "không biết" và "biết
    // chắc là cách cũ" trở thành một, và BK23 mất chỗ đứng ở đúng cột nó sinh ra để bảo vệ.
    [Fact]
    public void HaiPhienBan_LaHaiGiaTriKhacNhau()
        => Assert.NotEqual(CriterionScoreAggregator.VersionPerAnswer, CriterionScoreAggregator.VersionPerRootQuestion);

    // ── (4) null = KHÔNG BIẾT — không default, không backfill (BK23) ─────────────

    // Buổi CHƯA chấm không được mang dấu. Đây là lý do bản vá cố ý KHÔNG backfill: practice_sessions
    // chứa cả buổi chưa chấm, gán 1 cho chúng là khẳng định một điều chưa từng xảy ra.
    [Fact]
    public async Task BuoiChuaCham_KhongCoConDau()
    {
        using var t = new TestDb();
        var session = TestDb.Session(Guid.NewGuid(), SessionStatus.InProgress, JobCategory.BE);
        t.Db.Add(session);
        await t.Db.SaveChangesAsync();

        Assert.Null((await ReadFromDbAsync(t, session.Id)).ScoreAggregationVersion);
    }

    // Buổi bỏ ngang (SessionAbandoned) cũng không có điểm ⇒ không có gì để dán nhãn thước đo.
    [Fact]
    public async Task BuoiBoNgang_KhongCoConDau()
    {
        using var t = new TestDb();
        var session = TestDb.Session(Guid.NewGuid(), SessionStatus.Scoring, JobCategory.BE);
        t.Db.Add(session);
        await t.Db.SaveChangesAsync();

        await TestDb.Notifier(t.Db).EnqueueSessionAbandonedAsync(session.Id, "no_scored_answer");
        await t.Db.SaveChangesAsync();

        Assert.Null((await ReadFromDbAsync(t, session.Id)).ScoreAggregationVersion);
    }

    // ── (5) B2B — nơi con dấu thật sự được tiêu thụ ─────────────────────────────

    // Buổi B2B là ca duy nhất con dấu đi tiếp sang Campaign (B2C không có campaign_rankings). Phủ
    // riêng vì hai nhánh tính điểm B2C/B2B khác nhau, và vì đây mới là chỗ CAMP-10 trộn hai thang.
    [Fact]
    public async Task BuoiB2B_DongDauCaXuongDBLanLenPayload()
    {
        using var t = new TestDb();
        var campaignId = Guid.NewGuid();
        var (sessionId, candidate) = SeedSubmittable(t, campaignId);

        await Practice(t).SubmitSessionAsync(candidate, sessionId);

        Assert.Equal(2, (await ReadFromDbAsync(t, sessionId)).ScoreAggregationVersion);

        var payload = await ReadPayloadAsync(t, sessionId);
        Assert.Contains($"\"{WireKey}\":2", payload);
        // Tiền đề: đúng là event B2B (có campaign_id) — nếu không thì Campaign bỏ qua và con dấu
        // chẳng đi tới đâu, test ở trên sẽ "xanh" mà không chứng minh được gì.
        var evt = TestDb.ScoredOutbox(t.NewContext(), sessionId)!;
        Assert.Equal(campaignId, evt.CampaignId);
        Assert.Equal(2, evt.ScoreAggregationVersion);
    }

    // ── (6) GUARD: cửa đóng buổi thứ BA không được lặng lẽ mọc ra ───────────────

    /// <summary>
    /// Bản vá khẳng định <c>EnqueueSessionScoredAsync</c> là "chokepoint DUY NHẤT của mọi buổi chuyển
    /// sang Scored ⇒ không buổi nào lọt". Hôm nay đúng — đo được: chỉ <c>PracticeService</c> và
    /// <c>AnswerService</c> gán <c>SessionStatus.Scored</c>, và cả hai đều gọi notifier ngay sau đó.
    ///
    /// <para>Nhưng đó mới chỉ là một CÂU TRONG COMMENT, mà comment không làm đỏ build. Thêm một cửa
    /// đóng buổi thứ ba (sweeper chốt sổ buổi kẹt, đường quản trị, job dọn dữ liệu…) mà quên gọi
    /// notifier thì buổi đó vào bảng xếp hạng KHÔNG có nhãn thước đo — và nó hỏng theo đúng kiểu tệ
    /// nhất: <c>null</c> hợp lệ, không exception, không log, chỉ là điểm của hai thang lại nằm chung
    /// một bảng như trước khi ADP1 tồn tại.</para>
    ///
    /// <para>⚠ <c>[CallerFilePath]</c> chứ KHÔNG đi ngược tìm thư mục <c>.git</c>: trong git worktree
    /// <c>.git</c> là một FILE (con trỏ <c>gitdir:</c>) nên <c>Directory.Exists</c> không bao giờ đúng
    /// ⇒ test đỏ giả ở mọi worktree, mà worktree chính là cách repo này chạy multi-agent.</para>
    /// </summary>
    [Fact]
    public void MoiCuaDongBuoiScored_DeuPhaiGoiNotifier()
    {
        var serviceDir = ServiceSourceDir();
        // `(?<!\w)` loại `RequiredStatus = SessionStatus.Scored` (hằng số so sánh, không phải phép đóng
        // buổi). `(?:[\w.]+\.)?` bắt cả dạng ghi ĐẦY ĐỦ NAMESPACE — thiếu nó thì một file không `using`
        // Enums viết `Isas.InterviewService.Enums.SessionStatus.Scored` sẽ đi lọt qua guard này.
        var closes = new System.Text.RegularExpressions.Regex(
            @"(?<!\w)Status\s*=\s*(?:[\w.]+\.)?SessionStatus\.Scored");

        var offenders = Directory
            .EnumerateFiles(serviceDir, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Select(f => (File: f, Text: File.ReadAllText(f)))
            // Bỏ dòng chú thích: một comment nhắc tới phép gán không phải là phép gán.
            .Where(x => x.Text.Split('\n')
                .Any(line => !line.TrimStart().StartsWith("//") && closes.IsMatch(line)))
            .Where(x => !x.Text.Contains("EnqueueSessionScoredAsync"))
            .Select(x => Path.GetFileName(x.File))
            .OrderBy(n => n)
            .ToList();

        Assert.True(offenders.Count == 0,
            "File đóng buổi sang Scored mà KHÔNG gọi EnqueueSessionScoredAsync ⇒ buổi đó vào bảng xếp "
            + "hạng không có con dấu cách gộp điểm (ADP1), im lặng: " + string.Join(", ", offenders));

        // Đối chứng DƯƠNG: nếu phép quét không tìm thấy file nào cả thì mệnh đề trên đúng một cách rỗng
        // tuếch — "0 vi phạm" khi đang soi 0 dòng không chứng minh gì (bài học `counter.Count == 0` là
        // đồng hồ chết). Phải có ĐÚNG hai cửa đã biết.
        var closers = Directory
            .EnumerateFiles(serviceDir, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Where(f => File.ReadAllText(f).Split('\n')
                .Any(line => !line.TrimStart().StartsWith("//") && closes.IsMatch(line)))
            .Select(Path.GetFileName)
            .OrderBy(n => n)
            .ToList();

        Assert.Equal(new[] { "AnswerService.cs", "PracticeService.cs" }, closers);
    }

    private static string ServiceSourceDir([System.Runtime.CompilerServices.CallerFilePath] string thisFile = "")
        => Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(thisFile)!, "..", "Isas.InterviewService"));

    // ── (7) Không hồi quy: con dấu MỚI không được đè lên các con dấu đã có ───────

    // scoring_scope_version · campaign_rubric_version · policy_version đều nằm trên cùng entity và
    // cùng nói về "thước đo nào". Thêm một cột nữa vào đúng đường ghi đó là chỗ dễ đụng nhau nhất.
    [Fact]
    public async Task ConDauMoi_KhongDeLenNhanThuocDoDaGhim()
    {
        using var t = new TestDb();
        var campaignId = Guid.NewGuid();
        var candidate = Guid.NewGuid();
        var session = TestDb.Session(candidate, SessionStatus.InProgress, JobCategory.BE, campaignId: campaignId);
        session.CampaignRubricVersion = 7;
        session.CampaignPolicyVersion = 3;
        var crit = Crit(campaignId);
        var q = TestDb.Question(session.Id);
        var a = TestDb.Answer(session.Id, q.Id, AnswerStatus.Scored, DateTime.UtcNow, DateTime.UtcNow);
        t.Db.AddRange(session, crit, q, a);
        t.Db.Add(Score(a.Id, crit.Id));
        await t.Db.SaveChangesAsync();

        await Practice(t).SubmitSessionAsync(candidate, session.Id);

        var saved = await ReadFromDbAsync(t, session.Id);
        Assert.Equal(7, saved.CampaignRubricVersion);
        Assert.Equal(3, saved.CampaignPolicyVersion);
        Assert.Equal(2, saved.ScoreAggregationVersion);

        var evt = TestDb.ScoredOutbox(t.NewContext(), session.Id)!;
        Assert.Equal(7, evt.RubricVersion);
        Assert.Equal(3, evt.CampaignPolicyVersion);
        Assert.Equal(2, evt.ScoreAggregationVersion);
    }
}
