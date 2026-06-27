using Isas.InterviewService.Enums;

namespace Isas.InterviewService.Entities;

public class RubricCriterion
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = null!;
    public string? Description { get; set; }

    // Trọng số tính điểm tổng
    public decimal Weight { get; set; }

    // Thang điểm tối đa của tiêu chí (vd 5)
    public int MaxScore { get; set; }

    public bool IsActive { get; set; } = true;
    public JobCategory JobCategory { get; set; }
    public int Version { get; set; } = 1;

    // B2B: tiêu chí thuộc về 1 campaign (thay cho job_category); null = rubric B2C theo JobCategory.
    // Ref lỏng sang CampaignService - KHÔNG FK xuyên service.
    public Guid? CampaignId { get; set; }

    // Navigation
    public ICollection<RubricLevel> Levels { get; set; } = [];
}