using Isas.CampaignService.Models;
using System.ComponentModel.DataAnnotations;

namespace Isas.CampaignService.DTOs
{
    public class QuestionItem
    {
        public string QuestionText { get; set; }
        public QuestionSource Source { get; set; }
        public bool IsRequired { get; set; } = true;
    }

    // C12: tiêu chí chấm CÓ CẤU TRÚC — HR khai thẳng (name/weight/maxScore/description).
    // Ưu tiên cao nhất (có thì publish bỏ qua AI). Σweight ∈ [0.99,1.01] → chuẩn hoá Σ→1.
    public class CriterionItem
    {
        public string Name { get; set; } = null!;
        public string? Description { get; set; }
        public decimal Weight { get; set; }   // 0 < weight ≤ 1
        public int MaxScore { get; set; }      // ≥ 1
    }

    public class CreateCampaignRequest
    {
        [Required]
        public string Title { get; set; }

        [Required]
        public string? Domain { get; set; }

        public int? MaxCandidates { get; set; }

        [Required]
        public int? TimeLimitMinutes { get; set; }

        public bool AntiCheatEnabled { get; set; }

        // SEC-1: bật face-verify (B2B-only, mặc định false). Không gửi → false.
        public bool FaceVerifyEnabled { get; set; }

        // E5: ngưỡng % pass/fail (0–100). null = HR quyết tay (không auto).
        public int? PassScorePct { get; set; }

        // C11: JD & Criteria nhập TEXT trực tiếp (không bắt buộc PDF). Set *_text, *_file_url = null.
        public string? JdText { get; set; }
        public string? CriteriaText { get; set; }

        // C12: tiêu chí structured HR khai thẳng — ưu tiên cao nhất (publish bỏ qua AI). Chỉ set khi Draft.
        public List<CriterionItem>? Criteria { get; set; }

        [Required]
        public DateTime? StartsAt { get; set; }

        [Required]
        public DateTime? ExpiresAt { get; set; }

        public List<QuestionItem> Questions { get; set; } = new();
    }

    public class UploadCampaignFilesRequest
    {
        public IFormFile? JdFile { get; set; }
        public IFormFile? CriteriaFile { get; set; }
    }

    public class UpdateCampaignRequest
    {
        public string Title { get; set; }

        public string? Domain { get; set; }

        public int? MaxCandidates { get; set; }

        public int? TimeLimitMinutes { get; set; }

        public bool? AntiCheatEnabled { get; set; }

        // SEC-1: bật/tắt face-verify — null = không đổi (giữ giá trị cũ), như AntiCheatEnabled (C3).
        public bool? FaceVerifyEnabled { get; set; }

        // E5: ngưỡng % pass/fail (0–100). null = không đổi (giữ giá trị cũ).
        public int? PassScorePct { get; set; }

        // C11: cập nhật/ghi đè JD & Criteria dạng TEXT trực tiếp (text ưu tiên file → xoá *_file_url).
        public string? JdText { get; set; }
        public string? CriteriaText { get; set; }

        // C12: ghi đè tiêu chí structured (replace-all atomic) — chỉ khi Draft, ngược lại 409.
        public List<CriterionItem>? Criteria { get; set; }

        public DateTime? StartsAt { get; set; }

        public DateTime? ExpiresAt { get; set; }
    }

    public class TransitionStatusRequest
    {
        public CampaignStatus Status { get; set; }   // Active→Closed→Archived (Draft→Active dùng /publish)
    }

    public class CampaignQuestionResponse
    {
        public Guid Id { get; set; }
        public string QuestionText { get; set; }
        public string Source { get; set; }
        public bool IsRequired { get; set; }
    }

    // C12: tiêu chí có cấu trúc trả về (đọc/duyệt). order_no + source (HrEdited/AiSuggested).
    public class CampaignCriterionResponse
    {
        public Guid Id { get; set; }
        public int OrderNo { get; set; }
        public string Name { get; set; } = null!;
        public string? Description { get; set; }
        public decimal Weight { get; set; }
        public int MaxScore { get; set; }
        public string Source { get; set; } = null!;
    }

    public class CampaignResponse
    {
        public Guid Id { get; set; }
        public Guid OrgId { get; set; }   // BK4: owner = ORG (AUTH-8)
        public string Title { get; set; }
        public string? Domain { get; set; }
        public string Status { get; set; }
        public int? MaxCandidates { get; set; }
        public int? TimeLimitMinutes { get; set; }
        public bool AntiCheatEnabled { get; set; }
        public bool FaceVerifyEnabled { get; set; }   // SEC-1: bật face-verify (B2B-only)
        public int? PassScorePct { get; set; }   // E5: ngưỡng % pass/fail (null = HR quyết tay)
        public DateTime? StartsAt { get; set; }
        public DateTime? ExpiresAt { get; set; }
        public List<CampaignQuestionResponse> Questions { get; set; }
        public List<CampaignCriterionResponse> Criteria { get; set; }   // C12: tiêu chí structured
        public string? JDText { get; set; }
        public string? CriteriaText { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }

        public static CampaignResponse FromEntity(Campaign c) => new CampaignResponse
        {
            Id = c.Id,
            OrgId = c.OrgId,
            Title = c.Title,
            Domain = c.Domain,
            Status = c.Status.ToString(),
            MaxCandidates = c.MaxCandidates,
            TimeLimitMinutes = c.TimeLimitMinutes,
            AntiCheatEnabled = c.AntiCheatEnabled,
            FaceVerifyEnabled = c.FaceVerifyEnabled,
            PassScorePct = c.PassScorePct,
            StartsAt = c.StartsAt,
            ExpiresAt = c.ExpiresAt,
            Questions = c.Questions.Select(q => new CampaignQuestionResponse
            {
                Id = q.Id,
                QuestionText = q.QuestionText,
                Source = q.Source.ToString(),
                IsRequired = q.IsRequired
            }).ToList(),
            Criteria = c.Criteria
                .OrderBy(cr => cr.OrderNo)
                .Select(cr => new CampaignCriterionResponse
                {
                    Id = cr.Id,
                    OrderNo = cr.OrderNo,
                    Name = cr.Name,
                    Description = cr.Description,
                    Weight = cr.Weight,
                    MaxScore = cr.MaxScore,
                    Source = cr.Source.ToString()
                }).ToList(),
            JDText = c.JDText,
            CriteriaText = c.CriteriaText,
            CreatedAt = c.CreatedAt,
            UpdatedAt = c.UpdatedAt
        };
    }
}
