using Isas.InterviewService.ApplicationDbContext;
using Isas.InterviewService.DTOs;
using Isas.InterviewService.Entities;
using Isas.InterviewService.Enums;
using Microsoft.EntityFrameworkCore;

namespace Isas.InterviewService.Services;

/// <summary>
/// MIS1-B4 — trích tối đa <see cref="MaxCriteria"/> × <see cref="MaxMistakesPerCriterion"/> LỖI SAI
/// cụ thể (không phải trích dẫn nguyên câu như <see cref="RoadmapEvidenceLoader"/>) làm nguyên liệu
/// cho AI gom vào milestone (MIS1-B2) và anchor bài giảng (MIS1-B3).
///
/// <c>mistake_key</c> ("m1".."m12") MINT Ở ĐÂY, MỘT LẦN, theo ĐÚNG thứ tự đã sort (tiêu chí YẾU
/// NHẤT trước, trong mỗi tiêu chí answer ĐIỂM THẤP NHẤT trước, tie-break AnswerId). Không nơi nào
/// khác được re-derive key này từ chỉ số mảng — <c>RoadmapMistake.MistakeKey</c> lưu nguyên chuỗi.
///
/// CHƯA được ai gọi (B5 sẽ nối vào <see cref="RoadmapService.CreateAsync"/>) — hàm này CHỈ trích,
/// KHÔNG <c>Add</c>/<c>SaveChanges</c> — caller quyết định lúc nào persist.
/// </summary>
public static class RoadmapMistakeLoader
{
    /// <summary>Tối đa bao nhiêu tiêu chí yếu được trích lỗi — tiêu chí YẾU NHẤT trước.</summary>
    public const int MaxCriteria = 4;

    /// <summary>Tối đa bao nhiêu lỗi/tiêu chí — answer ĐIỂM THẤP NHẤT trước.</summary>
    public const int MaxMistakesPerCriterion = 3;

    /// <summary>
    /// Chọn tối đa <see cref="MaxCriteria"/> tiêu chí YẾU NHẤT trong <paramref name="weaknesses"/>
    /// (đã là tập <c>NeedsImprovement</c> — "yếu nhất TRONG SỐ đã yếu", không phải yếu nhất toàn cục);
    /// với mỗi tiêu chí, tải tối đa <see cref="MaxMistakesPerCriterion"/> answer <c>Ai</c>-scoring
    /// dưới ngưỡng <paramref name="thresholdPct"/> trong đúng <paramref name="sessionIds"/>, điểm
    /// THẤP NHẤT trước.
    ///
    /// Khớp theo <c>CriterionId</c> (KHÔNG khớp theo tên — tên là snapshot điểm-tại-thời-điểm, còn
    /// rubric_criteria là giá trị SỐNG hiện đang sửa được; admin đổi tên tiêu chí sẽ làm khớp-theo-tên
    /// gãy vĩnh viễn). Bỏ tiêu chí <c>DeliveryMetrics</c> (chấm bằng VAD, không phải văn bản — trình
    /// bày cho người học như "bạn đã nói X" là vô nghĩa). Bỏ answer <c>Skipped</c>/transcript rỗng.
    ///
    /// Trần LUÔN LÀ 4×3=12, CỐ Ý cứng ở đây — không nhận tham số tuỳ ý từ caller.
    /// </summary>
    public static async Task<IReadOnlyList<RoadmapMistake>> LoadAsync(
        InterviewDbContext db,
        Guid roadmapId,
        IReadOnlyList<Guid> sessionIds,
        IReadOnlyList<RoadmapWeakness> weaknesses,
        decimal thresholdPct,
        CancellationToken ct)
    {
        if (sessionIds.Count == 0 || weaknesses.Count == 0) return [];

        var result = new List<RoadmapMistake>();
        var seq = 0;

        foreach (var w in weaknesses.OrderBy(x => x.Percentage).Take(MaxCriteria))
        {
            // CriterionIds nullable (rubric_criteria có Version — 1 tên có thể ứng nhiều id qua các
            // buổi). Không có id nào để lọc theo → bỏ qua tiêu chí này, KHÔNG khớp theo tên.
            if (w.CriterionIds is not { Count: > 0 }) continue;
            var ids = w.CriterionIds;

            var rows = await db.AnswerScores.AsNoTracking()
                .Where(s => s.AttemptNo == 1 // chấm CHUẨN (temperature=0) — bỏ self-consistency E10
                            && sessionIds.Contains(s.Answer.SessionId)
                            && ids.Contains(s.CriterionId)
                            && s.Criterion.ScoringMethod == CriterionScoringMethod.Ai
                            // Nhân chéo — KHÔNG chia (s.Score*100/s.Criterion.MaxScore): Postgres
                            // không đảm bảo thứ tự đánh giá vế AND, guard MaxScore>0 không chắc chạy
                            // trước phép chia. Nhân chéo hết cửa chia-0, khỏi cần guard ở SQL.
                            && s.Score * 100m < thresholdPct * s.Criterion.MaxScore
                            && s.Reasoning != null && s.Reasoning != ""
                            && s.Answer.Status != AnswerStatus.Skipped
                            && s.Answer.Transcript != null && s.Answer.Transcript != "")
                .OrderBy(s => s.Score).ThenBy(s => s.AnswerId)
                .Take(MaxMistakesPerCriterion)
                .Select(s => new
                {
                    s.AnswerId,
                    s.CriterionId,
                    CriterionName = s.Criterion.Name,
                    Question = s.Answer.Question.Content,
                    Answer = s.Answer.Transcript!,
                    Reasoning = s.Reasoning!,
                    SampleAnswer = s.Answer.SampleAnswer,
                    s.Score,
                    MaxScore = s.Criterion.MaxScore,
                })
                .ToListAsync(ct);

            foreach (var r in rows)
            {
                seq++;
                result.Add(new RoadmapMistake
                {
                    Id = Guid.NewGuid(),
                    RoadmapId = roadmapId,
                    MistakeKey = $"m{seq}",
                    CriterionId = r.CriterionId,
                    CriterionName = r.CriterionName,
                    AnswerId = r.AnswerId,
                    Question = r.Question,
                    Answer = r.Answer,
                    Reasoning = r.Reasoning,
                    SampleAnswer = r.SampleAnswer,
                    // C# side, SAU khi đã materialize (.ToListAsync xong) — KHÔNG dịch xuống SQL nên
                    // guard chia-0 ở đây an toàn, khác vế lọc SQL phía trên phải né chia bằng nhân chéo.
                    ScorePct = r.MaxScore > 0 ? r.Score * 100m / r.MaxScore : 0m,
                    ThresholdPct = thresholdPct,
                    CreatedAt = DateTime.UtcNow,
                });
            }
        }
        return result;
    }
}
