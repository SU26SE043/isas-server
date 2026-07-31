using Isas.InterviewService.ApplicationDbContext;
using Isas.InterviewService.DTOs;
using Isas.InterviewService.Entities;
using Isas.InterviewService.Enums;
using Isas.InterviewService.Models;
using Isas.InterviewService.Services;
using Isas.InterviewService.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace Isas.InterviewService.Tests;

// RAG grounding — KnowledgeService: ingest (chunk→embed→upsert+row), DELETE THỨ TỰ (Qdrant trước Postgres),
// retrieve (degrade rỗng), batch (1 embed). Mock IVectorStore + mock embed client (không cần Qdrant/AI thật).
public class KnowledgeServiceTests
{
    private sealed class Harness
    {
        public required KnowledgeService Svc { get; init; }
        public required Mock<IVectorStore> VectorStore { get; init; }
        public required Mock<IAiServiceEmbedder> Embedder { get; init; }
        public required Mock<IContext7Client> Context7 { get; init; }
        public required Mock<IUrlContentFetcher> UrlFetcher { get; init; }
        public required InterviewDbContext Db { get; init; }
    }

    private static Harness Build(TestDb t, int topK = 4, float threshold = 0.5f)
    {
        var vs = new Mock<IVectorStore>();
        var emb = new Mock<IAiServiceEmbedder>();
        // Mặc định: trả đúng số vector = số text (dim 768 rác — test không kiểm giá trị vector).
        emb.Setup(e => e.EmbedAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<string> texts, string _, CancellationToken _) =>
                texts.Select(_ => new float[768]).ToList());
        var ctx = new Mock<IContext7Client>();
        var url = new Mock<IUrlContentFetcher>();

        var svc = new KnowledgeService(
            t.Db, vs.Object, emb.Object, new Chunker(), ctx.Object, url.Object,
            Options.Create(new GroundingOptions { Enabled = true, TopK = topK, ScoreThreshold = threshold }),
            NullLogger<KnowledgeService>.Instance);

        return new Harness { Svc = svc, VectorStore = vs, Embedder = emb, Context7 = ctx, UrlFetcher = url, Db = t.Db };
    }

    private static GroundingChunk Chunk(string id, string url = "https://react.dev/x", string title = "useEffect")
        => new(id, "nội dung chunk " + id, url, title);

    // ── INGEST ──────────────────────────────────────────────────────────────
    [Fact]
    public async Task IngestManual_ChunksEmbeddedAsDocument_UpsertedAndRowSaved()
    {
        using var t = new TestDb();
        var h = Build(t);
        var admin = Guid.NewGuid();
        List<VectorPoint>? upserted = null;
        h.VectorStore.Setup(v => v.UpsertAsync(It.IsAny<IReadOnlyList<VectorPoint>>(), It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyList<VectorPoint>, CancellationToken>((p, _) => upserted = p.ToList())
            .Returns(Task.CompletedTask);

        var req = new CreateKnowledgeRequest("React Hooks", JobCategory.FE, KnowledgeSourceType.Manual,
            Content: "## useState\nHook giữ state.\n\n## useEffect\nHook chạy sau render.", Url: null);

        var res = await h.Svc.IngestAsync(admin, req, default);

        // Embed đúng taskType RETRIEVAL_DOCUMENT.
        h.Embedder.Verify(e => e.EmbedAsync(It.IsAny<IReadOnlyList<string>>(), "RETRIEVAL_DOCUMENT", It.IsAny<CancellationToken>()), Times.Once);
        // Upsert 2 point (2 heading section) + payload jobCategory = "FE".
        Assert.NotNull(upserted);
        Assert.Equal(2, upserted!.Count);
        Assert.All(upserted, p => Assert.Equal("FE", p.JobCategory));
        // Row lưu với chunk_count khớp + RawContent giữ để reindex.
        var row = await t.NewContext().KnowledgeSources.AsNoTracking().SingleAsync(x => x.Id == res.Id);
        Assert.Equal(2, row.ChunkCount);
        Assert.Equal(KnowledgeSourceType.Manual, row.SourceType);
        Assert.False(string.IsNullOrEmpty(row.RawContent));
        Assert.Equal(2, res.ChunkCount);
    }

    [Fact]
    public async Task IngestUrl_FetchesHtml_ChunksAndUpserts()
    {
        using var t = new TestDb();
        var h = Build(t);
        h.UrlFetcher.Setup(u => u.FetchAsync("https://react.dev/learn", It.IsAny<CancellationToken>()))
            .ReturnsAsync("<h2>State</h2><p>useState.</p><h3>Effects</h3><p>useEffect.</p>");

        var req = new CreateKnowledgeRequest("React learn", JobCategory.FE, KnowledgeSourceType.Url,
            Content: null, Url: "https://react.dev/learn");
        var res = await h.Svc.IngestAsync(Guid.NewGuid(), req, default);

        h.UrlFetcher.Verify(u => u.FetchAsync("https://react.dev/learn", It.IsAny<CancellationToken>()), Times.Once);
        h.VectorStore.Verify(v => v.UpsertAsync(It.IsAny<IReadOnlyList<VectorPoint>>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.True(res.ChunkCount >= 2);
        Assert.Equal("https://react.dev/learn", res.SourceRef);
    }

    [Fact]
    public async Task IngestManual_MissingContent_400()
    {
        using var t = new TestDb();
        var h = Build(t);
        var req = new CreateKnowledgeRequest("x", JobCategory.FE, KnowledgeSourceType.Manual, Content: "  ", Url: null);
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Svc.IngestAsync(Guid.NewGuid(), req, default));
    }

    // ── DELETE — THỨ TỰ Qdrant TRƯỚC Postgres (mutation-check) ────────────────
    [Fact]
    public async Task Delete_Success_RemovesRowAfterQdrant()
    {
        using var t = new TestDb();
        var h = Build(t);
        var id = await SeedSource(t);
        h.VectorStore.Setup(v => v.DeleteBySourceAsync(id, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var ok = await h.Svc.DeleteAsync(id, default);

        Assert.True(ok);
        h.VectorStore.Verify(v => v.DeleteBySourceAsync(id, It.IsAny<CancellationToken>()), Times.Once);
        Assert.False(await t.NewContext().KnowledgeSources.AnyAsync(x => x.Id == id));
    }

    // Guard THỨ TỰ: Qdrant xóa TRƯỚC row. Nếu Qdrant lỗi → row Postgres CÒN NGUYÊN (chưa xóa). Nếu ai đảo
    // thứ tự (xóa row trước) thì Qdrant-fail sẽ để lại orphan VECTOR + row đã mất → test này ĐỎ.
    [Fact]
    public async Task Delete_QdrantFails_PostgresRowKept_NoOrphanVector()
    {
        using var t = new TestDb();
        var h = Build(t);
        var id = await SeedSource(t);
        h.VectorStore.Setup(v => v.DeleteBySourceAsync(id, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Qdrant down"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Svc.DeleteAsync(id, default));

        // Row PHẢI còn (Qdrant xóa trước, fail trước khi chạm remove row) → retry được, không orphan vector.
        Assert.True(await t.NewContext().KnowledgeSources.AnyAsync(x => x.Id == id));
    }

    [Fact]
    public async Task Delete_NotFound_ReturnsFalse_NoQdrantCall()
    {
        using var t = new TestDb();
        var h = Build(t);
        var ok = await h.Svc.DeleteAsync(Guid.NewGuid(), default);
        Assert.False(ok);
        h.VectorStore.Verify(v => v.DeleteBySourceAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── RETRIEVE ──────────────────────────────────────────────────────────────
    [Fact]
    public async Task Retrieve_EmbedsQueryAsRetrievalQuery_FiltersJobCategory()
    {
        using var t = new TestDb();
        var h = Build(t, topK: 4, threshold: 0.5f);
        var expected = new List<GroundingChunk> { Chunk("A"), Chunk("B") };
        h.VectorStore.Setup(v => v.SearchAsync("FE", It.IsAny<IReadOnlyList<float>>(), 4, 0.5f, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        var res = await h.Svc.RetrieveAsync("FE", "useEffect cleanup", default);

        Assert.Equal(2, res.Count);
        h.Embedder.Verify(e => e.EmbedAsync(It.IsAny<IReadOnlyList<string>>(), "RETRIEVAL_QUERY", It.IsAny<CancellationToken>()), Times.Once);
    }

    // Degrade: mọi lỗi hạ tầng → RỖNG (KHÔNG ném) — điểm degrade duy nhất.
    [Fact]
    public async Task Retrieve_QdrantThrows_DegradesToEmpty()
    {
        using var t = new TestDb();
        var h = Build(t);
        h.VectorStore.Setup(v => v.SearchAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<float>>(), It.IsAny<int>(), It.IsAny<float>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Qdrant down"));

        var res = await h.Svc.RetrieveAsync("FE", "q", default);
        Assert.Empty(res);   // degrade, KHÔNG ném
    }

    [Fact]
    public async Task Retrieve_EmbedThrows_DegradesToEmpty()
    {
        using var t = new TestDb();
        var h = Build(t);
        h.Embedder.Setup(e => e.EmbedAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AiServiceException("embed 502"));

        Assert.Empty(await h.Svc.RetrieveAsync("FE", "q", default));
    }

    // ── RETRIEVE BATCH — 1 embed cho N query (precompute roadmap) ────────────────
    [Fact]
    public async Task RetrieveBatch_OneEmbedCall_PerQueryResults()
    {
        using var t = new TestDb();
        var h = Build(t);
        var queries = new[] { "q1", "q2", "q3" };
        h.VectorStore.Setup(v => v.SearchAsync("FE", It.IsAny<IReadOnlyList<float>>(), It.IsAny<int>(), It.IsAny<float>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<GroundingChunk> { Chunk("X") });

        var res = await h.Svc.RetrieveBatchAsync("FE", queries, default);

        Assert.Equal(3, res.Count);
        // 1 LẦN /embed cho cả 3 query (không phải 3 lần).
        h.Embedder.Verify(e => e.EmbedAsync(It.Is<IReadOnlyList<string>>(l => l.Count == 3), "RETRIEVAL_QUERY", It.IsAny<CancellationToken>()), Times.Once);
        h.VectorStore.Verify(v => v.SearchAsync("FE", It.IsAny<IReadOnlyList<float>>(), It.IsAny<int>(), It.IsAny<float>(), It.IsAny<CancellationToken>()), Times.Exactly(3));
    }

    [Fact]
    public async Task RetrieveBatch_EmbedThrows_DegradesToPerQueryEmpties()
    {
        using var t = new TestDb();
        var h = Build(t);
        h.Embedder.Setup(e => e.EmbedAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AiServiceException("embed 502"));

        var res = await h.Svc.RetrieveBatchAsync("FE", new[] { "a", "b" }, default);
        Assert.Equal(2, res.Count);
        Assert.All(res, r => Assert.Empty(r));
    }

    private static async Task<Guid> SeedSource(TestDb t)
    {
        var src = new KnowledgeSource
        {
            Id = Guid.NewGuid(),
            Title = "seed",
            JobCategory = JobCategory.FE,
            SourceType = KnowledgeSourceType.Manual,
            RawContent = "## x\nfoo",
            Status = KnowledgeStatus.Active,
            ChunkCount = 1,
            CreatedAt = DateTime.UtcNow
        };
        t.Db.KnowledgeSources.Add(src);
        await t.Db.SaveChangesAsync();
        return src.Id;
    }
}
