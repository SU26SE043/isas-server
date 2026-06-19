namespace Isas.InterviewService.Models
{
    public class AnswerAudio
    {
        public Guid Id { get; set; }
        public Guid AnswerId { get; set; }
        public Guid UserId { get; set; }
        public string StorageKey { get; set; }
        public DateTime UploadedAt { get; set; }
        public DateTime ExpiredAt { get; set; }
        public bool IsRetained { get; set; }
        public string RetainedReason { get; set; }
        public virtual PracticeAnswer PracticeAnswer { get; set; } = null!;
    }
}
