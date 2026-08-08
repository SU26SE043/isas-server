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
        // Tra lại điểm uy tín SAU khi rebuild xong: rebuild là việc chính, hỏng thì SaveChanges không
        // chạy nên khỏi tốn thêm một lần gọi Context7 vô ích.
        await RefreshContext7ReputationAsync(source, ct);
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

        var libraryId = req.LibraryId.Trim();

        var source = new KnowledgeSource
        {
            Id = Guid.NewGuid(),
            Title = $"Context7: {libraryId} ({string.Join(", ", topics)})",
            JobCategory = req.JobCategory,
            SourceType = KnowledgeSourceType.Context7,
            SourceRef = libraryId,
            RawContent = string.Join("\n", topics),   // topics để reindex refetch
            // Điểm uy tín do SERVER tự tra, KHÔNG nhận từ client: cả lý do tồn tại của kho này là
            // "tài liệu UY TÍN" (D27), nên để client tự khai điểm uy tín thì ai cũng gắn 10 cho repo
            // của mình được — đúng lỗ mà F10 đã bịt cho `source` của câu hỏi campaign.
            // Trước đây dòng này ghi cứng `null` ⇒ `Context7Client` parse `trustScore` về rồi vứt đi,
            // cột `reputation` tồn tại nhưng KHÔNG có đường ghi nào (đúng mẫu "có tên mà không có ruột").
            Reputation = await TryResolveContext7ReputationAsync(libraryId, ct),
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

    /// <summary>
    /// Nhãn hiển thị của một trích dẫn: LUÔN nêu tên nguồn admin đã curate, kèm mục con nếu có.
    /// </summary>
    /// <remarks>
    /// Trước đây nhãn là <c>SectionTitle ?? source.Title</c> — heading của chunk ĐÈ tên nguồn. Trên trang
    /// thật, heading thường là đồ trang trí điều hướng: đo trên corpus đã nạp, <c>"Help improve MDN"</c>
    /// xuất hiện 5 lần, cạnh <c>"In This Article:"</c> và <c>"Format: 3-Part"</c>. Người dùng nhìn thấy
    /// một trích dẫn tên là "Help improve MDN" thì không kiểm chứng được gì, mà cả lý do tồn tại của
    /// citation là để kiểm chứng (D27).
    /// Tên nguồn đứng TRƯỚC nên nhãn không bao giờ vô nghĩa; mục con giữ lại phía sau để không mất vị trí
    /// trong tài liệu dài (MDN Web Performance: 57 chunk).
    /// <para>
    /// Áp cho <b>CẢ BA</b> source_type. Lần vá đầu bỏ sót nhánh Context7 (nó dùng thẳng tiêu đề snippet)
    /// nên đo trên prod sau khi reindex toàn bộ: 607/687 nhãn đúng, 80 sai — và 80 đúng bằng tổng chunk
    /// của cả 5 nguồn Context7, tức nhánh đó sai <b>100%</b> ("SET TRANSACTION", "CREATE INDEX",
    /// "Built-in React Hooks"). Bất biến "nhãn luôn bắt đầu bằng <c>source.Title</c>" nay được test khoá
    /// theo vòng lặp trên <c>Enum.GetValues&lt;KnowledgeSourceType&gt;()</c> để nhánh thêm về sau không
    /// lọt lần nữa.
    /// </para>
    /// </remarks>
    private static string CitationLabel(KnowledgeSource source, string? sectionTitle)
    {
        var section = sectionTitle?.Trim();
        return string.IsNullOrEmpty(section) || string.Equals(section, source.Title, StringComparison.OrdinalIgnoreCase)
            ? source.Title
            : $"{source.Title} · {section}";
    }

    /// <summary>
    /// Tra điểm uy tín (trustScore) của một thư viện Context7 — SERVER tự hỏi, không nhận từ client.
    /// </summary>
    /// <remarks>
    /// Fail-open có chủ đích: Context7 lỗi/rate-limit/không khớp id → trả <c>null</c> ("chưa xác định")
    /// chứ KHÔNG ném. Nạp corpus là việc admin làm thủ công và tốn tiền embedding; biến một nhãn phụ
    /// thành đường làm hỏng cả lần nạp là đánh đổi tồi — cùng lý do <c>cv_screening</c> không raise khi
    /// thiếu <c>fullName</c>.
    /// </remarks>
    /// <summary>
    /// Reindex một nguồn Context7 thì tra LẠI điểm uy tín — nhưng CHỈ ghi đè khi tra ĐƯỢC giá trị.
    /// </summary>
    /// <remarks>
    /// Trước đây <see cref="ReindexAsync"/> chỉ xoá point + re-chunk + re-embed, còn
    /// <c>Reputation</c> được gán ĐÚNG MỘT LẦN trong <see cref="Context7IngestAsync"/> ⇒ mọi nguồn nạp
    /// TRƯỚC khi có đường ghi đó vĩnh viễn <c>null</c>, reindex bao nhiêu lần cũng không cứu. Đo trên
    /// prod: reindex cả 25 nguồn xong thì <b>5/5</b> nguồn Context7 vẫn <c>reputation = null</c> — tức
    /// bản vá "server tự tra điểm uy tín" không bao giờ với tới dữ liệu đã nạp.
    /// <para>
    /// <b>Tra hụt thì GIỮ giá trị cũ, KHÔNG ghi đè <c>null</c>.</b> <c>null</c> ở đây nghĩa là "không
    /// biết" (Context7 lỗi/rate-limit, hoặc id không nằm trong tập kết quả search) chứ KHÔNG phải "uy
    /// tín bị rút" — ghi đè sẽ XOÁ dữ liệu tốt mỗi lần Context7 rate-limit, mà lần reindex sau không
    /// còn gì để khôi phục (điểm chỉ lấy lại được nếu đúng lúc đó Context7 sống). Cùng hướng fail-open
    /// với <see cref="TryResolveContext7ReputationAsync"/>: nạp/nạp lại corpus là việc admin làm thủ
    /// công và tốn tiền embedding, đừng để một nhãn phụ phá nó.
    /// </para>
    /// </remarks>
    private async Task RefreshContext7ReputationAsync(KnowledgeSource source, CancellationToken ct)
    {
        if (source.SourceType != KnowledgeSourceType.Context7 || string.IsNullOrWhiteSpace(source.SourceRef))
            return;   // Manual/Url không có điểm uy tín → không tốn lời gọi Context7 nào
        source.Reputation = await TryResolveContext7ReputationAsync(source.SourceRef, ct) ?? source.Reputation;
    }

    private async Task<string?> TryResolveContext7ReputationAsync(string libraryId, CancellationToken ct)
    {
        try
        {
            // `libs/search` nhận TÊN thư viện; lấy đoạn cuối của id ("/reactjs/react.dev" → "react.dev")
            // làm từ khoá rồi khớp lại bằng ID ĐẦY ĐỦ — khớp theo tên là cách nhận nhầm thư viện khác
            // trùng tên (search "react" trả về 5 kết quả khác nhau, uy tín từ 8.3 tới 10).
            var name = libraryId.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? libraryId;
            var hits = await context7.SearchAsync(name, null, ct);
            return hits.FirstOrDefault(h => string.Equals(h.Id, libraryId, StringComparison.OrdinalIgnoreCase))
                ?.Reputation;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "RAG grounding: không tra được điểm uy tín Context7 cho {Lib} — để trống", libraryId);
            return null;
        }
    }

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
                        .Select(c => new RawChunk(c.Content, string.Empty, CitationLabel(source, c.SectionTitle)))
                        .ToList();
                }
            case KnowledgeSourceType.Url:
                {
                    if (string.IsNullOrWhiteSpace(source.SourceRef))
                        throw new InvalidOperationException("Nguồn Url thiếu URL để (re)index.");
                    var html = await urlFetcher.FetchAsync(source.SourceRef, ct);
                    return chunker.Chunk(KnowledgeSourceType.Url, html)
                        .Select(c => new RawChunk(c.Content, source.SourceRef, CitationLabel(source, c.SectionTitle)))
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
                            // Nhãn đi qua CitationLabel như Manual/Url: `s.Title` là tiêu đề snippet
                            // ("SET TRANSACTION", "Built-in React Hooks") — hữu ích làm MỤC CON nhưng một
                            // mình nó không nêu nguồn nào cả. Chunker Context7 luôn trả SectionTitle=null
                            // (snippet đã là 1 section) nên `s.Title` chính là mục con của chunk này.
                            foreach (var c in chunker.Chunk(KnowledgeSourceType.Context7, s.Content))
                                result.Add(new RawChunk(c.Content, s.SourceUrl ?? string.Empty, CitationLabel(source, s.Title)));
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
