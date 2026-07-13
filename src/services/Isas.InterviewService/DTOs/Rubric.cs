namespace Isas.InterviewService.DTOs;

using Isas.InterviewService.Enums;

// BC16 — Rubric CÁ NHÂN B2C. Candidate tự khai 1 tiêu chí (CƠ BẢN: name/desc/weight/maxScore;
// KHÔNG levels/anchors E9 → AI chấm theo dải mặc định 0..maxScore).
public record RubricCriterionInput(
    string Name,
    string? Description,
    decimal Weight,    // chuẩn hoá Σ→1 khi lưu; B2C điểm tổng = TB cộng (INT-10) nên weight chỉ để hiển thị
    int MaxScore
);

// BC16 — thay TOÀN BỘ rubric riêng của candidate cho 1 JobCategory (replace-all).
public record UpsertRubricRequest(
    List<RubricCriterionInput> Criteria
);

// BC16 — rubric HIỆU LỰC đang dùng để chấm cho 1 JobCategory.
public record RubricResponse(
    JobCategory JobCategory,
    bool IsCustom,          // true = rubric riêng của candidate; false = seed mặc định (template để clone)
    IReadOnlyList<RubricCriterionItem> Criteria
);

public record RubricCriterionItem(
    Guid Id,
    string Name,
    string? Description,
    decimal Weight,
    int MaxScore
);
