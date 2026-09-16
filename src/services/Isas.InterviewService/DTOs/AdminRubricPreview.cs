namespace Isas.InterviewService.DTOs;

using Isas.InterviewService.Enums;

/// <param name="Question">
/// Câu hỏi admin TỰ GÕ. Ưu tiên cao nhất; bỏ trống thì xét <paramref name="SampleQuestionId"/>.
/// <para>⚠ CỐ Ý không cho chọn từ <c>practice_questions</c> thật: câu B2C sinh từ CV/JD của chính
/// người dùng nên chứa tên công ty/dự án của họ — hiện cho admin là rò rỉ dữ liệu.</para>
/// </param>
/// <param name="SampleQuestionId">
/// Id một câu trong <c>sampleQuestions</c> mà <c>GET /api/admin/rubrics/{jobCategory}</c> vừa trả.
/// Id không thuộc danh sách của (nghề, ngôn ngữ) đang chạy → <b>400 nêu rõ</b>, KHÔNG âm thầm rơi về
/// câu mặc định: rơi âm thầm thì admin tưởng mình đang kiểm chứng câu A còn hệ thống chấm câu B.
/// Bỏ trống cả hai → câu đầu trong bộ mẫu.
/// </param>
/// <param name="CustomAnswer">Bài thứ tư do admin tự dán — bài DUY NHẤT không do bộ chấm viết ra.</param>
public record AdminRubricPreviewRequest(
    string? Question = null,
    string? CustomAnswer = null,
    string? Seniority = null,
    string? SampleQuestionId = null,
    /// <summary>
    /// <c>false</c> ⇒ KHÔNG bắt AI viết 3 bài mẫu, chỉ chấm <see cref="CustomAnswer"/> (bắt buộc có).
    /// Mặc định <c>true</c> để hợp đồng cũ không đổi. Người chỉ muốn biết "hệ chấm TÔI thế nào"
    /// không cần 3 bài AI (1 lượt sinh + 3 lượt chấm, 30–60s).
    /// </summary>
    bool IncludeAiSamples = true,
    /// <summary>
    /// Số đo cách nói của CHÍNH bản ghi người dùng (từ <c>preview/transcribe</c>). Có nó, bài của họ
    /// được chấm với khối số đo thật (F11) và tiêu chí đo-bằng-máy (trôi chảy) được đo luôn — lần đầu
    /// chấm thử đo được cả tiêu chí này. Bài dán tay không có ⇒ tiêu chí đó không chấm (FE nói rõ).
    /// </summary>
    DeliveryMetricsDto? DeliveryMetrics = null
);

/// <summary>Kết quả chép lời cho màn tự thử: bản chép (sửa được trước khi chấm) + số đo cách nói.</summary>
/// <param name="NoSpeech">Cổng VAD không thấy tiếng nói — bản ghi trống/quá nhỏ; FE bảo ghi lại.</param>
public record AdminPreviewTranscribeResponse(
    string Transcript, DeliveryMetricsDto? DeliveryMetrics, string? TranscriptEngine, bool NoSpeech);

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
/// <param name="DeliveryMetrics">Chỉ bài <c>Custom</c> có bản ghi âm mới mang số đo; 3 bài AI luôn <c>null</c>.</param>
public record AdminPreviewSample(
    string Band, string AnswerText, int WordCount,
    decimal ExpectedPct, decimal ActualPct,
    IReadOnlyList<AdminPreviewSampleScore> Scores,
    DeliveryMetricsDto? DeliveryMetrics = null);

/// <param name="Measured"><c>true</c> = điểm ĐO từ bản ghi (DeliveryFluencyScorer), không do AI chấm — FE gắn nhãn, không đoán theo chữ.</param>
public record AdminPreviewSampleScore(
    Guid CriterionId, string CriterionName, int MaxScore,
    int ExpectedLevel, decimal ActualScore, int? LevelMatched, string? Reasoning,
    bool Measured = false);

/// <summary>Mốc AI gợi ý cho một tiêu chí — trả về để admin xem/sửa, KHÔNG ghi DB.</summary>
public record AdminSuggestLevelsResponse(
    JobCategory JobCategory, string Language, int RubricVersion,
    IReadOnlyList<AdminSuggestedCriterionLevels> Criteria);

public record AdminSuggestedCriterionLevels(
    Guid CriterionId, string Name, int MaxScore, IReadOnlyList<AdminRubricLevelItem> Levels);
