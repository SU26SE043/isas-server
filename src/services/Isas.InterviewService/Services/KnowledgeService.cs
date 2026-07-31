using Isas.InterviewService.ApplicationDbContext;
using Isas.InterviewService.DTOs;
using Isas.InterviewService.Entities;
using Isas.InterviewService.Enums;
using Isas.InterviewService.Models;
using Isas.InterviewService.Services.Interfaces;
using Isas.Shared.Pagination;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Isas.InterviewService.Services;

// RAG grounding — điều phối kho tri thức. Ingest: chunk → /embed (RETRIEVAL_DOCUMENT) → upsert Qdrant
// (TRƯỚC) rồi ghi row Postgres (SAU). Retrieve: embed query (RETRIEVAL_QUERY) → Qdrant search → grounding
// (degrade rỗng khi lỗi). Delete: Qdrant point (filter sourceId) TRƯỚC → row Postgres SAU (chống orphan vector).
public class KnowledgeService(
    InterviewDbContext db,
    IVectorStore vectorStore,
    IAiServiceEmbedder embedder,
    IChunker chunker,
    IContext7Client context7,
    IUrlContentFetcher urlFetcher,
    IOptions<GroundingOptions> options,
    ILogger<KnowledgeService> logger) : IKnowledgeService
{
    private readonly GroundingOptions _opts = options.Value;

    // ── INGEST Manual / Url ──────────────────────────────────────────────────
    public async Task<KnowledgeSourceResponse> IngestAsync(
        Guid adminId, CreateKnowledgeRequest req, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(req.Title))
            throw new InvalidOperationException("title là bắt buộc.");
        if (req.JobCategory is null)
            throw new InvalidOperationException("jobCategory là bắt buộc.");
        if (req.SourceType is not (KnowledgeSourceType.Manual or KnowledgeSourceType.Url))
            throw new InvalidOperationException("sourceType chỉ nhận Manual hoặc Url (Context7 dùng endpoint riêng).");

        var source = new KnowledgeSource
        {
            Id = Guid.NewGuid(),
            Title = req.Title.Trim(),
            JobCategory = req.JobCategory,
            SourceType = req.SourceType.Value,
            Status = KnowledgeStatus.Active,
            CreatedBy = adminId,
            CreatedAt = DateTime.UtcNow
        };

        if (req.SourceType == KnowledgeSourceType.Manual)
        {
            if (string.IsNullOrWhiteSpace(req.Content))
                throw new InvalidOperationException("Nguồn Manual cần `content`.");
            source.RawContent = req.Content;   // giữ để reindex
        }
        else // Url
        {
            if (string.IsNullOrWhiteSpace(req.Url))
                throw new InvalidOperationException("Nguồn Url cần `url`.");
            source.SourceRef = req.Url.Trim();   // reindex refetch từ đây
        }

        source.ChunkCount = await BuildAndUpsertAsync(source, ct);   // Qdrant TRƯỚC
        db.KnowledgeSources.Add(source);
        await db.SaveChangesAsync(ct);           // Postgres SAU

        logger.LogInformation(
            "RAG grounding: ingest nguồn {Id} ({Type}/{Cat}) — {Chunks} chunk",
            source.Id, source.SourceType, source.JobCategory, source.ChunkCount);
        return Map(source);
    }

    // ── LIST (keyset paged) ────────────────────────────────────────────────────
    public async Task<KeysetPage<KnowledgeSourceResponse>> ListAsync(
        JobCategory? jobCategory, string? cursor, int? limit, CancellationToken ct = default)
    {
        var take = KeysetPaging.ClampLimit(limit);
        var cur = KeysetCursor.Decode(cursor);

        var query = db.KnowledgeSources.AsNoTracking().AsQueryable();
        if (jobCategory is not null)
            query = query.Where(x => x.JobCategory == jobCategory);
        if (cur is not null)
            query = query.Where(x => x.CreatedAt < cur.CreatedAt
                || (x.CreatedAt == cur.CreatedAt && x.Id.CompareTo(cur.Id) < 0));

        var rows = await query
            .OrderByDescending(x => x.CreatedAt)
            .ThenByDescending(x => x.Id)
            .Take(take)
            .ToListAsync(ct);

        var next = rows.Count == take
            ? new KeysetCursor(rows[^1].CreatedAt, rows[^1].Id).Encode()
            : null;
        return new KeysetPage<KnowledgeSourceResponse>(rows.Select(Map).ToList(), next);
    }

    // ── DELETE (Qdrant TRƯỚC → row Postgres SAU) ────────────────────────────────
    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var source = await db.KnowledgeSources.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (source is null) return false;

        // BẮT BUỘC thứ tự: xóa point Qdrant TRƯỚC. Nếu Qdrant lỗi → ném, row Postgres GIỮ (chỉ còn
        // orphan metadata vô hại, retry được). CẤM ngược (xóa row trước + Qdrant fail = orphan VECTOR vẫn
        // retrievable → citation trỏ nguồn đã chết). Idempotent (nguồn 0 point → no-op).
        await vectorStore.DeleteBySourceAsync(id, ct);

        db.KnowledgeSources.Remove(source);
        await db.SaveChangesAsync(ct);
        logger.LogInformation("RAG grounding: xóa nguồn {Id} (Qdrant point trước, row sau)", id);
        return true;
    }

    // ── REINDEX (xóa point cũ → re-chunk + re-embed) ────────────────────────────
    public async Task<KnowledgeSourceResponse?> ReindexAsync(Guid id, CancellationToken ct = default)
    {
        var source = await db.KnowledgeSources.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (source is null) return null;

        await vectorStore.DeleteBySourceAsync(id, ct);   // dọn point cũ trước khi upsert lại
        source.ChunkCount = await BuildAndUpsertAsync(source, ct);
        await db.SaveChangesAsync(ct);
        logger.LogInformation("RAG grounding: reindex nguồn {Id} — {Chunks} chunk", id, source.ChunkCount);
        return Map(source);
    }

    // ── CONTEXT7 search / ingest ────────────────────────────────────────────────
    public async Task<IReadOnlyList<Context7SearchResult>> Context7SearchAsync(
        string libraryName, string? query, CancellationToken ct = default)
    {
        var libs = await context7.SearchAsync(libraryName, query, ct);
        return libs.Select(l => new Context7SearchResult(l.Id, l.Title, l.Reputation, l.Snippets)).ToList();
    }

    public async Task<KnowledgeSourceResponse> Context7IngestAsync(
        Guid adminId, Context7IngestRequest req, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(req.LibraryId))
            throw new InvalidOperationException("libraryId là bắt buộc.");
        if (req.Topics is not { Count: > 0 })
            throw new InvalidOperationException("Cần ít nhất 1 topic.");
        if (req.JobCategory is null)
            throw new InvalidOperationException("jobCategory là bắt buộc.");

        var topics = req.Topics.Select(t => t.Trim()).Where(t => t.Length > 0).Distinct().ToList();
        if (topics.Count == 0) throw new InvalidOperationException("Cần ít nhất 1 topic hợp lệ.");

        var source = new KnowledgeSource
        {
            Id = Guid.NewGuid(),
            Title = $"Context7: {req.LibraryId} ({string.Join(", ", topics)})",
            JobCategory = req.JobCategory,
            SourceType = KnowledgeSourceType.Context7,
            SourceRef = req.LibraryId.Trim(),
            RawContent = string.Join("\n", topics),   // topics để reindex refetch
            Reputation = null,
            Status = KnowledgeStatus.Active,
            CreatedBy = adminId,
            CreatedAt = DateTime.UtcNow
        };

        source.ChunkCount = await BuildAndUpsertAsync(source, ct);   // Qdrant TRƯỚC
        db.KnowledgeSources.Add(source);
        await db.SaveChangesAsync(ct);           // Postgres SAU

        logger.LogInformation(
            "RAG grounding: Context7 ingest {Lib} ({Topics}) — {Chunks} chunk",
            req.LibraryId, topics.Count, source.ChunkCount);
        return Map(source);
    }

    // ── RETRIEVE (degrade rỗng — điểm degrade DUY NHẤT) ─────────────────────────
    public async Task<IReadOnlyList<GroundingChunk>> RetrieveAsync(
        string jobCategory, string query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Array.Empty<GroundingChunk>();
        try
        {
            var q = query.Length > MaxQueryChars ? query[..MaxQueryChars] : query;
            var vectors = await embedder.EmbedAsync(new[] { q }, "RETRIEVAL_QUERY", ct);
            if (vectors.Count == 0) return Array.Empty<GroundingChunk>();
            return await vectorStore.SearchAsync(jobCategory, vectors[0], _opts.TopK, _opts.ScoreThreshold, ct);
        }
        catch (Exception ex)
        {
            // MỌI lỗi (embed 502 / Qdrant down / timeout) → degrade ungrounded (KHÔNG ném). Caller sinh
            // như thường, không citation. Đây là điểm degrade DUY NHẤT của đường retrieval.
            logger.LogWarning(ex, "RAG grounding: retrieve lỗi ({Cat}) → degrade ungrounded", jobCategory);
            return Array.Empty<GroundingChunk>();
        }
    }

    public async Task<IReadOnlyList<IReadOnlyList<GroundingChunk>>> RetrieveBatchAsync(
        string jobCategory, IReadOnlyList<string> queries, CancellationToken ct = default)
    {
        var empty = queries.Select(_ => (IReadOnlyList<GroundingChunk>)Array.Empty<GroundingChunk>()).ToList();
        if (queries.Count == 0) return empty;
        try
        {
            var texts = queries.Select(q => (q ?? string.Empty).Length > MaxQueryChars ? q![..MaxQueryChars] : q ?? string.Empty).ToList();
            var vectors = await embedder.EmbedAsync(texts, "RETRIEVAL_QUERY", ct);   // 1 lần /embed cho N query
            if (vectors.Count != queries.Count) return empty;

            var result = new List<IReadOnlyList<GroundingChunk>>(queries.Count);
            for (int i = 0; i < queries.Count; i++)
                result.Add(await vectorStore.SearchAsync(jobCategory, vectors[i], _opts.TopK, _opts.ScoreThreshold, ct));
            return result;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "RAG grounding: retrieve-batch lỗi ({Cat}) → degrade ungrounded", jobCategory);
            return empty;
        }
    }

    // ── helpers ─────────────────────────────────────────────────────────────────
    private const int MaxQueryChars = 4000;

    // Chunk theo source_type → embed (RETRIEVAL_DOCUMENT) → upsert Qdrant. Trả số point đã upsert.
    // KHÔNG ghi/save row (caller lo) — nhưng ĐÃ upsert Qdrant (thứ tự Qdrant-trước-Postgres).
    private async Task<int> BuildAndUpsertAsync(KnowledgeSource source, CancellationToken ct)
    {
        var raw = await BuildRawChunksAsync(source, ct);
        if (raw.Count == 0)
            throw new InvalidOperationException("Nguồn không tạo được chunk nào (nội dung rỗng?).");

        var vectors = await embedder.EmbedAsync(
            raw.Select(c => c.Content).ToList(), "RETRIEVAL_DOCUMENT", ct);

        var jobCat = source.JobCategory?.ToString() ?? GeneralCategory;
        var points = raw.Select((c, i) => new VectorPoint(
            Id: Guid.NewGuid(), Vector: vectors[i],
            SourceId: source.Id, JobCategory: jobCat, Ordinal: i,
            Content: c.Content, SourceUrl: c.SourceUrl, SourceTitle: c.SourceTitle)).ToList();

        await vectorStore.UpsertAsync(points, ct);
        return points.Count;
    }

    private const string GeneralCategory = "General";

    private record RawChunk(string Content, string SourceUrl, string SourceTitle);

    // Dispatch chunking theo source_type. Manual/Url dùng IChunker (heading/window); Context7 refetch snippet.
    private async Task<List<RawChunk>> BuildRawChunksAsync(KnowledgeSource source, CancellationToken ct)
    {
        switch (source.SourceType)
        {
            case KnowledgeSourceType.Manual:
                {
                    if (string.IsNullOrWhiteSpace(source.RawContent))
                        throw new InvalidOperationException("Nguồn Manual không có nội dung để (re)index.");
                    return chunker.Chunk(KnowledgeSourceType.Manual, source.RawContent)
                        .Select(c => new RawChunk(c.Content, string.Empty, c.SectionTitle ?? source.Title))
                        .ToList();
                }
            case KnowledgeSourceType.Url:
                {
                    if (string.IsNullOrWhiteSpace(source.SourceRef))
                        throw new InvalidOperationException("Nguồn Url thiếu URL để (re)index.");
                    var html = await urlFetcher.FetchAsync(source.SourceRef, ct);
                    return chunker.Chunk(KnowledgeSourceType.Url, html)
                        .Select(c => new RawChunk(c.Content, source.SourceRef, c.SectionTitle ?? source.Title))
                        .ToList();
                }
            case KnowledgeSourceType.Context7:
                {
                    if (string.IsNullOrWhiteSpace(source.SourceRef) || string.IsNullOrWhiteSpace(source.RawContent))
                        throw new InvalidOperationException("Nguồn Context7 thiếu libraryId/topics để (re)index.");
                    var topics = source.RawContent.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    var result = new List<RawChunk>();
                    foreach (var topic in topics)
                    {
                        var snippets = await context7.GetContextAsync(source.SourceRef, topic, ct);
                        foreach (var s in snippets)
                            // Snippet Context7 đã phân đoạn sẵn → 1 chunk (chunker split chỉ khi quá dài).
                            foreach (var c in chunker.Chunk(KnowledgeSourceType.Context7, s.Content))
                                result.Add(new RawChunk(c.Content, s.SourceUrl ?? string.Empty, s.Title));
                    }
                    return result;
                }
            default:
                throw new InvalidOperationException($"source_type không hỗ trợ: {source.SourceType}");
        }
    }

    private static KnowledgeSourceResponse Map(KnowledgeSource s) => new(
        s.Id, s.Title, s.JobCategory?.ToString(), s.SourceType.ToString(),
        s.SourceRef, s.Reputation, s.Status.ToString(), s.ChunkCount, s.CreatedAt);
}
