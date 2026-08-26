using Isas.InterviewService.Entities;
using Isas.InterviewService.Enums;

namespace Isas.InterviewService.Tests;

/// <summary>
/// MIS1-B6 — nguồn DÙNG CHUNG cho các bản sao <c>SeedScoredSession</c> trong 4 file test roadmap
/// (<c>RoadmapTests.cs</c> · <c>RoadmapReinforceModeTests.cs</c> · <c>RoadmapSourceJobCategoryTests.cs</c>
/// · <c>RoadmapLevelThresholdTests.cs</c>) — bốn hàm <c>private static</c> gần như giống hệt nhau,
/// mỗi file một bản chép tay. Bốn hàm CŨ vẫn giữ NGUYÊN chữ ký và vị trí (76 call site không phải
/// sửa) — chúng chỉ đổi thân hàm để GỌI VÀO đây.
///
/// <para>🔴 MIS1-B6 thêm việc mới cho 3 file gọi <see cref="ScoredSessionWithAnswers"/> (những file
/// đi qua <c>RoadmapService.CreateAsync</c>): roadmap nay XÂY TỪ LỖI THẬT
/// (<c>RoadmapMistakeLoader</c>, MIS1-B4/B5), nên một buổi chỉ có breakdown BC9
/// (<c>session_criterion_scores</c>) là KHÔNG ĐỦ để tạo được roadmap nữa — Guard 3
/// (<c>ROADMAP_NO_CONTENT_MISTAKES</c>) đòi ít nhất 1 <c>answer_scores</c> DƯỚI NGƯỠNG có
/// transcript + reasoning. <paramref name="seedContentMistakes"/>=<c>true</c> seed thêm đúng dữ
/// liệu đó cho MỖI tiêu chí <c>needsImprovement</c>.</para>
///
/// <para>🔴 <c>TestDb.Answer(...)</c> (Tests/TestDb.cs:204-217) KHÔNG set <c>Transcript</c> — thiếu
/// dòng gán tay ở đây thì <c>RoadmapMistakeLoader</c> trích được 0 lỗi (đòi
/// <c>Answer.Transcript != null &amp;&amp; != ""</c>) dù mọi thứ khác đúng, và triệu chứng là test
/// xanh giả hoặc đỏ vô cớ tuỳ ca. Đây CHÍNH LÀ lỗ đã có sẵn ở khuôn seed "đầy đủ" của
/// <c>RoadmapEvidenceLoaderTests.cs:26-56</c> (chép khuôn đó nguyên xi ⇒ trích được 0 lỗi) —
/// helper này CỐ Ý không chép khuôn đó, set <c>Transcript</c> tường minh ngay dưới đây.
/// (<c>ScoringMethod</c> thì an toàn: <c>RubricCriterion.ScoringMethod</c> mặc định = <c>Ai</c>.)</para>
///
/// <para><see cref="ScoredSessionForReport"/> KHÔNG dùng chung thân hàm với
/// <see cref="ScoredSessionWithAnswers"/>: <c>RoadmapLevelThresholdTests.cs</c> dựng
/// <c>Roadmap</c>/<c>RoadmapMilestone</c>/<c>RoadmapLesson</c> TRỰC TIẾP (không qua
/// <c>CreateAsync</c>) để test <c>RoadmapReportService</c> (BC15) — không cần content mistake, và
/// có 2 khác biệt hành vi CỐ Ý với 3 hàm kia (không dedupe tiêu chí theo tên — mỗi lần seed một
/// tiêu chí MỚI; <c>AverageScore</c> suy từ <c>%</c> chứ không phải hằng số). Ép nó dùng chung một
/// thân hàm với 3 file kia sẽ đổi 1 trong 2 hành vi đó một cách không cần thiết, cho một file
/// KHÔNG nằm trong danh sách đỏ của MIS1-B6 (nó không gọi <c>CreateAsync</c>) — giữ tách riêng,
/// chỉ SAO CHÉP NGUYÊN VĂN (không đổi observable) vào đây để bớt một bản chép tay.</para>
/// </summary>
internal static class TestSeed
{
    private static int _mistakeOrderCounter;

    public static Guid ScoredSessionWithAnswers(
        TestDb t, Guid candidateId, JobCategory jobCategory, bool seedContentMistakes,
        params (string name, decimal pct, bool needsImprovement)[] criteria)
    {
        var session = TestDb.Session(candidateId, SessionStatus.Scored, jobCategory);
        t.Db.PracticeSessions.Add(session);
        foreach (var (name, pct, needs) in criteria)
        {
            // Production chỉ có MỘT bộ tiêu chí seed cho mỗi (nghề, ngôn ngữ) — mọi buổi cùng nghề
            // trỏ vào chính nó (UNIQUE (job_category, language, version, name) khoá điều đó ở DB).
            var crit = t.Db.RubricCriteria.Local
                    .FirstOrDefault(c => c.Name == name && c.CandidateId == null && c.JobCategory == jobCategory)
                ?? t.Db.RubricCriteria.FirstOrDefault(
                    c => c.Name == name && c.CandidateId == null && c.JobCategory == jobCategory);
            if (crit is null)
            {
                crit = TestDb.Criterion(jobCategory, name: name);
                t.Db.RubricCriteria.Add(crit);
            }

            t.Db.SessionCriterionScores.Add(new SessionCriterionScore
            {
                Id = Guid.NewGuid(),
                SessionId = session.Id,
                CriterionId = crit.Id,
                CriterionName = name,
                AverageScore = 2m,
                MaxScore = crit.MaxScore,
                Percentage = pct,
                Weight = 1m,
                NeedsImprovement = needs,
                CreatedAt = DateTime.UtcNow
            });

            // MIS1-B6 — chỉ tiêu chí YẾU (needsImprovement) mới cần content mistake: `weaknesses`
            // (RoadmapService.CreateAsync) chỉ gom tiêu chí NeedsImprovement=true, nên
            // RoadmapMistakeLoader chỉ bao giờ nhìn tới những tiêu chí đó.
            if (needs && seedContentMistakes)
            {
                var question = TestDb.Question(session.Id, order: ++_mistakeOrderCounter);
                t.Db.PracticeQuestions.Add(question);
                var answer = TestDb.Answer(
                    session.Id, question.Id, AnswerStatus.Scored, DateTime.UtcNow, DateTime.UtcNow);
                answer.Transcript = "Câu trả lời của ứng viên cho " + name;   // BẮT BUỘC — xem tóm tắt lớp trên.
                t.Db.PracticeAnswers.Add(answer);
                t.Db.AnswerScores.Add(new AnswerScore
                {
                    Id = Guid.NewGuid(),
                    AnswerId = answer.Id,
                    CriterionId = crit.Id,
                    AttemptNo = 1,
                    // 1/5 = 20% < mọi ngưỡng hợp lý (mặc định ImprovementThresholdPct=50) — LUÔN
                    // dưới ngưỡng bất kể `pct` của SessionCriterionScore (hai bảng độc lập).
                    Score = 1,
                    Reasoning = $"Chưa nắm vững {name}.",
                    RubricVersion = 1,
                    CreatedAt = DateTime.UtcNow
                });
            }
        }
        t.Db.SaveChanges();
        return session.Id;
    }

    // Sao chép NGUYÊN VĂN thân hàm cũ của RoadmapLevelThresholdTests.cs — xem giải thích "vì sao
    // tách riêng" ở tóm tắt lớp trên. KHÔNG đổi bất kỳ giá trị observable nào.
    public static Guid ScoredSessionForReport(
        TestDb t, Guid candidateId, string criterionName, decimal pct)
    {
        var at = DateTime.UtcNow;
        var session = TestDb.Session(candidateId, SessionStatus.Scored, createdAt: at);
        var criterion = TestDb.Criterion(JobCategory.BE, name: criterionName);
        t.Db.PracticeSessions.Add(session);
        t.Db.RubricCriteria.Add(criterion);
        t.Db.SessionCriterionScores.Add(new SessionCriterionScore
        {
            Id = Guid.NewGuid(),
            SessionId = session.Id,
            CriterionId = criterion.Id,
            CriterionName = criterionName,
            AverageScore = Math.Round(pct / 20m, 2),   // MaxScore 5 ⇒ pct = avg/5*100
            MaxScore = 5,
            Percentage = pct,
            Weight = 1m,
            NeedsImprovement = pct < 50m,
            CreatedAt = at
        });
        t.Db.SaveChanges();
        return session.Id;
    }
}
