namespace Isas.InterviewService.DTOs;

using Isas.InterviewService.Enums;

/// <param name="Question">
/// Câu hỏi đem chấm thử. Bỏ trống → lấy câu đầu trong bộ mẫu hằng số của (nghề, ngôn ngữ).
/// <para>⚠ CỐ Ý không cho chọn từ <c>practice_questions</c> thật: câu B2C sinh từ CV/JD của chính
/// người dùng nên chứa tên công ty/dự án của họ — hiện cho admin là rò rỉ dữ liệu.</para>
/// </param>
/// <param name="CustomAnswer">Bài thứ tư do admin tự dán — bài DUY NHẤT không do bộ chấm viết ra.</param>
public record AdminRubricPreviewRequest(
    string? Question = null,
    string? CustomAnswer = null,
    string? Seniority = null
);

public record AdminRubricPreviewRunResponse(
    Guid Id,
    string Status,
    JobCategory JobCategory,
    string Language,
    int RubricVersion,
    string QuestionText,
    string RubricFingerprint,
    int? PromptVersion,
    /// <summary>
    /// Luôn <c>false</c> ở bản này: bài mẫu là VĂN BẢN nên không có số đo cách nói (F11). Cờ cấu trúc,
    /// KHÔNG loại tiêu chí "trôi chảy" khỏi lượt chấm — bỏ một tiêu chí sẽ đổi điểm các tiêu chí còn
    /// lại và đổi mẫu số trung bình cộng (INT-10), tức đo một thước đo khác với thước thật.
    /// </summary>
    bool DeliveryMetricsAvailable,
    bool LengthParityWarning,
    int FreeRunsRemaining,
    IReadOnlyList<AdminPreviewRubricCriterion> Rubric,
    IReadOnlyList<AdminPreviewSample> Samples,
    string? ErrorReason,
    DateTime CreatedAt,
    DateTime? CompletedAt
);

public record AdminPreviewRubricCriterion(
    Guid CriterionId, string Name, decimal Weight, int MaxScore,
    IReadOnlyList<AdminRubricLevelItem> Levels);

/// <param name="ExpectedPct">
/// Điểm KỲ VỌNG quy về %, tính bằng TRUNG BÌNH CỘNG các tiêu chí — đúng công thức B2C (INT-10), KHÔNG
/// dùng weight như B2B. Dùng nhầm công thức weighted ở đây thì báo cáo chấm thử đo một thang khác với
/// thang người luyện thật nhận, mà cả hai đều ra số trông hợp lý.
/// </param>
public record AdminPreviewSample(
    string Band, string AnswerText, int WordCount,
    decimal ExpectedPct, decimal ActualPct,
    IReadOnlyList<AdminPreviewSampleScore> Scores);

public record AdminPreviewSampleScore(
    Guid CriterionId, string CriterionName, int MaxScore,
    int ExpectedLevel, decimal ActualScore, int? LevelMatched, string? Reasoning);

/// <summary>Mốc AI gợi ý cho một tiêu chí — trả về để admin xem/sửa, KHÔNG ghi DB.</summary>
public record AdminSuggestLevelsResponse(
    JobCategory JobCategory, string Language, int RubricVersion,
    IReadOnlyList<AdminSuggestedCriterionLevels> Criteria);

public record AdminSuggestedCriterionLevels(
    Guid CriterionId, string Name, int MaxScore, IReadOnlyList<AdminRubricLevelItem> Levels);
