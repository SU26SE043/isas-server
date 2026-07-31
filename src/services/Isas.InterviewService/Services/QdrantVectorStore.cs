using Isas.InterviewService.DTOs;
using Isas.InterviewService.Services.Interfaces;
using Qdrant.Client;
using Qdrant.Client.Grpc;

namespace Isas.InterviewService.Services;

// RAG grounding — QdrantVectorStore. Collection `knowledge` (768, Cosine). Point id = chunk uuid;
// payload {sourceId, jobCategory, ordinal, content, sourceUrl, sourceTitle} đủ để trả grounding.
// Singleton (bọc QdrantClient — cũng singleton). Xem IVectorStore cho hợp đồng lỗi/degrade.
public class QdrantVectorStore(QdrantClient client, ILogger<QdrantVectorStore> logger) : IVectorStore
{
    private const string Collection = "knowledge";
    private const ulong VectorSize = 768;   // gemini-embedding-001 output_dimensionality (khớp Contract 1)

    public async Task EnsureCollectionAsync(CancellationToken ct = default)
    {
        if (await client.CollectionExistsAsync(Collection, ct))
            return;

        await client.CreateCollectionAsync(
            Collection,
            new VectorParams { Size = VectorSize, Distance = Distance.Cosine },
            cancellationToken: ct);

        // Index payload field `jobCategory` (keyword) để filter Match nhanh khi retrieve.
        await client.CreatePayloadIndexAsync(
            Collection, "jobCategory", PayloadSchemaType.Keyword, cancellationToken: ct);
        await client.CreatePayloadIndexAsync(
            Collection, "sourceId", PayloadSchemaType.Keyword, cancellationToken: ct);

        logger.LogInformation("Qdrant: tạo collection {Collection} (size {Size}, Cosine)", Collection, VectorSize);
    }

    public async Task UpsertAsync(IReadOnlyList<VectorPoint> points, CancellationToken ct = default)
    {
        if (points.Count == 0) return;

        var structs = points.Select(p =>
        {
            var ps = new PointStruct
            {
                Id = p.Id,               // implicit Guid → PointId (uuid)
                Vectors = p.Vector       // implicit float[] → Vectors
            };
            ps.Payload["sourceId"] = p.SourceId.ToString();
            ps.Payload["jobCategory"] = p.JobCategory;
            ps.Payload["ordinal"] = p.Ordinal;
            ps.Payload["content"] = p.Content;
            ps.Payload["sourceUrl"] = p.SourceUrl;
            ps.Payload["sourceTitle"] = p.SourceTitle;
            return ps;
        }).ToList();

        await client.UpsertAsync(Collection, structs, cancellationToken: ct);
    }

    public async Task<IReadOnlyList<GroundingChunk>> SearchAsync(
        string jobCategory, IReadOnlyList<float> queryVector,
        int topK, float scoreThreshold, CancellationToken ct = default)
    {
        var results = await client.SearchAsync(
            Collection,
            queryVector.ToArray(),
            filter: MatchFilter("jobCategory", jobCategory),
            limit: (ulong)Math.Max(1, topK),
            // scoreThreshold: Qdrant chỉ trả point ≥ ngưỡng (guard over-attribution ở tầng retrieval —
            // chunk yếu KHÔNG lọt vào tập grounding nên model không thể cite nó).
            scoreThreshold: scoreThreshold,
            payloadSelector: true,
            cancellationToken: ct);

        return results.Select(r => new GroundingChunk(
            ChunkId: r.Id.Uuid,
            Content: Payload(r, "content"),
            SourceUrl: Payload(r, "sourceUrl"),
            SourceTitle: Payload(r, "sourceTitle"))).ToList();
    }

    public async Task DeleteBySourceAsync(Guid sourceId, CancellationToken ct = default)
    {
        // Xóa theo FILTER sourceId (không cần biết id từng point). Idempotent — nguồn 0 point → no-op.
        await client.DeleteAsync(
            Collection, MatchFilter("sourceId", sourceId.ToString()), cancellationToken: ct);
    }

    // Filter keyword-match tường minh (Conditions.Match(string,string) không tồn tại ở SDK này — chỉ có
    // overload bool/long → dựng FieldCondition thẳng cho rõ + không nhập nhằng).
    private static Filter MatchFilter(string key, string keyword)
        => new()
        {
            Must =
            {
                new Condition { Field = new FieldCondition { Key = key, Match = new Match { Keyword = keyword } } }
            }
        };

    private static string Payload(ScoredPoint p, string key)
        => p.Payload.TryGetValue(key, out var v) ? v.StringValue ?? string.Empty : string.Empty;
}
