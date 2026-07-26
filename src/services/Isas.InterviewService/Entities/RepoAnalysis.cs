using Isas.InterviewService.Enums;

namespace Isas.InterviewService.Entities;

public class RepoAnalysis
{
    public Guid Id { get; set; }
    public Guid CandidateId { get; set; }
    public string RepoUrl { get; set; } = string.Empty;
    public string RepoOwner { get; set; } = string.Empty;
    public string RepoName { get; set; } = string.Empty;
    public JobCategory JobCategory { get; set; }
    public string DefaultBranch { get; set; } = string.Empty;
    public string? CommitSha { get; set; }
    public int Stars { get; set; }
    public string? PrimaryLanguage { get; set; }
    public Dictionary<string, long> Languages { get; set; } = [];
    public string Summary { get; set; } = string.Empty;
    public List<string> TechStack { get; set; } = [];
    public List<string> Strengths { get; set; } = [];
    public List<string> Weaknesses { get; set; } = [];
    public List<string> Suggestions { get; set; } = [];
    public List<string> InterviewTalkingPoints { get; set; } = [];
    public CvJdMatch? JdMatch { get; set; }
    public Guid? JdId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
