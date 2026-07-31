using Isas.InterviewService.DTOs;

namespace Isas.InterviewService.Services.Interfaces;

// RAG grounding — abstraction kho vector (Qdrant). Unit test MOCK cái này (không cần Qdrant thật):
// test chỉ chứng minh ĐẤU DÂY (ingest gọi upsert, delete đúng thứ tự), KHÔNG chứng minh chunk đúng
// (chất lượng retrieval chỉ verify được ở L3 + Phase 2).
public interface IVectorStore
{
    // Tạo collection `knowledge` (768, Cosine) nếu chưa có — idempotent. Gọi lúc startup (best-effort).
    Task EnsureCollectionAsync(CancellationToken ct = default);

    // Upsert N point (ingest). Qdrant down → ném (admin thấy lỗi ingest, KHÔNG degrade âm thầm).
    Task UpsertAsync(IReadOnlyList<VectorPoint> points, CancellationToken ct = default);

    // Search top-k lọc theo jobCategory, CHỈ giữ chunk score ≥ scoreThreshold (guard over-attribution).
    // Trả grounding shape gửi thẳng AIService. Qdrant down / lỗi → NÉM (caller degrade sang ungrounded).
    Task<IReadOnlyList<GroundingChunk>> SearchAsync(
        string jobCategory, IReadOnlyList<float> queryVector,
        int topK, float scoreThreshold, CancellationToken ct = default);

    // Xóa mọi point của 1 nguồn (filter theo sourceId). Gọi TRƯỚC khi xóa row Postgres (chống orphan vector).
    Task DeleteBySourceAsync(Guid sourceId, CancellationToken ct = default);
}

// 1 point sẵn sàng upsert — Id = chunk uuid, payload đủ để trả grounding (không cần join Postgres).
public record VectorPoint(
    Guid Id, float[] Vector,
    Guid SourceId, string JobCategory, int Ordinal,
    string Content, string SourceUrl, string SourceTitle);
