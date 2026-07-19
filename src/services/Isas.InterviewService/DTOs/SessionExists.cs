namespace Isas.InterviewService.DTOs;

// DB18 — endpoint internal máy-máy (X-Internal-Token, KHÔNG qua gateway) để PaymentService phát hiện
// orphan reservation: reservation Reserved mà session KHÔNG BAO GIỜ được insert (crash giữa reserve↔insert
// lúc Start). Payment gửi danh sách session_id đang giữ chỗ → Interview trả về TẬP CON thực sự tồn tại.
public record SessionExistsRequest(IReadOnlyList<Guid>? SessionIds);

// R1 — trạng thái 1 session. Status là STRING tên enum SessionStatus (GEN-2: enum truyền dạng string,
// KHÔNG phải số thứ tự — chèn/xoá phần tử enum không được làm lệch nghĩa dây giữa 2 service).
public record SessionStateDto(Guid SessionId, string Status);

// existingIds = tập con SessionIds có row practice_sessions (bất kể status). Payment coi phần còn lại
// (không trong existingIds) là orphan → release chỗ giữ.
//
// R1 — `States` là mở rộng ADDITIVE: cùng tập session với `ExistingIds`, kèm trạng thái, để Payment phân
// biệt "session đã terminal mà chỗ giữ còn Reserved" (rò credit — trước R1 không ai dọn) với "session
// đang bay hợp lệ".
// ⚠ `ExistingIds` GIỮ NGUYÊN và vẫn là nguồn chân lý DUY NHẤT cho câu hỏi "session có tồn tại không":
// Payment bản cũ chỉ đọc trường này, và Payment bản mới cũng PHẢI đọc trường này. Suy tồn-tại từ
// `States` sẽ khiến Payment MỚI nói chuyện với Interview CŨ (không có `States`) hiểu nhầm "không session
// nào tồn tại" → release cả session đang thi. Xem OrphanReservationReconciler §AN TOÀN.
public record SessionExistsResponse(
    IReadOnlyList<Guid> ExistingIds,
    IReadOnlyList<SessionStateDto>? States = null);
