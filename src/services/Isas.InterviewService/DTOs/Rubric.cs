namespace Isas.InterviewService.DTOs;

using Isas.InterviewService.Enums;

// BC16 — Rubric CÁ NHÂN B2C. Candidate tự khai 1 tiêu chí.
//
// ⚠ Trước đây DTO này CỐ Ý không có `levels`, và điều đó tạo ra một nghịch lý: dùng bộ chuẩn thì
// được thang có mô tả từng mức (admin soạn), còn TỰ TUỲ CHỈNH thì rơi về dải mặc định
// "Mức 3/5" — tức tự tuỳ chỉnh xong thì chất lượng chấm TỆ ĐI, và không có gì trên màn hình
// nói ra điều đó.
public record RubricCriterionInput(
    string Name,
    string? Description,
    decimal Weight,    // chuẩn hoá Σ→1 khi lưu; B2C điểm tổng = TB cộng (INT-10) nên weight chỉ để hiển thị
    int MaxScore,
    /// <summary>
    /// Mốc điểm (E9). <c>null</c>/rỗng = chưa khai ⇒ chấm theo dải mặc định <c>0..maxScore</c> — hợp
    /// lệ, không phải lỗi. Luật cấu trúc dùng chung <c>CriterionLevelRules</c> với B2B, không có luật
    /// thứ hai ở đây.
    /// </summary>
    List<RubricLevelInput>? Levels = null
);

public record RubricLevelInput(int Score, string Descriptor);

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
    int MaxScore,
    /// <summary>
    /// Mốc điểm hiện có. Khi <c>IsCustom = false</c> (đang trả seed làm template) thì đây là mốc do
    /// ADMIN soạn — chúng đi theo sang form sửa, để ứng viên bấm tuỳ chỉnh là đã có sẵn thang để
    /// chỉnh chứ không bắt đầu từ trang trắng. Đó là vế xoá bỏ nghịch lý "tự tuỳ chỉnh thì chấm tệ đi".
    /// </summary>
    IReadOnlyList<RubricLevelInput> Levels
);
