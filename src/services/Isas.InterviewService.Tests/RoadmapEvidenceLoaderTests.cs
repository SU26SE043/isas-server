using Isas.InterviewService.DTOs;
using Isas.InterviewService.Entities;
using Isas.InterviewService.Enums;
using Isas.InterviewService.Services;

namespace Isas.InterviewService.Tests;

/// <summary>
/// BE-5 — <see cref="RoadmapEvidenceLoader"/>: chọn tiêu chí YẾU NHẤT trước, answer ĐIỂM THẤP NHẤT
/// trước, ép trần MaxCriteria/MaxAnswersPerCriterion NGAY TRONG loader (caller không bypass được),
/// lọc AttemptNo==1 (chống self-consistency E10 đếm trùng), bỏ Reasoning rỗng/null, cắt trần độ dài
/// từng trích dẫn. Đây là anchor test cho 2 mutation-check bắt buộc trong đề bài: gỡ trần
/// MaxCriteria/MaxAnswersPerCriterion, và bỏ giới hạn nội dung.
/// </summary>
public class RoadmapEvidenceLoaderTests
{
    private static Guid AddSession(TestDb t, Guid candidateId)
    {
        var session = TestDb.Session(candidateId, SessionStatus.Scored, JobCategory.BE);
        t.Db.PracticeSessions.Add(session);
        return session.Id;
    }

    private static int _orderCounter;

    private static Guid AddAnswerScore(
        TestDb t, Guid sessionId, string criterionName, decimal score, string? reasoning,
        int attemptNo = 1)
    {
        var question = TestDb.Question(sessionId, order: ++_orderCounter);
        t.Db.PracticeQuestions.Add(question);
        var answer = TestDb.Answer(sessionId, question.Id, AnswerStatus.Scored, DateTime.UtcNow, DateTime.UtcNow);
        t.Db.PracticeAnswers.Add(answer);
        // Reuse tiêu chí CÙNG TÊN trong test này — UNIQUE (job_category,language,version,name) WHERE
        // candidate_id IS NULL AND campaign_id IS NULL khoá seed mặc định thành DUY NHẤT (khớp DB
        // thật: mọi buổi trỏ vào chung 1 bộ tiêu chí, giống production).
        var criterion = t.Db.RubricCriteria.Local.FirstOrDefault(c => c.Name == criterionName)
            ?? t.Db.RubricCriteria.FirstOrDefault(c => c.Name == criterionName);
        if (criterion is null)
        {
            criterion = TestDb.Criterion(JobCategory.BE, name: criterionName);
            t.Db.RubricCriteria.Add(criterion);
        }
        t.Db.AnswerScores.Add(new AnswerScore
        {
            Id = Guid.NewGuid(),
            AnswerId = answer.Id,
            CriterionId = criterion.Id,
            AttemptNo = attemptNo,
            Score = score,
            Reasoning = reasoning,
            RubricVersion = 1,
            CreatedAt = DateTime.UtcNow
        });
        return answer.Id;
    }

    [Fact]
    public async Task Rong_KhiKhongCoSessionHoacKhongCoYeuDiem()
    {
        using var t = new TestDb();
        var result = await RoadmapEvidenceLoader.LoadAsync(t.Db, [], [new RoadmapWeakness("Clarity", 30)], default);
        Assert.Empty(result);

        var sid = AddSession(t, Guid.NewGuid());
        await t.Db.SaveChangesAsync();
        result = await RoadmapEvidenceLoader.LoadAsync(t.Db, [sid], [], default);
        Assert.Empty(result);
    }

    [Fact]
    public async Task ChonToiDa3TieuChiYeuNhat_TheoPhanTramTangDan()
    {
        using var t = new TestDb();
        var sid = AddSession(t, Guid.NewGuid());
        AddAnswerScore(t, sid, "A", 1, "lý do A");
        AddAnswerScore(t, sid, "B", 1, "lý do B");
        AddAnswerScore(t, sid, "C", 1, "lý do C");
        AddAnswerScore(t, sid, "D", 1, "lý do D");
        await t.Db.SaveChangesAsync();

        // 4 tiêu chí yếu, D nặng nhất (pct thấp nhất) — chỉ 3 tiêu chí YẾU NHẤT được cấp bằng chứng.
        var weaknesses = new List<RoadmapWeakness>
        {
            new("A", 60), new("B", 50), new("C", 40), new("D", 10)
        };

        var result = await RoadmapEvidenceLoader.LoadAsync(t.Db, [sid], weaknesses, default);

        Assert.Equal(RoadmapEvidenceLoader.MaxCriteria, result.Count);
        var names = result.Select(r => r.CriterionName).ToList();
        Assert.Equal(["D", "C", "B"], names); // 3 yếu nhất, đúng thứ tự yếu → đỡ yếu
        Assert.DoesNotContain("A", names); // yếu nhẹ nhất bị loại khỏi ngân sách 3-tiêu-chí
    }

    [Fact]
    public async Task ChonToiDa3AnswerMoiTieuChi_TheoDiemThapNhat()
    {
        using var t = new TestDb();
        var sid = AddSession(t, Guid.NewGuid());
        AddAnswerScore(t, sid, "X", 5, "khá tốt");
        AddAnswerScore(t, sid, "X", 3, "trung bình");
        AddAnswerScore(t, sid, "X", 1, "rất yếu");
        AddAnswerScore(t, sid, "X", 2, "yếu");
        await t.Db.SaveChangesAsync();

        var result = await RoadmapEvidenceLoader.LoadAsync(
            t.Db, [sid], [new RoadmapWeakness("X", 20)], default);

        var quotes = Assert.Single(result).Reasoning;
        Assert.Equal(RoadmapEvidenceLoader.MaxAnswersPerCriterion, quotes.Count);
        // điểm THẤP NHẤT trước (1,2,3) — "khá tốt" (điểm 5, tốt nhất) bị loại khỏi trần 3-answer
        Assert.Equal(["rất yếu", "yếu", "trung bình"], quotes);
        Assert.DoesNotContain("khá tốt", quotes);
    }

    [Fact]
    public async Task BoQua_AttemptKhac1_ChongSelfConsistencyDemTrung()
    {
        using var t = new TestDb();
        var sid = AddSession(t, Guid.NewGuid());
        AddAnswerScore(t, sid, "X", 1, "chuẩn - attempt 1", attemptNo: 1);
        AddAnswerScore(t, sid, "X", 1, "nhiễu - attempt 2", attemptNo: 2);
        AddAnswerScore(t, sid, "X", 1, "nhiễu - attempt 3", attemptNo: 3);
        await t.Db.SaveChangesAsync();

        var result = await RoadmapEvidenceLoader.LoadAsync(
            t.Db, [sid], [new RoadmapWeakness("X", 20)], default);

        var quotes = Assert.Single(result).Reasoning;
        var quote = Assert.Single(quotes);
        Assert.Equal("chuẩn - attempt 1", quote);
    }

    [Fact]
    public async Task BoQua_ReasoningRongHoacNull()
    {
        using var t = new TestDb();
        var sid = AddSession(t, Guid.NewGuid());
        AddAnswerScore(t, sid, "X", 1, null);
        AddAnswerScore(t, sid, "X", 2, "");
        AddAnswerScore(t, sid, "X", 3, "lý do thật");
        await t.Db.SaveChangesAsync();

        var result = await RoadmapEvidenceLoader.LoadAsync(
            t.Db, [sid], [new RoadmapWeakness("X", 20)], default);

        var quotes = Assert.Single(result).Reasoning;
        Assert.Equal(["lý do thật"], quotes);
    }

    [Fact]
    public async Task TieuChiKhongCoReasoningNao_BiLoaiKhoiKetQua()
    {
        using var t = new TestDb();
        var sid = AddSession(t, Guid.NewGuid());
        AddAnswerScore(t, sid, "X", 1, null);
        await t.Db.SaveChangesAsync();

        var result = await RoadmapEvidenceLoader.LoadAsync(
            t.Db, [sid], [new RoadmapWeakness("X", 20)], default);

        Assert.Empty(result); // KHÔNG trả CriterionEvidence rỗng-Reasoning — không có gì để trích
    }

    [Fact]
    public async Task CatNgan_TrichDanQuaDai()
    {
        using var t = new TestDb();
        var sid = AddSession(t, Guid.NewGuid());
        var longReasoning = new string('x', 1000); // vượt xa MaxReasoningCharsPerQuote (400)
        AddAnswerScore(t, sid, "X", 1, longReasoning);
        await t.Db.SaveChangesAsync();

        var result = await RoadmapEvidenceLoader.LoadAsync(
            t.Db, [sid], [new RoadmapWeakness("X", 20)], default);

        var quote = Assert.Single(Assert.Single(result).Reasoning);
        Assert.True(quote.Length < longReasoning.Length);
        Assert.Equal(new string('x', 400), quote);
    }

    [Fact]
    public async Task ChiTaiAnswerTrongDanhSachSessionId_KhongLoBuoiKhac()
    {
        using var t = new TestDb();
        var candidate = Guid.NewGuid();
        var sidTrong = AddSession(t, candidate);
        var sidNgoai = AddSession(t, Guid.NewGuid()); // buổi của candidate KHÁC — không nằm trong sourceSessionIds
        AddAnswerScore(t, sidTrong, "X", 5, "trong phạm vi");
        AddAnswerScore(t, sidNgoai, "X", 1, "NGOÀI phạm vi — điểm thấp hơn nhưng KHÔNG được lấy");
        await t.Db.SaveChangesAsync();

        var result = await RoadmapEvidenceLoader.LoadAsync(
            t.Db, [sidTrong], [new RoadmapWeakness("X", 20)], default);

        var quote = Assert.Single(Assert.Single(result).Reasoning);
        Assert.Equal("trong phạm vi", quote);
    }
}
