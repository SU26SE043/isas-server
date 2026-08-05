namespace Isas.CampaignService.Models
{
    public class Campaign
    {
        public Guid Id { get; set; }
        // BK4: chủ sở hữu = ORG (AUTH-8/D5) — billing/campaign gắn theo org, không cá nhân HR.
        public Guid OrgId { get; set; }
        public string Title { get; set; }
        public string? Domain { get; set; }
        public CampaignStatus Status { get; set; }
        public int? MaxCandidates { get; set; }
        public int? MaxConcurrentInterviews { get; set; }
        public int? TimeLimitMinutes { get; set; }
        public bool AntiCheatEnabled { get; set; }
        // SEC-1: bật xác minh khuôn mặt trước bài (B2B-only, mặc định false). Gate face-verify (SEC-2) +
        // cho phép nhận cờ danh tính (face_mismatch/no_face/multiple_faces/identity_unverified) từ AIService.
        public bool FaceVerifyEnabled { get; set; }
        // E5: ngưỡng % điểm tổng để auto pass/fail (0–100, CAMP-11). null = không auto → HR quyết tay.
        public int? PassScorePct { get; set; }
        // INT-17: HR bật phỏng vấn THÍCH ỨNG cho chiến dịch này (mặc định false = luồng batch tĩnh cũ).
        // Seed = TOÀN BỘ campaign questions (ai cũng nhận, công bằng); AI chỉ thêm câu ở ĐUÔI sau khi
        // ứng viên trả lời hết seed, chấm theo CÙNG tiêu chí campaign ⇒ ranking vẫn so sánh được.
        public bool AdaptiveEnabled { get; set; }
        // T8: snapshot grounding selection at campaign creation/update; existing running campaigns keep it.
        public bool GroundingEnabled { get; set; }
        // INT-17: trần số câu THÍCH ỨNG được thêm (null = dùng mặc định phía Interview). Giữ bài bounded.
        public int? MaxFollowUps { get; set; }
        // INT-17: trần TỔNG số câu (seed + thích ứng; null = mặc định Interview). Giữ độ dài so sánh được.
        public int? MaxQuestions { get; set; }
        // INT-17b: trần số câu ĐÀO SÂU cho MỖI câu hỏi campaign. null/0 = chế độ cũ (AI chỉ thêm câu ở
        // ĐUÔI sau khi ứng viên trả lời hết seed). > 0 = mỗi câu campaign có chuỗi đào sâu XEN KẼ ngay
        // sau nó — vẫn công bằng vì mọi ứng viên nhận cùng bộ câu gốc và cùng trần độ sâu.
        // ⚠ Độ dài bài nhân lên: N câu campaign × (1 + trần) — HR phải cân nhắc, xem ValidateAdaptiveCaps.
        public int? MaxDeepPerQuestion { get; set; }
        public string? JDFileUrl { get; set; }
        public string? JDText { get; set; }
        public string? CriteriaFileUrl { get; set; }
        public string? CriteriaText { get; set; }
        // C13: rule cứng sàng CV (hard-filter, set khi Draft). null = không áp rule đó.
        public List<string>? RequiredSkills { get; set; }   // jsonb — phải có ĐỦ trong cv_parsed_text
        public List<string>? KeywordsAny { get; set; }      // jsonb — có ≥1 từ khóa
        public int? MinYearsExperience { get; set; }        // số năm KN tối thiểu
        public DateTime? StartsAt { get; set; }
        public DateTime? ExpiresAt { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public DateTime? DeletedAt { get; set; }   // soft delete (D11): null = còn sống

        // Navigation
        public ICollection<CampaignQuestion> Questions { get; set; } = new List<CampaignQuestion>();
        public ICollection<CampaignCriterion> Criteria { get; set; } = new List<CampaignCriterion>();
        public ICollection<CampaignInvitation> Invitations { get; set; } = new List<CampaignInvitation>();
        public ICollection<CvSubmission> CvSubmissions { get; set; } = new List<CvSubmission>();   // C13: sàng CV (DB16)
    }

    public enum CampaignStatus
    {
        Draft,
        Active,
        Closed,
        Archived
    }
}
