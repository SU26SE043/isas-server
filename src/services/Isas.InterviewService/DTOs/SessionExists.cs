namespace Isas.InterviewService.DTOs;

// DB18 — endpoint internal máy-máy (X-Internal-Token, KHÔNG qua gateway) để PaymentService phát hiện
// orphan reservation: reservation Reserved mà session KHÔNG BAO GIỜ được insert (crash giữa reserve↔insert
// lúc Start). Payment gửi danh sách session_id đang giữ chỗ → Interview trả về TẬP CON thực sự tồn tại.
public record SessionExistsRequest(IReadOnlyList<Guid>? SessionIds);

// existingIds = tập con SessionIds có row practice_sessions (bất kể status). Payment coi phần còn lại
// (không trong existingIds) là orphan → release chỗ giữ.
public record SessionExistsResponse(IReadOnlyList<Guid> ExistingIds);
