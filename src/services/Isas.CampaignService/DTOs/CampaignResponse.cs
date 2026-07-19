using Isas.CampaignService.Models;
using System.ComponentModel.DataAnnotations;

namespace Isas.CampaignService.DTOs
{
    public class QuestionItem
    {
        // F10 — id của câu hỏi ĐANG CÓ (echo lại từ `CampaignResponse.Questions[].id`).
        // Có id  → sửa đúng row đó, GIỮ NGUYÊN `source` + `created_at` (câu AI không mất dấu vết, không đổi thứ tự).
        // Không id → câu mới HR gõ tay.
        // Trước F10, PUT questions `Clear()` rồi tạo lại toàn bộ với Guid mới ⇒ sửa 1 câu là xoá sạch
        // provenance `AiGenerated` của cả chiến dịch (F9 sinh bao nhiêu cũng thành CustomHr).
        public Guid? Id { get; set; }

        public string QuestionText { get; set; }

        // ⚠ Server KHÔNG đọc field này khi ghi (create/update đều ép `CustomHr`).
        // `AiGenerated` là KHẲNG ĐỊNH VỀ NGUỒN GỐC — chỉ đường sinh F9 mới có quyền đặt. Nhận từ client thì
        // FE/HR gắn nhãn "AI sinh" cho câu gõ tay được ⇒ field mất sạch giá trị kiểm chứng, mà đó lại đúng
        // là thứ F9/F10 sinh ra để bảo vệ. Giữ lại để không phá hợp đồng JSON đang có (BK20).
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

        // INT-17: bật phỏng vấn THÍCH ỨNG cho chiến dịch (mặc định false = luồng tĩnh). Không gửi → false.
        public bool AdaptiveEnabled { get; set; }
        // INT-17: trần câu thích ứng / tổng câu. null = dùng mặc định phía Interview.
        public int? MaxFollowUps { get; set; }
        public int? MaxQuestions { get; set; }

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

        // INT-17: bật/tắt phỏng vấn thích ứng + trần câu — null = không đổi (giữ cũ), như AntiCheatEnabled.
        public bool? AdaptiveEnabled { get; set; }
        public int? MaxFollowUps { get; set; }
        public int? MaxQuestions { get; set; }

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
        public bool AdaptiveEnabled { get; set; }   // INT-17: phỏng vấn thích ứng (B2B opt-in)
        public int? MaxFollowUps { get; set; }      // INT-17: trần câu thích ứng (null = mặc định Interview)
        public int? MaxQuestions { get; set; }      // INT-17: trần tổng câu (null = mặc định Interview)
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
            AdaptiveEnabled = c.AdaptiveEnabled,   // INT-17
            MaxFollowUps = c.MaxFollowUps,
            MaxQuestions = c.MaxQuestions,
            StartsAt = c.StartsAt,
            ExpiresAt = c.ExpiresAt,
            // F10: sắp theo ĐÚNG thứ tự ứng viên sẽ gặp (ParticipationService dùng CreatedAt, Id) —
            // FE echo `id` lại khi PUT, nên thứ tự response phải ổn định giữa các lần gọi.
            Questions = c.Questions
                .OrderBy(q => q.CreatedAt).ThenBy(q => q.Id)
                .Select(q => new CampaignQuestionResponse
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
