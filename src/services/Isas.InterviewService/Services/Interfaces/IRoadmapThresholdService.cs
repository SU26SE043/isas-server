using Isas.InterviewService.DTOs;

namespace Isas.InterviewService.Services.Interfaces;

/// <summary>
/// BC15 — ngưỡng % ĐẠT theo cấp độ lộ trình. Nguồn sự thật lúc chạy: hàng trong
/// <c>roadmap_level_thresholds</c>; chưa có hàng thì rơi về mặc định code/cấu hình
/// (<c>RoadmapOptions</c>).
/// </summary>
public interface IRoadmapThresholdService
{
    /// <summary>Toàn bộ cấp độ + giá trị hiệu lực/mặc định/đã-chỉnh-chưa. Dùng cho màn quản trị.</summary>
    Task<IReadOnlyList<RoadmapLevelThresholdResponse>> ListAsync(CancellationToken ct = default);

    /// <summary>
    /// Ngưỡng đang hiệu lực của một cấp độ. <b>KHÔNG BAO GIỜ ném</b> — nó nằm trên đường build
    /// report, ném ở đây là làm hỏng cả trang kết quả của người học vì một dòng cấu hình thiếu.
    /// Cấp độ lạ / chưa cấu hình ⇒ mặc định.
    /// </summary>
    Task<int> ThresholdForAsync(string level, CancellationToken ct = default);

    /// <summary>
    /// Đặt ngưỡng cho một hoặc nhiều cấp độ (cấp không nêu thì giữ nguyên). Trả về danh sách đầy
    /// đủ sau khi ghi, để màn quản trị khỏi phải gọi lại GET.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Body rỗng · cấp độ lạ · trùng cấp độ · ngưỡng ngoài [0,100]. Controller map sang 400.
    /// Validate TOÀN BỘ trước khi ghi — sai một entry thì không entry nào được ghi.
    /// </exception>
    Task<IReadOnlyList<RoadmapLevelThresholdResponse>> UpsertAsync(
        IReadOnlyDictionary<string, int> thresholds, Guid actor, CancellationToken ct = default);

    /// <summary>
    /// Bỏ phần ghi đè của một cấp độ → quay về mặc định code. <c>false</c> = cấp đó vốn chưa ai
    /// chỉnh (404).
    /// </summary>
    /// <exception cref="InvalidOperationException">Cấp độ lạ. Controller map sang 400.</exception>
    Task<bool> ResetAsync(string level, CancellationToken ct = default);
}
