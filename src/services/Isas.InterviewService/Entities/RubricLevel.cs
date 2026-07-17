namespace Isas.InterviewService.Entities;

public class RubricLevel
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid CriterionId { get; set; }
    public RubricCriterion Criterion { get; set; } = null!;

    // Mức điểm (vd 0,1,2,3,4,5)
    public int Score { get; set; }

    // Mô tả mức điểm này nghĩa là gì
    public string Descriptor { get; set; } = null!;

    // DB15 — câu trả lời mẫu neo (anchored examples) cho mức điểm này, gộp thành jsonb string[] trên
    // chính rubric_levels (thay bảng rubric_anchors 1-n cũ). Mỗi phần tử = 1 ExampleAnswer, giữ thứ tự.
    // Non-null (mặc định rỗng); cấu hình jsonb + ValueConverter trong RubricLevelConfiguration.
    public List<string> ExampleAnswers { get; set; } = [];
}