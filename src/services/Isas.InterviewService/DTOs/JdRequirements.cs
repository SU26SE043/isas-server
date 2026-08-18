using System.ComponentModel.DataAnnotations;
using Isas.InterviewService.Enums;

namespace Isas.InterviewService.DTOs;

// B2C bước 1 — nguồn JD có thể là text nhập tay hoặc file đã upload. Nếu gửi cả hai,
// service dùng jdText và không đọc/kiểm tra ownership của jdId.
public record JdRequirementsRequest(
    string? JdText,
    Guid? JdId,
    [Required] JobCategory? JobCategory
);

public record JdRequirementSuggestion(string Text, IReadOnlyList<Citation> Citations);

public record JdRequirementsResponse(
    IReadOnlyList<JdRequirementSuggestion> MustHave,
    IReadOnlyList<JdRequirementSuggestion> NiceToHave
);
