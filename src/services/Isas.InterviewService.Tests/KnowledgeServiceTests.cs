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
        // ⚠ Thân mục CỐ Ý dài hơn `Chunker.MinSectionChars` (60) — xem lý do ở `ChunkerTests`.
        h.UrlFetcher.Setup(u => u.FetchAsync("https://react.dev/learn", It.IsAny<CancellationToken>()))
            .ReturnsAsync("<h2>State</h2><p>useState giữ giá trị giữa các lần render và trả về hàm cập nhật.</p>"
                        + "<h3>Effects</h3><p>useEffect chạy sau render, dùng để đồng bộ với hệ thống bên ngoài.</p>");

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
    // ── Nhãn trích dẫn + điểm uy tín (sửa 2026-08-08) ───────────────────────────────────────
    //
    // Vì sao có nhóm test này: cả hai đường đều KHÔNG được test nào phủ trước đó, mà cả hai đều là
    // thứ NGƯỜI DÙNG NHÌN THẤY hoặc dùng để đánh giá độ tin cậy của nguồn.

    /// Heading của chunk KHÔNG được đè tên nguồn: trên trang thật heading hay là đồ trang trí điều
    /// hướng ("Help improve MDN" xuất hiện 5 lần trong corpus đã nạp) ⇒ trích dẫn thành vô nghĩa,
    /// mà kiểm chứng được là cả lý do citation tồn tại (D27).
    [Fact]
    public async Task Ingest_NhanTrichDan_LuonNeuTenNguonTruoc_KemMucCon()
    {
        using var t = new TestDb();
        var h = Build(t);
        List<VectorPoint>? upserted = null;
        h.VectorStore.Setup(v => v.UpsertAsync(It.IsAny<IReadOnlyList<VectorPoint>>(), It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyList<VectorPoint>, CancellationToken>((p, _) => upserted = p.ToList())
            .Returns(Task.CompletedTask);

        await h.Svc.IngestAsync(Guid.NewGuid(), new CreateKnowledgeRequest(
            "MDN — ARIA / Accessibility", JobCategory.FE, KnowledgeSourceType.Manual,
            "## Help improve MDN\nnội dung phần điều hướng ở đây.", null));

        Assert.NotNull(upserted);
        var label = upserted!.First().SourceTitle;
        Assert.StartsWith("MDN — ARIA / Accessibility", label);            // tên nguồn ĐỨNG TRƯỚC
        Assert.Contains("Help improve MDN", label);                        // mục con KHÔNG bị mất
        Assert.NotEqual("Help improve MDN", label);                        // và KHÔNG được đè tên nguồn
    }

    /// Không có mục con → chỉ tên nguồn, không có dấu phân cách lơ lửng.
    [Fact]
    public async Task Ingest_KhongCoMucCon_NhanChiLaTenNguon()
    {
        using var t = new TestDb();
        var h = Build(t);
        List<VectorPoint>? upserted = null;
        h.VectorStore.Setup(v => v.UpsertAsync(It.IsAny<IReadOnlyList<VectorPoint>>(), It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyList<VectorPoint>, CancellationToken>((p, _) => upserted = p.ToList())
            .Returns(Task.CompletedTask);

        await h.Svc.IngestAsync(Guid.NewGuid(), new CreateKnowledgeRequest(
            "Scrum Guide 2020", JobCategory.BA, KnowledgeSourceType.Manual,
            "đoạn văn thuần không có heading nào cả.", null));

        Assert.Equal("Scrum Guide 2020", upserted!.First().SourceTitle);
    }

    /// Điểm uy tín do SERVER tra, và phải khớp bằng ID ĐẦY ĐỦ: search "react" trả về nhiều thư viện
    /// trùng tên với uy tín khác nhau (8.3 → 10), khớp theo tên là gắn nhầm điểm của thư viện khác.
    [Fact]
    public async Task Context7Ingest_LuuDiemUyTin_KhopTheoIdDayDu()
    {
        using var t = new TestDb();
        var h = Build(t);
        h.Context7.Setup(c => c.SearchAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Context7Library>
            {
                new("/react/react", "React", "8.3", 6165),
                new("/reactjs/react.dev", "React", "10", 6052),
            });
        h.Context7.Setup(c => c.GetContextAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Context7Snippet> { new("useEffect", "nội dung snippet", "https://react.dev/x") });

        var res = await h.Svc.Context7IngestAsync(Guid.NewGuid(),
            new Context7IngestRequest("/reactjs/react.dev", new List<string> { "hooks" }, JobCategory.FE));

        Assert.Equal("10", t.Db.KnowledgeSources.Single(x => x.Id == res.Id).Reputation);
    }

    /// Context7 lỗi → điểm uy tín để trống, KHÔNG làm hỏng cả lần nạp (nạp corpus tốn tiền embedding;
    /// biến một nhãn phụ thành đường làm hỏng là đánh đổi tồi — cùng lý do cv_screening không raise
    /// khi thiếu fullName).
    [Fact]
    public async Task Context7Ingest_TraUyTinLoi_VanNapDuoc_UyTinDeTrong()
    {
        using var t = new TestDb();
        var h = Build(t);
        h.Context7.Setup(c => c.SearchAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Context7 sập"));
        h.Context7.Setup(c => c.GetContextAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Context7Snippet> { new("useEffect", "nội dung snippet", "https://react.dev/x") });

        var res = await h.Svc.Context7IngestAsync(Guid.NewGuid(),
            new Context7IngestRequest("/reactjs/react.dev", new List<string> { "hooks" }, JobCategory.FE));

        Assert.Null(t.Db.KnowledgeSources.Single(x => x.Id == res.Id).Reputation);
        Assert.True(res.ChunkCount > 0);   // nạp VẪN thành công
    }

    // ── Defect 1: nhãn trích dẫn — BẤT BIẾN áp cho MỌI source_type (2026-08-08) ────────────
    //
    // Hai test nhãn ở trên chỉ phủ nhánh Manual, nên nhánh Context7 dùng thẳng `s.Title` lọt qua toàn bộ
    // suite. Đo trên prod sau khi reindex cả 25 nguồn: 687 chunk / 607 nhãn đúng / 80 sai — 80 đúng bằng
    // tổng chunk của 5 nguồn Context7 (10+15+16+23+16) ⇒ nhánh đó sai 100%.
    //
    // Vì thế test này KHÔNG assert một chuỗi cụ thể mà khoá BẤT BIẾN "nhãn luôn bắt đầu bằng tên nguồn",
    // và duyệt `Enum.GetValues<KnowledgeSourceType>()` để source_type thêm về sau hoặc được phủ, hoặc
    // làm test ĐỎ ở nhánh `default` — không có cửa lọt im lặng lần thứ hai.

    /// Mỗi source_type nạp một nguồn có "mục con" là đồ trang trí điều hướng — thứ quan sát được thật
    /// trên corpus prod ("Help improve MDN", "In This Article:", "SET TRANSACTION").
    private static async Task<(string SourceTitle, List<VectorPoint> Points)> IngestForTypeAsync(
        Harness h, KnowledgeSourceType type)
    {
        List<VectorPoint>? upserted = null;
        h.VectorStore.Setup(v => v.UpsertAsync(It.IsAny<IReadOnlyList<VectorPoint>>(), It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyList<VectorPoint>, CancellationToken>((p, _) => upserted = p.ToList())
            .Returns(Task.CompletedTask);

        KnowledgeSourceResponse res;
        switch (type)
        {
            case KnowledgeSourceType.Manual:
                res = await h.Svc.IngestAsync(Guid.NewGuid(), new CreateKnowledgeRequest(
                    "MDN — ARIA / Accessibility", JobCategory.FE, KnowledgeSourceType.Manual,
                    "## Help improve MDN\nphần điều hướng cuối trang.", null));
                break;

            case KnowledgeSourceType.Url:
                h.UrlFetcher.Setup(u => u.FetchAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                    // 🔑 Tiêu đề điều hướng GIỮ NGUYÊN — test này kiểm NHÃN trích dẫn, và chunker cố ý
                    // KHÔNG lọc theo tiêu đề (đo được: bắt 0 mục, mà "Notes" là tiêu đề tài liệu thật).
                    // Chỉ kéo dài thân cho qua sàn độ dài của nguồn `Url`.
                    .ReturnsAsync("<h2>In This Article:</h2><p>Mục lục điều hướng liệt kê các phần chính "
                                + "của trang, giúp người đọc nhảy nhanh tới nội dung cần tìm.</p>");
                res = await h.Svc.IngestAsync(Guid.NewGuid(), new CreateKnowledgeRequest(
                    "PostgreSQL — Transactions", JobCategory.BE, KnowledgeSourceType.Url,
                    null, "https://www.postgresql.org/docs/current/tutorial-transactions.html"));
                break;

            case KnowledgeSourceType.Context7:
                h.Context7.Setup(c => c.GetContextAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(new List<Context7Snippet> { new("SET TRANSACTION", "nội dung snippet", "https://x/y") });
                res = await h.Svc.Context7IngestAsync(Guid.NewGuid(),
                    new Context7IngestRequest("/postgres/postgres", new List<string> { "transactions" }, JobCategory.BE));
                break;

            default:
                // Có source_type MỚI mà không ai bổ sung ca kiểm nhãn → ĐỎ ngay, không lọt im lặng.
                throw new Xunit.Sdk.XunitException(
                    $"source_type mới `{type}` chưa có ca kiểm nhãn trích dẫn — thêm nhánh vào IngestForTypeAsync.");
        }

        Assert.NotNull(upserted);
        Assert.NotEmpty(upserted!);
        return (res.Title, upserted!);
    }

    [Fact]
    public async Task Ingest_NhanTrichDan_MoiSourceType_LuonBatDauBangTenNguon()
    {
        foreach (var type in Enum.GetValues<KnowledgeSourceType>())
        {
            using var t = new TestDb();
            var h = Build(t);

            var (sourceTitle, points) = await IngestForTypeAsync(h, type);

            Assert.All(points, p => Assert.StartsWith(sourceTitle, p.SourceTitle, StringComparison.Ordinal));
        }
    }

    /// Mục con KHÔNG được vứt đi: tài liệu dài có tới 57 chunk, nhãn chỉ có tên nguồn thì mất vị trí.
    /// Đây là vế còn lại của bất biến — nếu ai "sửa" bằng cách trả thẳng `source.Title` thì test trên
    /// vẫn XANH còn test này ĐỎ.
    [Fact]
    public async Task Ingest_NhanTrichDan_MoiSourceType_GiuLaiMucCon()
    {
        var mucConTheoType = new Dictionary<KnowledgeSourceType, string>
        {
            [KnowledgeSourceType.Manual] = "Help improve MDN",
            [KnowledgeSourceType.Url] = "In This Article:",
            [KnowledgeSourceType.Context7] = "SET TRANSACTION",
        };

        foreach (var type in Enum.GetValues<KnowledgeSourceType>())
        {
            using var t = new TestDb();
            var h = Build(t);

            var (sourceTitle, points) = await IngestForTypeAsync(h, type);
            var mucCon = Assert.Contains(type, mucConTheoType);
            var label = points[0].SourceTitle;

            Assert.Contains(mucCon, label, StringComparison.Ordinal);   // mục con còn nguyên
            Assert.NotEqual(mucCon, label);                             // nhưng KHÔNG đè tên nguồn
            Assert.NotEqual(sourceTitle, label);                        // và KHÔNG bị nuốt mất
        }
    }

    // ── Defect 2: reindex phải tra LẠI điểm uy tín (2026-08-08) ────────────────────────────
    //
    // `Reputation` chỉ được gán trong Context7IngestAsync ⇒ nguồn nạp trước khi có đường ghi đó vĩnh
    // viễn null. Đo trên prod: reindex cả 25 nguồn xong, 5/5 nguồn Context7 vẫn null.

    private static void SetupSnippets(Harness h) =>
        h.Context7.Setup(c => c.GetContextAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Context7Snippet> { new("useEffect", "nội dung snippet", "https://react.dev/x") });

    private static async Task<Guid> SeedContext7Source(TestDb t, string? reputation)
    {
        var src = new KnowledgeSource
        {
            Id = Guid.NewGuid(),
            Title = "Context7: /reactjs/react.dev (hooks)",
            JobCategory = JobCategory.FE,
            SourceType = KnowledgeSourceType.Context7,
            SourceRef = "/reactjs/react.dev",
            RawContent = "hooks",
            Reputation = reputation,
            Status = KnowledgeStatus.Active,
            ChunkCount = 1,
            CreatedAt = DateTime.UtcNow
        };
        t.Db.KnowledgeSources.Add(src);
        await t.Db.SaveChangesAsync();
        return src.Id;
    }

    /// Nguồn nạp từ trước bản vá (uy tín null) → reindex phải điền được. Đây chính là ca prod.
    [Fact]
    public async Task Reindex_Context7_TraLaiDiemUyTin_DienDuocChoNguonCu()
    {
        using var t = new TestDb();
        var h = Build(t);
        var id = await SeedContext7Source(t, reputation: null);
        SetupSnippets(h);
        h.Context7.Setup(c => c.SearchAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Context7Library>
            {
                new("/react/react", "React", "8.3", 6165),
                new("/reactjs/react.dev", "React", "10", 6052),
            });

        var res = await h.Svc.ReindexAsync(id, default);

        Assert.Equal("10", res!.Reputation);   // khớp bằng ID ĐẦY ĐỦ, không phải theo tên
        Assert.Equal("10", t.NewContext().KnowledgeSources.Single(x => x.Id == id).Reputation);
    }

    /// Nguồn ĐÃ có uy tín và Context7 trả giá trị MỚI KHÁC → phải CẬP NHẬT, không giữ số cũ.
    ///
    /// <remarks>
    /// Lỗ test do supervisor tìm ra lúc gộp: hai test "giữ giá trị cũ" ngay dưới đều dựng ca tra
    /// HỤT, còn test điền-cho-nguồn-cũ thì seed <c>null</c>. Không ca nào phân biệt được
    /// <c>mới ?? cũ</c> (đúng) với <c>cũ ?? mới</c> (sai) ⇒ đảo thứ tự hai vế vẫn XANH 684/684,
    /// mà hành vi thật là reindex KHÔNG BAO GIỜ làm mới uy tín đã có. `trustScore` bên Context7
    /// đổi theo thời gian, và reindex chính là lúc nên đọc lại — hỏng kiểu này không có triệu
    /// chứng nào, chỉ là số cũ nằm im mãi.
    /// </remarks>
    [Fact]
    public async Task Reindex_Context7_TraVeGiaTriMoi_CapNhat_KhongGiuSoCu()
    {
        using var t = new TestDb();
        var h = Build(t);
        var id = await SeedContext7Source(t, reputation: "10");
        SetupSnippets(h);
        h.Context7.Setup(c => c.SearchAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Context7Library>
            {
                new("/reactjs/react.dev", "React", "9.2", 6052),
            });

        var res = await h.Svc.ReindexAsync(id, default);

        Assert.Equal("9.2", res!.Reputation);
        Assert.Equal("9.2", t.NewContext().KnowledgeSources.Single(x => x.Id == id).Reputation);
    }

    /// Context7 lỗi/rate-limit khi tra lại → GIỮ giá trị cũ. Ghi đè null ở đây là XOÁ dữ liệu tốt vì
    /// một sự cố tạm thời, mà lần reindex sau không còn gì để khôi phục.
    [Fact]
    public async Task Reindex_Context7_TraUyTinLoi_GiuGiaTriCu_KhongGhiDeNull()
    {
        using var t = new TestDb();
        var h = Build(t);
        var id = await SeedContext7Source(t, reputation: "10");
        SetupSnippets(h);
        h.Context7.Setup(c => c.SearchAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Context7 rate-limit"));

        var res = await h.Svc.ReindexAsync(id, default);

        Assert.Equal("10", res!.Reputation);
        Assert.Equal("10", t.NewContext().KnowledgeSources.Single(x => x.Id == id).Reputation);
        Assert.True(res.ChunkCount > 0);   // reindex VẪN thành công (fail-open)
    }

    /// Search trả về nhưng không có id nào khớp (id rơi khỏi tập kết quả) → vẫn là "không biết",
    /// KHÔNG phải "uy tín bị rút" ⇒ giữ giá trị cũ.
    [Fact]
    public async Task Reindex_Context7_SearchKhongKhopId_GiuGiaTriCu()
    {
        using var t = new TestDb();
        var h = Build(t);
        var id = await SeedContext7Source(t, reputation: "10");
        SetupSnippets(h);
        h.Context7.Setup(c => c.SearchAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Context7Library> { new("/react/react", "React", "8.3", 6165) });

        var res = await h.Svc.ReindexAsync(id, default);

        Assert.Equal("10", res!.Reputation);   // KHÔNG lấy "8.3" của thư viện trùng tên, cũng không xoá
    }

    /// Manual/Url không có điểm uy tín → reindex KHÔNG được gọi Context7 (đừng tốn lời gọi vô ích và
    /// đừng để một nguồn không-Context7 phụ thuộc vào Context7 sống hay chết).
    ///
    /// ⚠ PHẢI phủ CẢ Url, không chỉ Manual. Bản đầu của test này chỉ seed Manual — mà Manual có
    /// `SourceRef == null` nên nó được chặn bởi vế `IsNullOrWhiteSpace(SourceRef)`, KHÔNG phải bởi vế
    /// `SourceType != Context7`. Mutation gỡ vế source_type vẫn XANH: đường đi rơi vào
    /// `TryResolveContext7ReputationAsync(null)` → `libraryId.Split` ném NRE → chính catch-all của hàm
    /// đó nuốt → `SearchAsync` không bao giờ được gọi ⇒ `Times.Never` đúng vì LÝ DO SAI.
    /// Url mới là ca thật: `SourceRef` là URL (KHÔNG rỗng) nên gỡ vế source_type là gửi thẳng một URL
    /// sang Context7 làm `libraryId`.
    [Theory]
    [InlineData(KnowledgeSourceType.Manual)]
    [InlineData(KnowledgeSourceType.Url)]
    public async Task Reindex_NguonKhongPhaiContext7_KhongGoiContext7(KnowledgeSourceType type)
    {
        using var t = new TestDb();
        var h = Build(t);
        h.UrlFetcher.Setup(u => u.FetchAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("<h2>State</h2><p>useState giữ giá trị giữa các lần render và trả về hàm cập nhật.</p>");

        var src = new KnowledgeSource
        {
            Id = Guid.NewGuid(),
            Title = type == KnowledgeSourceType.Url ? "PostgreSQL — Transactions" : "seed",
            JobCategory = JobCategory.BE,
            SourceType = type,
            // Url có SourceRef KHÔNG rỗng — đây chính là chỗ vế `SourceType != Context7` phải gánh.
            SourceRef = type == KnowledgeSourceType.Url ? "https://www.postgresql.org/docs/current/x.html" : null,
            RawContent = type == KnowledgeSourceType.Manual ? "## x\nfoo" : null,
            Status = KnowledgeStatus.Active,
            ChunkCount = 1,
            CreatedAt = DateTime.UtcNow
        };
        t.Db.KnowledgeSources.Add(src);
        await t.Db.SaveChangesAsync();

        var res = await h.Svc.ReindexAsync(src.Id, default);

        Assert.NotNull(res);
        h.Context7.Verify(c => c.SearchAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
