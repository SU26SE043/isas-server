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

// `Citations` = tài liệu chuẩn ngành truy hồi từ kho tri thức (Qdrant) — KHÔNG phải trích từ JD.
// `JdQuote` mới là câu NGUYÊN VĂN trong JD của user sinh ra requirement này, để FE cho user bấm
// "Xem trong JD" mà kiểm chứng. AIService đã verify quote là substring thật của jdText rồi mới trả
// (không phải ⇒ null), nên null là giá trị hợp lệ và bình thường — FE ẩn tính năng, không hỏng gì.
public record JdRequirementSuggestion(
    string Text, IReadOnlyList<Citation> Citations, string? JdQuote = null);

public record JdRequirementsResponse(
    IReadOnlyList<JdRequirementSuggestion> MustHave,
    IReadOnlyList<JdRequirementSuggestion> NiceToHave
);
