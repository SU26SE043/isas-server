using Isas.InterviewService.DTOs;
using Isas.InterviewService.Entities;

namespace Isas.InterviewService.Services;

// BC8 — dựng báo cáo "CV vs câu trả lời" cho buổi luyện B2C đã Scored.
// THUẦN ĐỌC dữ liệu SẴN CÓ — KHÔNG AI, KHÔNG call service ngoài:
//   • "CV mạnh"   = strengths/skill từ cv_analyses (BC7).
//   • "trả lời yếu" = tiêu chí needs_improvement từ session_criterion_scores (BC9, ngưỡng 50%).
//   • "gap"        = tiêu chí VỪA yếu VỪA được CV thể hiện mạnh — xác định deterministic bằng
//                    token overlap giữa TÊN tiêu chí và chuỗi strength/skill CV (không semantic AI).
// Hàm PURE (không DB) để unit-test độc lập; PracticeService lo phần load cv_analyses.
public static class CvVsAnswerReportBuilder
{
    // Từ generic/nối — lọc để giảm khớp giả (vd "and", "skills", "experience"). Case-insensitive.
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "and", "the", "for", "with", "strong", "good", "skill", "skills", "experience",
        "knowledge", "ability", "able", "using", "use", "years", "year", "level", "very",
        "kinh", "nghiem", "nghiệm", "kỹ", "năng", "tốt", "khả",
    };

    private static readonly char[] Separators =
        { ' ', ',', '.', ';', ':', '/', '\\', '(', ')', '[', ']', '-', '_', '+', '&', '\t', '\n', '\r' };

    // strengths rỗng (session không CV / CV chưa phân tích BC7) → báo cáo ABSENT (null), KHÔNG lỗi.
    public static CvVsAnswerReportResponse? Build(
        IReadOnlyList<string> cvStrengths,
        IReadOnlyList<SessionCriterionScore> criterionScores)
    {
        if (cvStrengths.Count == 0) return null;

        // Token hoá trước mỗi strength (giữ chuỗi gốc để trả làm bằng chứng).
        var strengthTokens = cvStrengths
            .Select(s => (Text: s, Tokens: Tokenize(s)))
            .ToList();

        var gaps = new List<CvAnswerGapResponse>();
        foreach (var cs in criterionScores)
        {
            if (!cs.NeedsImprovement) continue;   // chỉ tiêu chí answer YẾU (dưới ngưỡng)

            var critTokens = Tokenize(cs.CriterionName);
            if (critTokens.Count == 0) continue;

            // strength/skill CV có ≥1 token chung với tên tiêu chí → CV "thể hiện mạnh" tiêu chí này.
            var evidence = strengthTokens
                .Where(s => s.Tokens.Overlaps(critTokens))
                .Select(s => s.Text)
                .ToList();
            if (evidence.Count == 0) continue;

            gaps.Add(new CvAnswerGapResponse(
                cs.CriterionId, cs.CriterionName, cs.Percentage, cs.MaxScore, evidence));
        }

        return new CvVsAnswerReportResponse(cvStrengths, gaps);
    }

    private static HashSet<string> Tokenize(string text)
    {
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in text.Split(Separators, StringSplitOptions.RemoveEmptyEntries))
        {
            var w = raw.Trim();
            if (w.Length < 3) continue;          // bỏ token quá ngắn (nhiễu)
            if (StopWords.Contains(w)) continue; // bỏ từ generic
            tokens.Add(w);
        }
        return tokens;
    }
}
