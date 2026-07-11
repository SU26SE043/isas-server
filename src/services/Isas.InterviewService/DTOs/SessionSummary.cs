namespace Isas.InterviewService.DTOs;

// BC10 — số liệu 1 tiêu chí gửi xuống AIService /summarize-session (khớp ai.md: name/percentage/needsImprovement).
// Lấy từ session_criterion_scores (BC9). AI dùng để viết nhận xét chung, KHÔNG dùng để tính điểm.
public record SessionSummaryCriterion(string Name, decimal Percentage, bool NeedsImprovement);
