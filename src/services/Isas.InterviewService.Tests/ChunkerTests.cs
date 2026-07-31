using Isas.InterviewService.Enums;
using Isas.InterviewService.Services;

namespace Isas.InterviewService.Tests;

// RAG grounding — chunker theo source_type. Kiểm ranh giới ngữ nghĩa (heading), KHÔNG kiểm số token chính xác.
public class ChunkerTests
{
    private static readonly Chunker Sut = new();

    [Fact]
    public void Manual_SplitsByMarkdownHeading_CapturesSectionTitle()
    {
        var md = "## useState\nTrình bày hook useState.\n\n## useEffect\nGiải thích effect và cleanup.";
        var chunks = Sut.Chunk(KnowledgeSourceType.Manual, md);

        Assert.Equal(2, chunks.Count);
        Assert.Equal("useState", chunks[0].SectionTitle);
        Assert.Contains("useState", chunks[0].Content);
        Assert.Equal("useEffect", chunks[1].SectionTitle);
        Assert.Contains("cleanup", chunks[1].Content);
    }

    [Fact]
    public void Manual_NoHeading_SingleChunk_NullTitle()
    {
        var chunks = Sut.Chunk(KnowledgeSourceType.Manual, "Chỉ một đoạn văn không có heading.");
        Assert.Single(chunks);
        Assert.Null(chunks[0].SectionTitle);
    }

    [Fact]
    public void Url_SplitsByHtmlHeading_StripsTags()
    {
        var html = "<h2>Hooks</h2><p>useState giữ <b>state</b>.</p><h3>Effects</h3><p>useEffect chạy sau render.</p>";
        var chunks = Sut.Chunk(KnowledgeSourceType.Url, html);

        Assert.Equal(2, chunks.Count);
        Assert.Equal("Hooks", chunks[0].SectionTitle);
        // Tag <b> đã bị strip khỏi content.
        Assert.DoesNotContain("<b>", chunks[0].Content);
        Assert.Contains("state", chunks[0].Content);
        Assert.Equal("Effects", chunks[1].SectionTitle);
    }

    [Fact]
    public void Context7_SingleChunk_DoesNotParseHtml()
    {
        // Snippet Context7 đã phân đoạn sẵn (kể cả nếu chứa dấu < > như code) → 1 chunk, KHÔNG tách heading.
        var snippet = "useEffect example:\n\nfunction App() { useEffect(() => {}, []); }";
        var chunks = Sut.Chunk(KnowledgeSourceType.Context7, snippet);
        Assert.Single(chunks);
        Assert.Contains("useEffect", chunks[0].Content);
    }

    [Fact]
    public void Empty_ReturnsNoChunks()
    {
        Assert.Empty(Sut.Chunk(KnowledgeSourceType.Manual, "   "));
        Assert.Empty(Sut.Chunk(KnowledgeSourceType.Context7, ""));
    }

    [Fact]
    public void LongSection_WindowsIntoMultipleChunks()
    {
        // 1 section rất dài (không heading giữa chừng) → phải cắt thành nhiều window.
        var para = string.Join("\n\n", Enumerable.Range(0, 60).Select(i => $"Đoạn văn số {i} " + new string('x', 100)));
        var chunks = Sut.Chunk(KnowledgeSourceType.Manual, para);
        Assert.True(chunks.Count > 1, "Nội dung dài phải được cắt thành nhiều chunk");
    }
}
