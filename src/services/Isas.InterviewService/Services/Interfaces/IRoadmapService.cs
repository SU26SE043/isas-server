using Isas.InterviewService.DTOs;
using Isas.Shared.Pagination;

namespace Isas.InterviewService.Services.Interfaces;

// BC12 (D20) — tạo + đọc roadmap ôn tập B2C. Không trừ credit (chỉ session luyện BC14 mới reserve).
public interface IRoadmapService
{
    // POST /roadmaps — gom điểm yếu + baseline (BC9) → AIService /generate-roadmap → persist 3 bảng.
    Task<RoadmapResponse> CreateAsync(Guid candidateId, CreateRoadmapRequest req, CancellationToken ct = default);

    // GET /roadmaps/{id} — đầy đủ (kèm theoryContent). null → 404; khác chủ → UnauthorizedAccessException (403).
    Task<RoadmapResponse?> GetAsync(Guid candidateId, Guid id, CancellationToken ct = default);

    // BE-6 — đổi tên. null = không tìm thấy (404); khác chủ → UnauthorizedAccessException (403);
    // tên rỗng/quá dài → InvalidOperationException (400).
    Task<RoadmapResponse?> RenameAsync(Guid candidateId, Guid id, string? requestedName, CancellationToken ct = default);

    // GET /roadmaps — của chính user (không kèm theoryContent).
    // Danh sách keyset-paged, KHÔNG kèm cây milestone/lesson (chi tiết dùng GetAsync).
    // `status` + `hasFinalReport` OPT-IN cho picker "chọn lộ trình đã hoàn tất" của wizard; vắng cả
    // hai ⇒ hành vi y hệt hôm nay. `status` lạ → InvalidOperationException (400).
    Task<KeysetPage<RoadmapSummaryResponse>> ListAsync(
        Guid candidateId, string? cursor = null, int? limit = null,
        string? status = null, bool? hasFinalReport = null, CancellationToken ct = default);
}
