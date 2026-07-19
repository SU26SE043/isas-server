using Isas.InterviewService.DTOs;
using Isas.InterviewService.Entities;

namespace Isas.InterviewService.Services.Interfaces;

// F14 (FR08) — dựng mốc đối chiếu cho radar kết quả buổi luyện B2C.
public interface ICriterionBenchmarkService
{
    /// <summary>
    /// Trả mốc cho từng tiêu chí của buổi, hoặc null khi tắt / không có tiêu chí nào.
    /// KHÔNG ghi DB — thuần đọc, tính read-time (mốc đổi theo dữ liệu cộng đồng nên
    /// snapshot vào bảng sẽ lỗi thời ngay và không có ai chịu trách nhiệm làm mới).
    /// </summary>
    Task<BenchmarkResponse?> BuildAsync(
        PracticeSession session,
        IReadOnlyList<SessionCriterionScore> criterionScores,
        CancellationToken ct = default);
}
