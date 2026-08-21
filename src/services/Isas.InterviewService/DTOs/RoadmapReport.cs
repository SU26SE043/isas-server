namespace Isas.InterviewService.DTOs;

// BC15 (D20) — report roadmap ôn tập cá nhân hoá B2C. Interim (Active) tính on-read từ
// session_criterion_scores (BC9); Final (Completed) đọc snapshot roadmaps.final_report — KHÔNG tính lại.

// GET /roadmaps/{id}/report. radar + levelEvaluation luôn có (kể cả interim); kết luận (AI) rỗng/null khi interim.
public record RoadmapReportResponse(
    IReadOnlyList<CriterionScoreResponse> Radar,          // avg % per tiêu chí qua các session thuộc roadmap
    IReadOnlyList<RoadmapLevelEvaluationResponse> LevelEvaluation,
    IReadOnlyList<string> Strengths,                       // kết luận chi tiết — AI /summarize-roadmap (best-effort)
    IReadOnlyList<string> Weaknesses,
    IReadOnlyList<string> Improvements,                    // cần cải thiện + gợi ý luyện tiếp
    string? OverallComment,
    // Trạng thái roadmap tại thời điểm đọc. Client dùng để biết đây là báo cáo TẠM THỜI (Active,
    // tính on-read) hay báo cáo CUỐI (Completed, đọc snapshot đã chốt) — hai thứ khác nhau về ý
    // nghĩa: bản cuối sẽ không đổi nữa, bản tạm thời còn dịch chuyển theo mỗi buổi luyện.
    //
    // Thiếu field này thì client KHÔNG có cách nào phân biệt: đo trên deploy, màn báo cáo của một
    // roadmap đã Completed vẫn ghi "Báo cáo tạm thời" vì frontend đã có sẵn nhánh suy từ status
    // (`status === 'completed'` → snapshot) nhưng response chưa bao giờ mang status.
    string RoadmapStatus
);

// Đánh giá 1 tiêu chí theo ngưỡng level (Fresher 50 · Junior 60 · Middle 70 · Senior 80).
public record RoadmapLevelEvaluationResponse(
    string CriterionName,
    decimal Percentage,      // avg % tiêu chí (= radar)
    int LevelThreshold,      // ngưỡng đạt theo level roadmap
    bool Passed              // percentage ≥ levelThreshold
);

// Tiến độ 1 tiêu chí gửi xuống AIService /summarize-roadmap: startPct (baseline lúc tạo) → endPct (radar cuối).
public record RoadmapCriteriaProgress(
    string CriterionName,
    decimal? StartPct,       // baseline % lúc tạo roadmap (null nếu chưa có buổi nào lúc tạo)
    decimal EndPct,          // radar % hiện tại
    int LevelThreshold,
    bool Passed
);

// Kết quả AI /summarize-roadmap (best-effort). AI lỗi → caller để rỗng/null (KHÔNG chặn Completed).
public record RoadmapSummaryAiResult(
    IReadOnlyList<string> Strengths,
    IReadOnlyList<string> Weaknesses,
    IReadOnlyList<string> Improvements,
    string? OverallComment
);
