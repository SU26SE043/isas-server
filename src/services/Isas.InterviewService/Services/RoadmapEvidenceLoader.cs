using Isas.InterviewService.ApplicationDbContext;
using Isas.InterviewService.DTOs;
using Microsoft.EntityFrameworkCore;

namespace Isas.InterviewService.Services;

/// <summary>
/// BE-5 — trích <c>Reasoning</c> (E11, luôn trích NGUYÊN VĂN lời ứng viên) của các answer điểm
/// THẤP NHẤT cho từng tiêu chí YẾU, làm bằng chứng hành vi cụ thể thay cho con số % trừu tượng.
///
/// <c>answer_scores</c> có 1.616 dòng Reasoning (trung bình 225 ký tự) mà trước bản này không có
/// code nào đọc lại. Khác biệt cho AI: "Tư duy giải quyết vấn đề — 40%" chỉ là tiêu đề sách giáo
/// khoa; "Câu trả lời không cân nhắc đánh đổi khi ưu tiên tính năng với nguồn lực hạn chế" dạy
/// đúng thứ còn thiếu.
///
/// Dùng CHUNG cho cả roadmap prompt (lúc tạo — <see cref="RoadmapService"/>) lẫn lesson-theory
/// prompt (lúc mở bài — <see cref="RoadmapLessonService"/>): hai call site độc lập, cùng một logic
/// tải + cùng một trần, KHÔNG lưu DB (roadmap không có cột nào giữ evidence — tính lại mỗi lần).
/// </summary>
public static class RoadmapEvidenceLoader
{
    /// <summary>Tối đa bao nhiêu tiêu chí yếu được cấp bằng chứng — chọn tiêu chí YẾU NHẤT trước.</summary>
    public const int MaxCriteria = 3;

    /// <summary>Tối đa bao nhiêu answer/tiêu chí — answer ĐIỂM THẤP NHẤT trước.</summary>
    public const int MaxAnswersPerCriterion = 3;

    // Reasoning không có max length ở DB (text) — chặn 1 answer bất thường dài nuốt hết ngân sách
    // ~2.000 ký tự đề bài nêu (3 tiêu chí × 3 answer). Trung bình đo được 225 ký tự; 400 đã rộng
    // hơn gấp rưỡi nên gần như không bao giờ chạm ngưỡng trên dữ liệu thật.
    private const int MaxReasoningCharsPerQuote = 400;

    /// <summary>
    /// Chọn tối đa <see cref="MaxCriteria"/> tiêu chí YẾU NHẤT (percentage tăng dần, tên phân
    /// biệt) trong <paramref name="weaknesses"/>; với mỗi tiêu chí đó, tải tối đa
    /// <see cref="MaxAnswersPerCriterion"/> <c>Reasoning</c> của answer ĐIỂM THẤP NHẤT trong đúng
    /// các buổi ở <paramref name="sessionIds"/>. Trần CỐ Ý cứng ở đây, KHÔNG nhận tham số từ
    /// caller — mở ra tham số tuỳ ý là mở lại đúng lỗ "gửi hết mọi answer" mà trần này sinh ra để
    /// chặn (đề bài BE-5 mục 4).
    ///
    /// Answer thiếu <c>Reasoning</c> (rỗng/null) bị bỏ qua — không có gì để trích. Không session
    /// nào / không tiêu chí yếu nào → rỗng.
    /// </summary>
    public static async Task<IReadOnlyList<CriterionEvidence>> LoadAsync(
        InterviewDbContext db,
        IReadOnlyList<Guid> sessionIds,
        IReadOnlyList<RoadmapWeakness> weaknesses,
        CancellationToken ct)
    {
        if (sessionIds.Count == 0 || weaknesses.Count == 0) return [];

        var weakestNames = weaknesses
            .OrderBy(w => w.Percentage)
            .Select(w => w.CriterionName)
            .Distinct()
            .Take(MaxCriteria)
            .ToList();

        var result = new List<CriterionEvidence>();
        foreach (var name in weakestNames)
        {
            // AttemptNo == 1 — bản chấm CHUẨN (temperature=0, tất định). N>1 (self-consistency,
            // E10) sinh thêm AnswerScore cho CÙNG 1 answer+tiêu chí; không lọc thì mỗi attempt bị
            // đếm như một "answer khác" và ăn hụt suất của MaxAnswersPerCriterion.
            var quotes = await db.AnswerScores.AsNoTracking()
                .Where(s => s.AttemptNo == 1
                            && s.Criterion.Name == name
                            && sessionIds.Contains(s.Answer.SessionId)
                            && s.Reasoning != null && s.Reasoning != "")
                .OrderBy(s => s.Score)
                .Select(s => s.Reasoning!)
                .Take(MaxAnswersPerCriterion)
                .ToListAsync(ct);

            if (quotes.Count == 0) continue;
            result.Add(new CriterionEvidence(
                name, quotes.Select(q => Truncate(q, MaxReasoningCharsPerQuote)).ToList()));
        }
        return result;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
