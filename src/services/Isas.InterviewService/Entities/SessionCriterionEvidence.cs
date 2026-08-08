namespace Isas.InterviewService.Entities;

// Evidence state is owned by InterviewService; AIService only receives a snapshot.
public class SessionCriterionEvidence
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SessionId { get; set; }
    public PracticeSession Session { get; set; } = null!;
    public Guid CriterionId { get; set; }
    public string CriterionName { get; set; } = null!;
    public string State { get; set; } = "UNKNOWN";
    public List<string> EvidenceFound { get; set; } = [];
    public List<string> MissingEvidence { get; set; } = [];
    public int DeepCount { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
