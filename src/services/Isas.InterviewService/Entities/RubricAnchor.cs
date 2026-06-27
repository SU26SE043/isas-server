namespace Isas.InterviewService.Entities;

public class RubricAnchor
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid LevelId { get; set; }
    public RubricLevel Level { get; set; } = null!;

    // Ví dụ câu trả lời mẫu cho mức điểm này (anchored example)
    public string ExampleAnswer { get; set; } = null!;
}