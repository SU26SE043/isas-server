namespace Isas.InterviewService.Models;

// BC9 — cấu hình chấm điểm tổng kết buổi luyện B2C.
public class ScoringOptions
{
    public const string SectionName = "Scoring";

    // Tiêu chí có percentage < ngưỡng này (mặc định 50%) bị gắn needs_improvement.
    // Ngưỡng CHỐT lúc tính (đổi ngưỡng sau không hồi tố kết quả đã lưu).
    public decimal ImprovementThresholdPct { get; set; } = 50m;
}
