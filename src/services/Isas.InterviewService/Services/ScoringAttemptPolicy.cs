namespace Isas.InterviewService.Services;

/// <summary>
/// E10 — số attempt phải chấm cho MỘT answer (self-consistency).
/// </summary>
/// <remarks>
/// Tách ra khỏi <see cref="AnswerService"/> vì <see cref="StuckAnswerRepublisher"/> PHẢI tính ra
/// CÙNG một con số: republisher là bên đi BÙ attempt còn thiếu, còn bên đếm "đã đủ chưa" là
/// AnswerService. Hai công thức lệch nhau một chữ ⇒ answer treo vĩnh viễn ở <c>Scoring</c>:
/// không lỗi, không cảnh báo, chỉ là một buổi không bao giờ đóng — và credit đã reserve cũng
/// không bao giờ được consume/release (sự cố 2026-08-15, session <c>39834dbb</c>).
///
/// Vế <c>EntitlementSource != "legacy"</c>: buổi tạo TRƯỚC khi có tiering không mang con số
/// entitlement nào (cột mặc định 0/1) nên phải rơi về cấu hình; buổi B2B chấm theo cấu hình chung
/// vì tiering là chuyện của ví cá nhân B2C.
/// </remarks>
public static class ScoringAttemptPolicy
{
    public static int Resolve(
        Guid? campaignId, string? entitlementSource, int sessionSelfConsistencyN, int fallbackN)
        => Math.Max(1, campaignId is null && entitlementSource != "legacy"
            ? sessionSelfConsistencyN
            : fallbackN);
}
