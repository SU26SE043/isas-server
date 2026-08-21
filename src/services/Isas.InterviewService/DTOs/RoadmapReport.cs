namespace Isas.InterviewService.DTOs;

// BC15 (D20) — report roadmap ôn tập cá nhân hoá B2C. Interim (Active) tính on-read từ
// session_criterion_scores (BC9); Final (Completed) đọc snapshot roadmaps.final_report — KHÔNG tính lại.

// GET /roadmaps/{id}/report. radar + levelEvaluation luôn có (kể cả interim); kết luận (AI) rỗng/null khi interim.
public record RoadmapReportResponse(
    IReadOnlyList<RoadmapRadarCriterionResponse> Radar,   // xu hướng GẦN ĐÂY per tiêu chí (xem record bên dưới)
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
    string RoadmapStatus,
    // Diễn tiến TỪNG BUỔI theo thời gian — lớp dữ liệu mà radar (dù đã thu về cửa sổ gần đây) vẫn
    // không thể hiện: radar trả lời "đang ở đâu", progress trả lời "đi lên hay đi xuống".
    //
    // ⚠ Snapshot final_report lưu TRƯỚC bản này KHÔNG có khoá `progress` ⇒ deserialize ra null.
    // Đường đọc phải chuẩn hoá về [] (xem RoadmapReportService.GetReportAsync).
    IReadOnlyList<RoadmapSessionProgressResponse> Progress
);

/// <summary>
/// Một nan radar của báo cáo lộ trình. KHÔNG dùng lại <see cref="CriterionScoreResponse"/> (BC9):
/// record đó mô tả điểm của MỘT buổi luyện và đang nằm trong payload kết quả buổi — nhét thêm
/// trường xu hướng vào đó là phình payload của một màn hình không dùng tới chúng.
/// </summary>
/// <param name="Percentage">
/// Trung bình % qua <b>tối đa 3 buổi GẦN NHẤT</b> có chấm tiêu chí này (KHÔNG phải mọi buổi).
/// Đây là con số <see cref="RoadmapLevelEvaluationResponse"/> dùng để kết luận Đạt/Chưa đạt.
/// </param>
/// <param name="AverageScore">Điểm thô trung bình, CÙNG cửa sổ buổi với <paramref name="Percentage"/>.</param>
/// <param name="StartPercentage">
/// % ở buổi ĐẦU TIÊN có chấm tiêu chí này — mốc để người học thấy mình đi từ đâu tới.
/// <c>null</c> khi tiêu chí mới chỉ có đúng 1 buổi: không có gì để so, và bịa ra một mốc bằng chính
/// điểm hiện tại sẽ hiện thành "tiến bộ 0%" thay vì "chưa đủ dữ liệu" (BK23 — <c>null</c> nghĩa là
/// KHÔNG BIẾT, đừng vẽ thành số).
/// </param>
/// <param name="SessionCount">
/// Tổng số buổi có chấm tiêu chí này — CỠ MẪU. Các nan radar KHÔNG bằng nhau về cỡ mẫu: INT-18 chỉ
/// chấm tiêu chí nội dung khi câu hỏi nhắm tới nó, nên một nan có thể dựng trên 1 buổi còn nan bên
/// cạnh dựng trên 5. Client cần con số này để không trình bày hai nan như nhau về độ tin cậy.
/// </param>
/// <param name="RecentCount">Số buổi THỰC dùng cho <paramref name="Percentage"/> (≤ 3).</param>
public record RoadmapRadarCriterionResponse(
    Guid CriterionId,
    string Name,
    decimal MaxScore,
    decimal Weight,
    decimal Percentage,
    decimal AverageScore,
    decimal? StartPercentage,
    int SessionCount,
    int RecentCount
);

/// <summary>
/// Kết quả MỘT buổi luyện thuộc lộ trình, xếp theo thời gian được chấm. Dùng để vẽ đường xu hướng.
/// </summary>
/// <param name="Order">Thứ tự thời gian trong lộ trình, 1-based.</param>
/// <param name="CompletedAt">
/// Thời điểm buổi được CHẤM (mốc ghi <c>session_criterion_scores</c>), không phải lúc bấm nộp.
/// Lấy mốc sẵn có, KHÔNG thêm cột; và đây là mốc duy nhất chắc chắn tồn tại cho mọi dòng ở đây —
/// dòng progress chỉ sinh ra từ buổi ĐÃ có breakdown điểm.
/// </param>
/// <param name="OverallPercentage">Trung bình % các tiêu chí CỦA CHÍNH buổi đó (equal weight, như INT-10 B2C).</param>
public record RoadmapSessionProgressResponse(
    int Order,
    string LessonTitle,
    DateTime CompletedAt,
    decimal OverallPercentage,
    IReadOnlyList<RoadmapProgressCriterionResponse> Scores
);

// Điểm 1 tiêu chí trong 1 buổi (thang %). Gọn có chủ đích — đường xu hướng chỉ cần tên + %.
public record RoadmapProgressCriterionResponse(string Name, decimal Percentage);

// Đánh giá 1 tiêu chí theo ngưỡng level (Fresher 50 · Junior 60 · Middle 70 · Senior 80).
public record RoadmapLevelEvaluationResponse(
    string CriterionName,
    decimal Percentage,      // = radar (TB tối đa 3 buổi gần nhất), không phải TB mọi buổi
    int LevelThreshold,      // ngưỡng đạt theo level roadmap
    bool Passed              // percentage ≥ levelThreshold
);

// Tiến độ 1 tiêu chí gửi xuống AIService /summarize-roadmap: startPct (mốc xuất phát) → endPct (hiện tại).
public record RoadmapCriteriaProgress(
    string CriterionName,
    // Mốc xuất phát, ưu tiên: baseline lúc TẠO roadmap → % buổi ĐẦU TIÊN trong roadmap → null.
    // null = KHÔNG BIẾT (tiêu chí mới chỉ được chấm ở đúng 1 buổi và roadmap không có baseline).
    decimal? StartPct,
    decimal EndPct,          // radar % hiện tại (TB tối đa 3 buổi gần nhất)
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
