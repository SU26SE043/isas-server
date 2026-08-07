using Isas.InterviewService.DTOs;
using Isas.InterviewService.Entities;

namespace Isas.InterviewService.Services;

/// <summary>
/// E9 — dựng danh sách tiêu chí (kèm mức neo <c>levels</c> + <c>anchors</c>) cho message chấm.
///
/// <para><b>Nguồn mức thống nhất:</b> nếu criterion CÓ <c>rubric_levels</c> (khai báo) → dùng;
/// nếu KHÔNG (B2B <c>campaign_criteria</c> / B2C chưa seed levels) → sinh <b>dải mặc định
/// <c>0..maxScore</c></b> ngay tại Interview (descriptor generic "Mức i/maxScore").</para>
///
/// <para>Nhờ vậy E9 (AI chọn mức khớp thay vì tự bịa thang) đúng cho <b>cả B2B & B2C</b> mà
/// KHÔNG cần đụng Campaign/<c>suggest-criteria</c>. Anchor (câu mẫu) chỉ có khi rubric_levels
/// khai — dải mặc định không có anchor.</para>
///
/// Dùng chung ở <see cref="AnswerService"/> (publish khi upload) và
/// <see cref="StuckAnswerRepublisher"/> (re-publish khi kẹt) để 2 đường build message giống nhau.
/// </summary>
public static class ScoringCriteriaBuilder
{
    public static List<ScoringCriterionDto> Build(IEnumerable<RubricCriterion> criteria)
        => criteria.Select(ToDto).ToList();

    private static ScoringCriterionDto ToDto(RubricCriterion c)
    {
        // Mức khai báo (nếu có): sắp theo score tăng dần.
        var declared = (c.Levels ?? [])
            .OrderBy(l => l.Score)
            .ToList();

        var levels = declared.Count > 0
            ? declared.Select(l => new ScoringLevelDto { Score = l.Score, Descriptor = l.Descriptor }).ToList()
            : DefaultBand(c.MaxScore, c.Language);

        // Anchor chỉ đến từ rubric_levels khai (DB15: câu mẫu nằm ở cột jsonb example_answers của mức);
        // dải mặc định không có câu mẫu. OUTPUT giữ nguyên hợp đồng cũ: {Score, ExampleAnswer} sort theo score.
        var anchors = declared
            .SelectMany(l => (l.ExampleAnswers ?? new List<string>())
                .Select(ex => new ScoringAnchorDto { Score = l.Score, ExampleAnswer = ex }))
            .OrderBy(a => a.Score)
            .ToList();

        return new ScoringCriterionDto
        {
            CriterionId = c.Id,
            Name = c.Name,
            Description = c.Description,
            MaxScore = c.MaxScore,
            Weight = c.Weight,
            Levels = levels,
            Anchors = anchors.Count > 0 ? anchors : null
        };
    }

    /// <summary>Dải mức mặc định <c>0..maxScore</c> khi tiêu chí không khai rubric_levels.</summary>
    public static List<ScoringLevelDto> DefaultBand(int maxScore, string language = "vi")
    {
        var top = Math.Max(maxScore, 0);
        return Enumerable.Range(0, top + 1)
            .Select(i => new ScoringLevelDto
            {
                Score = i,
                Descriptor = language == "en" ? $"Level {i}/{top}" : $"Mức {i}/{top}"
            })
            .ToList();
    }

    /// <summary>
    /// Tập điểm mức HỢP LỆ của 1 tiêu chí (để C# guard snap/validate ở callback):
    /// điểm các <c>rubric_levels</c> khai nếu có; nếu không → dải mặc định <c>0..maxScore</c>.
    /// </summary>
    public static IReadOnlyList<int> ValidLevelScores(IEnumerable<RubricLevel> declaredLevels, int maxScore)
    {
        var declared = declaredLevels
            .Select(l => l.Score)
            .Distinct()
            .OrderBy(s => s)
            .ToList();
        if (declared.Count > 0) return declared;

        var top = Math.Max(maxScore, 0);
        return Enumerable.Range(0, top + 1).ToList();
    }
}
