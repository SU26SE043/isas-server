using Isas.InterviewService.DTOs;
using Isas.InterviewService.Entities;
using Isas.InterviewService.Enums;
using Isas.InterviewService.Services;

namespace Isas.InterviewService.Tests;

/// <summary>
/// MIS1-B4 — <see cref="RoadmapMistakeLoader"/>: chỉ lấy tiêu chí <c>Ai</c>-scoring, chỉ lấy answer
/// DƯỚI ngưỡng, ép trần 4 tiêu chí × 3 lỗi = 12 NGAY TRONG loader (caller không bypass được), khớp
/// theo <c>CriterionId</c> (KHÔNG theo tên), bỏ answer <c>Skipped</c>/transcript rỗng/Reasoning
/// rỗng, <c>MaxScore=0</c> không nổ, thứ tự tất định + <c>mistake_key</c> mint đúng "m1".."mN" theo
/// ĐÚNG thứ tự đã sort (tiêu chí yếu nhất trước, trong mỗi tiêu chí điểm thấp nhất trước).
/// </summary>
public class RoadmapMistakeLoaderTests
{
    private static Guid AddSession(TestDb t, Guid candidateId)
    {
        var session = TestDb.Session(candidateId, SessionStatus.Scored, JobCategory.BE);
        t.Db.PracticeSessions.Add(session);
        return session.Id;
    }

    private static int _orderCounter;

    /// <summary>
    /// Seed 1 AnswerScore + answer/question/criterion đứng sau nó. `criterion` truyền sẵn (không tự
    /// tra theo tên — khác <c>RoadmapEvidenceLoaderTests</c>) vì bài test này PHẢI phân biệt được 2
    /// tiêu chí CÙNG TÊN nhưng KHÁC id (đúng ca rubric đổi version mà loader phải xử đúng).
    /// </summary>
    private static Guid AddMistakeAnswer(
        TestDb t, Guid sessionId, RubricCriterion criterion, decimal score, string? reasoning,
        string? transcript = "câu trả lời của ứng viên", AnswerStatus status = AnswerStatus.Scored,
        int attemptNo = 1, string? sampleAnswer = null)
    {
        if (t.Db.RubricCriteria.Local.All(c => c.Id != criterion.Id)
            && !t.Db.RubricCriteria.Any(c => c.Id == criterion.Id))
            t.Db.RubricCriteria.Add(criterion);

        var question = TestDb.Question(sessionId, order: ++_orderCounter);
        t.Db.PracticeQuestions.Add(question);
        var answer = TestDb.Answer(sessionId, question.Id, status, DateTime.UtcNow, DateTime.UtcNow);
        answer.Transcript = transcript;
        answer.SampleAnswer = sampleAnswer;
        t.Db.PracticeAnswers.Add(answer);

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

    private static RubricCriterion AiCriterion(string name = "Clarity", int maxScore = 5)
    {
        var c = TestDb.Criterion(JobCategory.BE, name: name);
        c.MaxScore = maxScore;
        return c;
    }

    [Fact]
    public async Task Rong_KhiKhongCoSessionHoacKhongCoTieuChiYeu()
    {
        using var t = new TestDb();
        var criterion = AiCriterion();
        var result = await RoadmapMistakeLoader.LoadAsync(
            t.Db, Guid.NewGuid(), [], [new RoadmapWeakness("Clarity", 30, [criterion.Id])], 50m, default);
        Assert.Empty(result);

        var sid = AddSession(t, Guid.NewGuid());
        await t.Db.SaveChangesAsync();
        result = await RoadmapMistakeLoader.LoadAsync(t.Db, Guid.NewGuid(), [sid], [], 50m, default);
        Assert.Empty(result);
    }

    [Fact]
    public async Task Rong_KhiWeaknessKhongMangCriterionIds()
    {
        // CriterionIds null/rỗng (đường second-path RoadmapLessonService dựng từ Baseline không có
        // id) — KHÔNG khớp theo tên, phải bỏ qua tiêu chí này hoàn toàn.
        using var t = new TestDb();
        var criterion = AiCriterion();
        var sid = AddSession(t, Guid.NewGuid());
        AddMistakeAnswer(t, sid, criterion, 1, "lý do yếu");
        await t.Db.SaveChangesAsync();

        var result = await RoadmapMistakeLoader.LoadAsync(
            t.Db, Guid.NewGuid(), [sid], [new RoadmapWeakness("Clarity", 20, null)], 50m, default);
        Assert.Empty(result);

        result = await RoadmapMistakeLoader.LoadAsync(
            t.Db, Guid.NewGuid(), [sid], [new RoadmapWeakness("Clarity", 20, [])], 50m, default);
        Assert.Empty(result);
    }

    [Fact]
    public async Task ChiLayTieuChi_Ai_BoQua_DeliveryMetrics()
    {
        using var t = new TestDb();
        var aiCrit = AiCriterion("Clarity");
        var deliveryCrit = AiCriterion("Fluency");
        deliveryCrit.ScoringMethod = CriterionScoringMethod.DeliveryMetrics;

        var sid = AddSession(t, Guid.NewGuid());
        AddMistakeAnswer(t, sid, aiCrit, 1, "lý do AI chấm");
        AddMistakeAnswer(t, sid, deliveryCrit, 1, "lý do máy đo VAD");
        await t.Db.SaveChangesAsync();

        var weaknesses = new List<RoadmapWeakness>
        {
            new("Clarity", 20, [aiCrit.Id]),
            new("Fluency", 20, [deliveryCrit.Id])
        };
        var result = await RoadmapMistakeLoader.LoadAsync(
            t.Db, Guid.NewGuid(), [sid], weaknesses, 50m, default);

        var mistake = Assert.Single(result);
        Assert.Equal("Clarity", mistake.CriterionName);
    }

    [Fact]
    public async Task ChiLayAnswer_DuoiNguong_TrenNguongBiLoai()
    {
        using var t = new TestDb();
        var crit = AiCriterion("Clarity", maxScore: 10);
        var sid = AddSession(t, Guid.NewGuid());
        AddMistakeAnswer(t, sid, crit, 3, "dưới ngưỡng 50% (3/10=30%)");   // 30% < 50% → lấy
        AddMistakeAnswer(t, sid, crit, 8, "trên ngưỡng 50% (8/10=80%)");   // 80% ≥ 50% → loại
        await t.Db.SaveChangesAsync();

        var result = await RoadmapMistakeLoader.LoadAsync(
            t.Db, Guid.NewGuid(), [sid], [new RoadmapWeakness("Clarity", 30, [crit.Id])], 50m, default);

        var mistake = Assert.Single(result);
        Assert.Equal("dưới ngưỡng 50% (3/10=30%)", mistake.Reasoning);
    }

    [Fact]
    public async Task EpTranToiDa4TieuChiX3Loi_TheoDungThuTuYeuNhatVaDiemThapNhat()
    {
        using var t = new TestDb();
        var sid = AddSession(t, Guid.NewGuid());

        // 5 tiêu chí yếu — chỉ 4 tiêu chí YẾU NHẤT (percentage thấp nhất) được trích.
        var criteria = new[] { "A", "B", "C", "D", "E" }
            .Select(n => AiCriterion(n)).ToArray();
        var weaknesses = new List<RoadmapWeakness>();
        for (var i = 0; i < criteria.Length; i++)
        {
            var pct = new[] { 60m, 50m, 40m, 30m, 10m }[i]; // E yếu nhất
            weaknesses.Add(new RoadmapWeakness(criteria[i].Name, pct, [criteria[i].Id]));
            // Mỗi tiêu chí có 4 answer dưới ngưỡng — chỉ 3 điểm THẤP NHẤT được lấy.
            AddMistakeAnswer(t, sid, criteria[i], 4, $"{criteria[i].Name}-4");
            AddMistakeAnswer(t, sid, criteria[i], 1, $"{criteria[i].Name}-1");
            AddMistakeAnswer(t, sid, criteria[i], 3, $"{criteria[i].Name}-3");
            AddMistakeAnswer(t, sid, criteria[i], 2, $"{criteria[i].Name}-2");
        }
        await t.Db.SaveChangesAsync();

        var result = await RoadmapMistakeLoader.LoadAsync(
            t.Db, Guid.NewGuid(), [sid], weaknesses, 100m, default); // ngưỡng cao để mọi answer đều "dưới ngưỡng"

        Assert.Equal(RoadmapMistakeLoader.MaxCriteria * RoadmapMistakeLoader.MaxMistakesPerCriterion, result.Count);

        var names = result.Select(r => r.CriterionName).Distinct().ToList();
        Assert.Equal(["E", "D", "C", "B"], names); // 4 yếu nhất, đúng thứ tự yếu → đỡ yếu
        Assert.DoesNotContain("A", names); // yếu nhẹ nhất bị loại khỏi ngân sách 4-tiêu-chí

        // Trong mỗi tiêu chí — điểm THẤP NHẤT trước (1,2,3), điểm 4 (tệ nhất về "tốt") bị loại.
        var eGroup = result.Where(r => r.CriterionName == "E").Select(r => r.Reasoning).ToList();
        Assert.Equal(["E-1", "E-2", "E-3"], eGroup);
        Assert.DoesNotContain("E-4", eGroup);
    }

    /// <summary>
    /// REC1-B1 — tiêu chí TÁI PHẠM NHIỀU BUỔI (<c>WeakSessions</c>) phải xếp TRƯỚC tiêu chí ít tái
    /// phạm hơn, KỂ CẢ KHI <c>Percentage</c> (điểm buổi mới nhất) của nó CAO HƠN hẳn. Trước bản vá,
    /// sort chỉ nhìn <c>Percentage</c> — một tiêu chí lụt điểm đúng MỘT buổi (rồi cải thiện) sẽ vượt
    /// mặt một tiêu chí sai đi sai lại nhiều buổi, chọn nhầm thứ đáng ưu tiên đưa vào lộ trình.
    /// </summary>
    [Fact]
    public async Task TaiPhamNhieuBuoiXetTruoc_KeCaKhiPercentageCaoHon()
    {
        using var t = new TestDb();
        var sid = AddSession(t, Guid.NewGuid());
        var critItYeu = AiCriterion("ItYeu");
        var critTaiPham = AiCriterion("TaiPham");

        AddMistakeAnswer(t, sid, critItYeu, 1, "it-yeu-1");
        AddMistakeAnswer(t, sid, critTaiPham, 1, "tai-pham-1");
        await t.Db.SaveChangesAsync();

        var weaknesses = new List<RoadmapWeakness>
        {
            // Percentage 10 < 40 ⇒ theo luật CŨ (OrderBy Percentage) "ItYeu" xét TRƯỚC — SAI, vì nó
            // chỉ lụt điểm ĐÚNG MỘT buổi trong khi "TaiPham" sai đi sai lại 3/4 buổi.
            new("ItYeu", 10, [critItYeu.Id], WeakSessions: 1, TotalSessions: 4),
            new("TaiPham", 40, [critTaiPham.Id], WeakSessions: 3, TotalSessions: 4),
        };
        var result = await RoadmapMistakeLoader.LoadAsync(
            t.Db, Guid.NewGuid(), [sid], weaknesses, 100m, default);

        Assert.Equal(2, result.Count);
        // "m1" (mint đầu tiên, theo đúng thứ tự sort) phải thuộc về tiêu chí TÁI PHẠM NHIỀU HƠN,
        // bất kể Percentage của nó cao hơn "ItYeu".
        Assert.Equal("TaiPham", result.Single(r => r.MistakeKey == "m1").CriterionName);
        Assert.Equal("ItYeu", result.Single(r => r.MistakeKey == "m2").CriterionName);
    }

    [Fact]
    public async Task MintMistakeKey_TheoDungThuTu_M1DenMN()
    {
        using var t = new TestDb();
        var sid = AddSession(t, Guid.NewGuid());
        var critYeuNhat = AiCriterion("Yeu");
        var critDoNhe = AiCriterion("DoNhe");

        AddMistakeAnswer(t, sid, critYeuNhat, 2, "yeu-2");
        AddMistakeAnswer(t, sid, critYeuNhat, 1, "yeu-1");
        AddMistakeAnswer(t, sid, critDoNhe, 3, "donhe-3");
        await t.Db.SaveChangesAsync();

        var weaknesses = new List<RoadmapWeakness>
        {
            new("DoNhe", 40, [critDoNhe.Id]),   // đỡ yếu hơn → xét SAU
            new("Yeu", 10, [critYeuNhat.Id])    // yếu nhất → xét TRƯỚC
        };
        var result = await RoadmapMistakeLoader.LoadAsync(
            t.Db, Guid.NewGuid(), [sid], weaknesses, 100m, default);

        Assert.Equal(3, result.Count);
        // "Yeu" xét trước (yếu hơn), trong đó điểm thấp nhất (yeu-1) trước yeu-2 → m1, m2.
        var byKey = result.OrderBy(r => r.MistakeKey, StringComparer.Ordinal).ToList();
        Assert.Equal("m1", result.First(r => r.Reasoning == "yeu-1").MistakeKey);
        Assert.Equal("m2", result.First(r => r.Reasoning == "yeu-2").MistakeKey);
        Assert.Equal("m3", result.First(r => r.Reasoning == "donhe-3").MistakeKey);
        Assert.Equal(3, byKey.Select(r => r.MistakeKey).Distinct().Count()); // không trùng key
    }

    [Fact]
    public async Task BoQua_AnswerSkipped_VaTranscriptRong()
    {
        using var t = new TestDb();
        var crit = AiCriterion();
        var sid = AddSession(t, Guid.NewGuid());
        AddMistakeAnswer(t, sid, crit, 1, "answer skipped", status: AnswerStatus.Skipped);
        AddMistakeAnswer(t, sid, crit, 1, "transcript rong", transcript: "");
        AddMistakeAnswer(t, sid, crit, 1, "transcript null", transcript: null);
        AddMistakeAnswer(t, sid, crit, 1, "hop le", transcript: "câu trả lời thật");
        await t.Db.SaveChangesAsync();

        var result = await RoadmapMistakeLoader.LoadAsync(
            t.Db, Guid.NewGuid(), [sid], [new RoadmapWeakness("Clarity", 20, [crit.Id])], 50m, default);

        var mistake = Assert.Single(result);
        Assert.Equal("hop le", mistake.Reasoning);
    }

    [Fact]
    public async Task BoQua_ReasoningRongHoacNull()
    {
        using var t = new TestDb();
        var crit = AiCriterion();
        var sid = AddSession(t, Guid.NewGuid());
        AddMistakeAnswer(t, sid, crit, 1, null);
        AddMistakeAnswer(t, sid, crit, 1, "");
        AddMistakeAnswer(t, sid, crit, 1, "lý do thật");
        await t.Db.SaveChangesAsync();

        var result = await RoadmapMistakeLoader.LoadAsync(
            t.Db, Guid.NewGuid(), [sid], [new RoadmapWeakness("Clarity", 20, [crit.Id])], 50m, default);

        var mistake = Assert.Single(result);
        Assert.Equal("lý do thật", mistake.Reasoning);
    }

    [Fact]
    public async Task BoQua_AttemptKhac1_ChongSelfConsistencyDemTrung()
    {
        using var t = new TestDb();
        var crit = AiCriterion();
        var sid = AddSession(t, Guid.NewGuid());
        AddMistakeAnswer(t, sid, crit, 1, "chuẩn - attempt 1", attemptNo: 1);
        AddMistakeAnswer(t, sid, crit, 1, "nhiễu - attempt 2", attemptNo: 2);
        await t.Db.SaveChangesAsync();

        var result = await RoadmapMistakeLoader.LoadAsync(
            t.Db, Guid.NewGuid(), [sid], [new RoadmapWeakness("Clarity", 20, [crit.Id])], 50m, default);

        var mistake = Assert.Single(result);
        Assert.Equal("chuẩn - attempt 1", mistake.Reasoning);
    }

    [Fact]
    public async Task ChiTaiAnswerTrongDanhSachSessionId_KhongLoBuoiKhac()
    {
        using var t = new TestDb();
        var crit = AiCriterion();
        var candidate = Guid.NewGuid();
        var sidTrong = AddSession(t, candidate);
        var sidNgoai = AddSession(t, Guid.NewGuid());
        AddMistakeAnswer(t, sidTrong, crit, 3, "trong phạm vi");
        AddMistakeAnswer(t, sidNgoai, crit, 1, "NGOÀI phạm vi — điểm thấp hơn nhưng KHÔNG được lấy");
        await t.Db.SaveChangesAsync();

        var result = await RoadmapMistakeLoader.LoadAsync(
            t.Db, Guid.NewGuid(), [sidTrong], [new RoadmapWeakness("Clarity", 20, [crit.Id])], 90m, default);

        var mistake = Assert.Single(result);
        Assert.Equal("trong phạm vi", mistake.Reasoning);
    }

    [Fact]
    public async Task KhopTheoCriterionId_KhongTheoTen_HaiTieuChiCungTenKhacId()
    {
        // Rubric đổi version giữa các buổi ⇒ "cùng tên" nhưng KHÁC id. CriterionIds chỉ mang id CŨ
        // → chỉ answer gắn đúng id đó được lấy, answer của tiêu chí "Clarity" phiên bản MỚI (id khác)
        // dù cùng tên vẫn phải bị loại (đây là lý do CẤM "khớp theo tên" trong đề bài).
        using var t = new TestDb();
        var critCu = AiCriterion("Clarity", maxScore: 10);
        var critMoi = TestDb.Criterion(JobCategory.BE, version: 2, name: "Clarity");
        var sid = AddSession(t, Guid.NewGuid());
        AddMistakeAnswer(t, sid, critCu, 1, "answer gắn tiêu chí CŨ");
        AddMistakeAnswer(t, sid, critMoi, 1, "answer gắn tiêu chí MỚI — id khác");
        await t.Db.SaveChangesAsync();

        var result = await RoadmapMistakeLoader.LoadAsync(
            t.Db, Guid.NewGuid(), [sid], [new RoadmapWeakness("Clarity", 20, [critCu.Id])], 50m, default);

        var mistake = Assert.Single(result);
        Assert.Equal("answer gắn tiêu chí CŨ", mistake.Reasoning);
        Assert.Equal(critCu.Id, mistake.CriterionId);
    }

    [Fact]
    public async Task MaxScoreBangKhong_KhongLamNo()
    {
        // MaxScore=0 hiếm khi lọt qua WHERE thật (nhân chéo score*100 < threshold*0=0 chỉ đúng khi
        // score âm), nhưng phép tính ScorePct ở tầng C# (SAU khi đã materialize) phải tự nó không
        // chia-cho-0 dù dữ liệu có bất thường thế nào — test thẳng bằng cách ép score âm để lọt WHERE.
        using var t = new TestDb();
        var crit = AiCriterion("ZeroMax", maxScore: 0);
        var sid = AddSession(t, Guid.NewGuid());
        AddMistakeAnswer(t, sid, crit, -1, "score âm, maxScore=0");
        await t.Db.SaveChangesAsync();

        var result = await RoadmapMistakeLoader.LoadAsync(
            t.Db, Guid.NewGuid(), [sid], [new RoadmapWeakness("ZeroMax", 20, [crit.Id])], 100m, default);

        var mistake = Assert.Single(result);
        Assert.Equal(0m, mistake.ScorePct);
    }

    [Fact]
    public async Task GiuLaiSampleAnswerNull_VaGiuNguyenVanKhongCatTran()
    {
        using var t = new TestDb();
        var crit = AiCriterion();
        var sid = AddSession(t, Guid.NewGuid());
        var longReasoning = new string('x', 1000);
        AddMistakeAnswer(t, sid, crit, 1, longReasoning, sampleAnswer: null);
        await t.Db.SaveChangesAsync();

        var result = await RoadmapMistakeLoader.LoadAsync(
            t.Db, Guid.NewGuid(), [sid], [new RoadmapWeakness("Clarity", 20, [crit.Id])], 50m, default);

        var mistake = Assert.Single(result);
        Assert.Null(mistake.SampleAnswer);
        // KHÔNG cắt trần độ dài lúc lưu (khác RoadmapEvidenceLoader) — cắt là việc của lúc GỬI (B5).
        Assert.Equal(longReasoning.Length, mistake.Reasoning.Length);
    }

    [Fact]
    public async Task RoadmapIdDuocGanDungChoMoiHang()
    {
        using var t = new TestDb();
        var crit = AiCriterion();
        var sid = AddSession(t, Guid.NewGuid());
        AddMistakeAnswer(t, sid, crit, 1, "lý do");
        await t.Db.SaveChangesAsync();

        var roadmapId = Guid.NewGuid();
        var result = await RoadmapMistakeLoader.LoadAsync(
            t.Db, roadmapId, [sid], [new RoadmapWeakness("Clarity", 20, [crit.Id])], 50m, default);

        Assert.Equal(roadmapId, Assert.Single(result).RoadmapId);
    }
}
