using Isas.InterviewService.Entities;
using Isas.InterviewService.Enums;
using Microsoft.EntityFrameworkCore;

namespace Isas.InterviewService.Tests;

/// <summary>
/// ADP1 — điểm gộp về CÂU GỐC: một câu gốc cộng cả chuỗi đào sâu của nó là MỘT quan sát, không phải N.
///
/// <para><b>Vì sao bộ test này tồn tại:</b> trước nó, hành vi mới có <b>0% coverage</b> — đo được, không
/// phải phỏng đoán: 30 file test seed <c>new AnswerScore</c>, 5 file chạm <c>RootQuestionId</c>,
/// <b>giao nhau RỖNG</b>. Mọi test có điểm đều để <c>RootQuestionId = null</c> ⇒ mỗi answer là gốc riêng
/// ⇒ bước gộp mới gom nhóm một-phần-tử ⇒ <b>no-op</b>. Suite xanh chỉ chứng minh "không phá gì cũ",
/// không nói được câu nào về việc phép gộp có ĐÚNG hay không.</para>
///
/// <para><b>Khoá CON SỐ, không khoá "chạy được".</b> Mỗi ca dựng dữ liệu sao cho công thức cũ và công
/// thức mới cho ra hai giá trị KHÁC nhau, rồi ghim giá trị mới — kèm giá trị cũ trong chú thích để lần
/// sau ai đó đọc failure message biết ngay mình vừa quay về hành vi nào.</para>
/// </summary>
public class RootQuestionAggregationAdp1Tests
{
    // Thang 10 để điểm thô đọc thẳng ra phần trăm (×10): 3.4 → 34%, 2.5 → 25%.
    private const int Max = 10;

    private static RubricCriterion Crit(string name)
        => new()
        {
            Id = Guid.NewGuid(),
            Name = name,
            Description = name,
            Weight = 1.0m,
            MaxScore = Max,
            IsActive = true,
            JobCategory = JobCategory.BE,
            CampaignId = null,
            CandidateId = null,
            Language = "vi",
            Version = 1
        };

    private static AnswerScore Score(Guid answerId, Guid criterionId, decimal score, int attempt = 1)
        => new()
        {
            Id = Guid.NewGuid(),
            AnswerId = answerId,
            CriterionId = criterionId,
            AttemptNo = attempt,
            Score = score,
            Reasoning = "x",
            RubricVersion = 1,
            CreatedAt = DateTime.UtcNow
        };

    /// <summary>
    /// Dựng buổi B2C: mỗi phần tử <paramref name="chains"/> là MỘT câu gốc kèm điểm của cả chuỗi đào
    /// sâu của nó (<c>[0]</c> = câu gốc, phần còn lại = câu AI nối thêm).
    ///
    /// <para><paramref name="linkRoot"/> = <c>false</c> ⇒ mọi câu để <c>RootQuestionId = null</c>: đúng
    /// hình dạng chế độ frontier (kill-switch <c>MaxDeepPerQuestion = 0</c>, <c>AnswerService</c> để null
    /// trên mọi câu nối thêm) và của mọi buổi đã có trên production trước INT-17b. Đây là ca LÙI AN TOÀN
    /// — ở đó phép gộp mới phải cho ra ĐÚNG con số cũ.</para>
    /// </summary>
    private static PracticeSession SeedSession(
        TestDb t, RubricCriterion crit, decimal[][] chains, bool linkRoot = true)
    {
        var session = TestDb.Session(Guid.NewGuid(), SessionStatus.Scored, JobCategory.BE);
        t.Db.Add(session);

        var order = 0;
        foreach (var chain in chains)
        {
            Guid? rootId = null;
            for (var i = 0; i < chain.Length; i++)
            {
                var q = new PracticeQuestion
                {
                    Id = Guid.NewGuid(),
                    SessionId = session.Id,
                    OrderNo = ++order,
                    Content = "q",
                    TimeLimitSec = 120,
                    Kind = i == 0 ? QuestionKind.Seed : QuestionKind.FollowUp,
                    Depth = i,
                    RootQuestionId = (i == 0 || !linkRoot) ? null : rootId
                };
                if (i == 0) rootId = q.Id;

                var a = TestDb.Answer(session.Id, q.Id, AnswerStatus.Scored, DateTime.UtcNow, DateTime.UtcNow);
                t.Db.AddRange(q, a);
                t.Db.Add(Score(a.Id, crit.Id, chain[i]));
            }
        }
        return session;
    }

    // Đường B2C: ghi session_criterion_scores + overall_score.
    private static async Task<PracticeSession> RunResultService(TestDb t, Guid sessionId)
    {
        await TestDb.ResultService(t.Db).ComputeAndStoreAsync(sessionId);
        return await t.Db.PracticeSessions.AsNoTracking().FirstAsync(x => x.Id == sessionId);
    }

    // Đường điểm tổng đi vào event xếp hạng (campaign_rankings với B2B).
    private static async Task<decimal> RunNotifier(TestDb t, Guid sessionId)
    {
        await TestDb.Notifier(t.Db).EnqueueSessionScoredAsync(sessionId);
        await t.Db.SaveChangesAsync();
        return TestDb.ScoredOutbox(t.NewContext(), sessionId)!.TotalScore;
    }

    // ── VIỆC 1(a) — GỘP ĐÚNG: chuỗi dài không còn ăn nhiều phiếu hơn ────────────────────────
    //
    // Cùng buổi, cùng ứng viên, cùng một tiêu chí:
    //   câu gốc A bị đào sâu 3 lần → 4 answer, mỗi answer 4đ
    //   câu gốc B trần            → 1 answer, 1đ
    //
    //   CŨ (mỗi answer một phiếu): (4+4+4+4+1)/5 = 3.4  → 34.00%
    //   MỚI (gộp về câu gốc):      (4 , 1) → 2.5        → 25.00%
    //
    // 25 ≠ 34 là toàn bộ nội dung của bản sửa: chủ đề bị hỏi kỹ hơn không còn tự động nặng gấp bốn,
    // mà độ dài chuỗi lại do AI quyết lúc thi chứ không phải do thước đo ai khai.
    [Fact]
    public async Task ChuoiDaoSauGopVeCauGoc_KhongConAnNhieuPhieuHonChuoiNgan()
    {
        using var t = new TestDb();
        var crit = Crit("Chiều sâu kỹ thuật");
        t.Db.Add(crit);
        var session = SeedSession(t, crit, [[4m, 4m, 4m, 4m], [1m]]);
        await t.Db.SaveChangesAsync();

        var s = await RunResultService(t, session.Id);

        Assert.Equal(25m, s.OverallScore);   // 34.00 = hành vi CŨ (mỗi answer một phiếu)

        var row = await t.Db.SessionCriterionScores.AsNoTracking()
            .SingleAsync(x => x.SessionId == session.Id);
        Assert.Equal(2.5m, row.AverageScore);   // 3.4 = hành vi CŨ
        Assert.Equal(25m, row.Percentage);

        // answeredCount vẫn đếm theo ANSWER, KHÔNG theo câu gốc — nó trả lời câu hỏi "trả lời được
        // bao nhiêu câu", không phải mẫu số của điểm. Gộp nhầm cả nó là đổi nghĩa một trường khác.
        Assert.Equal(5, s.AnsweredCount);
    }

    // Cùng dữ liệu, đường điểm tổng đi vào xếp hạng. Một tiêu chí weight 1.0 ⇒ weighted = equal-weight
    // ⇒ cùng 25.00. Đây là đường NẶNG hơn với B2B: số này đi thẳng vào campaign_rankings, nên hai ứng
    // viên cùng chiến dịch từng bị xếp cạnh nhau bằng hai cách phân bổ trọng số khác nhau.
    [Fact]
    public async Task ChuoiDaoSauGopVeCauGoc_DiemDiVaoXepHangCungGopVeGoc()
    {
        using var t = new TestDb();
        var crit = Crit("Chiều sâu kỹ thuật");
        t.Db.Add(crit);
        var session = SeedSession(t, crit, [[4m, 4m, 4m, 4m], [1m]]);
        await t.Db.SaveChangesAsync();

        Assert.Equal(25m, await RunNotifier(t, session.Id));   // 34.00 = hành vi CŨ
    }

    // ── VIỆC 1(b) — LÙI AN TOÀN: mọi RootQuestionId null ⇒ ĐÚNG con số cũ ───────────────────
    //
    // Không phải trang trí. Đây là lời hứa lùi-an-toàn cho (a) kill-switch frontier
    // (MaxDeepPerQuestion = 0 ⇒ AnswerService để RootQuestionId = null trên mọi câu nối thêm) và
    // (b) MỌI buổi đã có trên production trước INT-17b. Thiếu vế này thì không phân biệt được
    // "gộp đúng" với "gộp bừa" — một phép gộp sai bét vẫn làm ca (a) xanh nếu nó tình cờ ra 25.
    [Fact]
    public async Task MoiCauLaGocRieng_ChoDungConSoCuaHanhViCu_ResultService()
    {
        using var t = new TestDb();
        var crit = Crit("Chiều sâu kỹ thuật");
        t.Db.Add(crit);
        var session = SeedSession(t, crit, [[4m, 4m, 4m, 4m], [1m]], linkRoot: false);
        await t.Db.SaveChangesAsync();

        var s = await RunResultService(t, session.Id);

        Assert.Equal(34m, s.OverallScore);   // (4+4+4+4+1)/5 = 3.4 → 34%

        var row = await t.Db.SessionCriterionScores.AsNoTracking()
            .SingleAsync(x => x.SessionId == session.Id);
        Assert.Equal(3.4m, row.AverageScore);
    }

    [Fact]
    public async Task MoiCauLaGocRieng_ChoDungConSoCuaHanhViCu_Notifier()
    {
        using var t = new TestDb();
        var crit = Crit("Chiều sâu kỹ thuật");
        t.Db.Add(crit);
        var session = SeedSession(t, crit, [[4m, 4m, 4m, 4m], [1m]], linkRoot: false);
        await t.Db.SaveChangesAsync();

        Assert.Equal(34m, await RunNotifier(t, session.Id));
    }

    // ── VIỆC 2 — KHE NỐI: hai đường điểm trên CÙNG một buổi phải ra CÙNG một con số ──────────
    //
    // Hàm gộp có test riêng vẫn KHÔNG chặn được việc ai đó sau này gọi sai ở MỘT trong hai chỗ.
    // Đúng lớp lỗ Q10-M2 của repo này: renderer có test, retry có test, khe giữa chúng thì không —
    // và bug production sống đúng ở đó.
    //
    // Test này CỐ Ý chỉ khẳng định "hai số bằng nhau" chứ không ghim giá trị: giá trị đã do 4 ca ở
    // trên khoá. Nhờ vậy nó là dụng cụ SẮC cho đúng một thứ — sửa lệch một chỗ gọi thì chỉ nó đỏ.
    //
    // Kèm CHỐT CHỐNG RỖNG: 0 == 0 cũng "bằng nhau", mà 0 lại là đúng hình dạng hai lỗi đã biết trong
    // file production (weightSum = 0 ⇒ TotalScore 0; scoredCriteriaCount = 0 ⇒ overall 0). Không có
    // chốt này thì một buổi không resolve nổi rubric sẽ làm test xanh một cách vô nghĩa.
    [Fact]
    public async Task HaiDuongDiem_TrenCungMotBuoi_ChoCungMotConSo()
    {
        using var t = new TestDb();
        var crit = Crit("Chiều sâu kỹ thuật");
        t.Db.Add(crit);
        var session = SeedSession(t, crit, [[4m, 4m, 4m, 4m], [1m]]);
        await t.Db.SaveChangesAsync();

        var total = await RunNotifier(t, session.Id);
        var overall = (await RunResultService(t, session.Id)).OverallScore;

        Assert.NotEqual(0m, total);          // chống xanh-vô-nghĩa (xem chú thích trên)
        Assert.Equal(total, overall);
    }

    // ── VIỆC 1 — TRUNG BÌNH qua các câu gốc, KHÔNG phải median ──────────────────────────────
    //
    // Cần BA câu gốc mới phân biệt được: với hai gốc {4, 1} thì median (trung bình 2 phần tử giữa)
    // = 2.5 = trung bình, hai công thức trùng nhau và mọi ca ở trên đều mù.
    //
    //   gốc A (chuỗi 4 answer @4) → 4      gốc B (1 answer @4) → 4      gốc C (1 answer @1) → 1
    //   TRUNG BÌNH qua gốc : (4+4+1)/3 = 3.0  → 30.00   ← đúng
    //   MEDIAN qua gốc     : sorted{1,4,4}→4  → 40.00
    //   CŨ theo answer     : 21/6 = 3.5       → 35.00
    // Ba giá trị đôi một khác nhau ⇒ một ca phân xử được cả ba khả năng.
    [Fact]
    public async Task TrungBinhQuaCacCauGoc_KhongPhaiMedian()
    {
        using var t = new TestDb();
        var crit = Crit("Chiều sâu kỹ thuật");
        t.Db.Add(crit);
        var session = SeedSession(t, crit, [[4m, 4m, 4m, 4m], [4m], [1m]]);
        await t.Db.SaveChangesAsync();

        var s = await RunResultService(t, session.Id);
        Assert.Equal(30m, s.OverallScore);   // 40.00 = median qua gốc ; 35.00 = hành vi CŨ

        Assert.Equal(30m, await RunNotifier(t, session.Id));
    }

    // ── VIỆC 1 — BẤT BIẾN SẢN PHẨM viết bằng lời: độ dài chuỗi KHÔNG được đổi điểm ───────────
    //
    // Hai buổi giống hệt nhau, khác đúng một thứ: chuỗi đào sâu của gốc A dài 2 hay 4 answer, cùng
    // mức điểm. Dưới hành vi CŨ hai buổi ra 30.00 và 34.00 — tức ứng viên bị AI hỏi kỹ hơn thì điểm
    // đổi mà bài làm không đổi. Nay cả hai phải ra CÙNG một số.
    //
    // Hai tham số cùng ghim về MỘT hằng số, nên bất biến "độ dài chuỗi không đổi điểm" suy ra được
    // từ chính cặp ca này chứ không phải từ một lời hứa trong chú thích.
    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public async Task DoDaiChuoiDaoSau_KhongLamDoiDiem(int chainLength)
    {
        using var t = new TestDb();
        var crit = Crit("Chiều sâu kỹ thuật");
        t.Db.Add(crit);
        var chain = Enumerable.Repeat(4m, chainLength).ToArray();
        var session = SeedSession(t, crit, [chain, [1m]]);
        await t.Db.SaveChangesAsync();

        var s = await RunResultService(t, session.Id);
        // CŨ: chuỗi 2 → 30.00, chuỗi 4 → 34.00 (điểm trôi theo độ dài chuỗi).
        Assert.Equal(25m, s.OverallScore);
    }

    // ── VIỆC 1 — câu gốc KHÔNG chạm tiêu chí thì không có mặt ở mẫu số của tiêu chí đó ───────
    //
    // INT-18: tiêu chí không ai hỏi bị LOẠI khỏi điểm, KHÔNG tính 0 — phạt ứng viên vì thứ họ không
    // được hỏi. Bước gộp mới thêm một tầng nhóm nên phải giữ đúng tính chất đó ở mức CÂU GỐC.
    //
    //   gốc A: chuỗi 4 answer — mỗi answer có điểm X=4 và Y=2
    //   gốc B: 1 answer      — CHỈ có điểm Y=5, không dòng X nào
    //
    //   X: chỉ gốc A chạm  → 4.0                 → 40.00   (nếu gốc B bị tính 0 thì ra 20.00)
    //   Y: cả hai gốc chạm → (2, 5) → 3.5        → 35.00   (hành vi CŨ: 13/5 = 2.6 → 26.00)
    //   overall = (40+35)/2 = 37.50                        (hành vi CŨ: (40+26)/2 = 33.00)
    [Fact]
    public async Task CauGocKhongChamTieuChi_KhongPhaLoangDiemCuaTieuChiDo()
    {
        using var t = new TestDb();
        var x = Crit("Thiết kế hệ thống");
        var y = Crit("Giao tiếp & trình bày");
        var session = TestDb.Session(Guid.NewGuid(), SessionStatus.Scored, JobCategory.BE);
        t.Db.AddRange(x, y, session);

        Guid? rootA = null;
        for (var i = 0; i < 4; i++)
        {
            var q = new PracticeQuestion
            {
                Id = Guid.NewGuid(), SessionId = session.Id, OrderNo = i + 1, Content = "q",
                TimeLimitSec = 120, Depth = i,
                Kind = i == 0 ? QuestionKind.Seed : QuestionKind.FollowUp,
                RootQuestionId = i == 0 ? null : rootA
            };
            if (i == 0) rootA = q.Id;
            var a = TestDb.Answer(session.Id, q.Id, AnswerStatus.Scored, DateTime.UtcNow, DateTime.UtcNow);
            t.Db.AddRange(q, a);
            t.Db.AddRange(Score(a.Id, x.Id, 4m), Score(a.Id, y.Id, 2m));
        }

        var qB = new PracticeQuestion
        {
            Id = Guid.NewGuid(), SessionId = session.Id, OrderNo = 5, Content = "q",
            TimeLimitSec = 120, Depth = 0, Kind = QuestionKind.Seed, RootQuestionId = null
        };
        var aB = TestDb.Answer(session.Id, qB.Id, AnswerStatus.Scored, DateTime.UtcNow, DateTime.UtcNow);
        t.Db.AddRange(qB, aB);
        t.Db.Add(Score(aB.Id, y.Id, 5m));           // gốc B KHÔNG có dòng điểm nào cho X
        await t.Db.SaveChangesAsync();

        var s = await RunResultService(t, session.Id);

        var rows = await t.Db.SessionCriterionScores.AsNoTracking()
            .Where(r => r.SessionId == session.Id).ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.Equal(4.0m, rows.Single(r => r.CriterionId == x.Id).AverageScore);   // 2.0 = tính gốc B thành 0
        Assert.Equal(3.5m, rows.Single(r => r.CriterionId == y.Id).AverageScore);   // 2.6 = hành vi CŨ
        Assert.Equal(37.5m, s.OverallScore);
    }

    // ── VIỆC 1 — E10 vẫn chạy TRƯỚC bước gộp về gốc ─────────────────────────────────────────
    //
    // Thứ tự ba bước là load-bearing: median mỗi (answer, criterion) — rồi mới gộp về gốc. Bỏ hoặc
    // đảo bước median thì các attempt self-consistency lẻ tẻ được đối xử như answer riêng.
    //
    //   gốc A: answer a0 có 3 attempt {1,1,7} → median 1 ; answer a1 một attempt @3 → gốc A = 2.0
    //   gốc B: 1 answer @1                                                          → gốc B = 1.0
    //   ĐÚNG  : (2.0 + 1.0)/2 = 1.5                                → 15.00
    //   Bỏ median (mọi dòng thô vào thẳng nhóm gốc): A=(1+1+7+3)/4=3.0, B=1 → 2.0  → 20.00
    [Fact]
    public async Task MedianTungAnswer_ChayTruocKhiGopVeCauGoc()
    {
        using var t = new TestDb();
        var crit = Crit("Chiều sâu kỹ thuật");
        var session = TestDb.Session(Guid.NewGuid(), SessionStatus.Scored, JobCategory.BE);
        t.Db.AddRange(crit, session);

        var qRoot = new PracticeQuestion
        {
            Id = Guid.NewGuid(), SessionId = session.Id, OrderNo = 1, Content = "q",
            TimeLimitSec = 120, Depth = 0, Kind = QuestionKind.Seed, RootQuestionId = null
        };
        var qDeep = new PracticeQuestion
        {
            Id = Guid.NewGuid(), SessionId = session.Id, OrderNo = 2, Content = "q",
            TimeLimitSec = 120, Depth = 1, Kind = QuestionKind.FollowUp, RootQuestionId = qRoot.Id
        };
        var qB = new PracticeQuestion
        {
            Id = Guid.NewGuid(), SessionId = session.Id, OrderNo = 3, Content = "q",
            TimeLimitSec = 120, Depth = 0, Kind = QuestionKind.Seed, RootQuestionId = null
        };
        var a0 = TestDb.Answer(session.Id, qRoot.Id, AnswerStatus.Scored, DateTime.UtcNow, DateTime.UtcNow);
        var a1 = TestDb.Answer(session.Id, qDeep.Id, AnswerStatus.Scored, DateTime.UtcNow, DateTime.UtcNow);
        var aB = TestDb.Answer(session.Id, qB.Id, AnswerStatus.Scored, DateTime.UtcNow, DateTime.UtcNow);
        t.Db.AddRange(qRoot, qDeep, qB, a0, a1, aB);

        // 3 attempt của CÙNG (answer, criterion) — trung bình 3.0 nhưng median 1.0, để hai công thức
        // không thể trùng kết quả.
        t.Db.AddRange(
            Score(a0.Id, crit.Id, 1m, attempt: 1),
            Score(a0.Id, crit.Id, 1m, attempt: 2),
            Score(a0.Id, crit.Id, 7m, attempt: 3),
            Score(a1.Id, crit.Id, 3m),
            Score(aB.Id, crit.Id, 1m));
        await t.Db.SaveChangesAsync();

        var s = await RunResultService(t, session.Id);
        Assert.Equal(15m, s.OverallScore);   // 20.00 = bỏ bước median
    }
}
