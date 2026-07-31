using Isas.InterviewService.DTOs;
using Isas.InterviewService.Enums;
using Isas.Shared.Pagination;

namespace Isas.InterviewService.Services.Interfaces;

// RAG grounding — điều phối ingest/retrieve/delete. Ingest: chunk → /embed → upsert Qdrant + ghi row.
// Retrieve: embed query → Qdrant search → grounding (degrade rỗng khi lỗi). Delete: Qdrant point TRƯỚC row.
public interface IKnowledgeService
{
    // Nạp nguồn Manual (content) / Url (tải HTML). AI/Qdrant lỗi → ném (admin thấy). Content rỗng → 400.
    Task<KnowledgeSourceResponse> IngestAsync(Guid adminId, CreateKnowledgeRequest req, CancellationToken ct = default);

    Task<KeysetPage<KnowledgeSourceResponse>> ListAsync(
        JobCategory? jobCategory, string? cursor, int? limit, CancellationToken ct = default);

    // Xóa Qdrant point (filter sourceId) TRƯỚC rồi row Postgres. Không có row → false (404).
    Task<bool> DeleteAsync(Guid id, CancellationToken ct = default);

    // Re-chunk + re-embed 1 nguồn (xóa point cũ → upsert lại). Chỉ Manual/Url có content lưu... → xem impl.
    Task<KnowledgeSourceResponse?> ReindexAsync(Guid id, CancellationToken ct = default);

    Task<IReadOnlyList<Context7SearchResult>> Context7SearchAsync(
        string libraryName, string? query, CancellationToken ct = default);

    // Nạp N topic của 1 thư viện Context7 → 1 knowledge_source (per-snippet = 1 chunk).
    Task<KnowledgeSourceResponse> Context7IngestAsync(
        Guid adminId, Context7IngestRequest req, CancellationToken ct = default);

    // RETRIEVAL — embed query (RETRIEVAL_QUERY) → Qdrant search filter jobCategory → grounding.
    // MỌI lỗi (embed/Qdrant down/miss) → RỖNG (degrade ungrounded, KHÔNG ném) — điểm degrade DUY NHẤT.
    Task<IReadOnlyList<GroundingChunk>> RetrieveAsync(
        string jobCategory, string query, CancellationToken ct = default);

    // RETRIEVAL BATCH (Cách 2 precompute roadmap) — embed N query trong 1 LẦN /embed → search từng cái.
    // Trả grounding per-query (cùng thứ tự queries). Lỗi → list các phần tử RỖNG (degrade, KHÔNG ném).
    Task<IReadOnlyList<IReadOnlyList<GroundingChunk>>> RetrieveBatchAsync(
        string jobCategory, IReadOnlyList<string> queries, CancellationToken ct = default);
}
