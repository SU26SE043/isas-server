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
        // ⚠ Thân mỗi mục CỐ Ý dài hơn `Chunker.MinSectionChars` (60). Bản cũ của fixture này chỉ
        // ~25 ký tự/mục, và RAG-1 (22/08) thêm sàn độ dài cho nguồn `Url` nên nó rơi hết. Đổi tiền
        // đề chứ không nới sàn: đo trên corpus thật, 57/57 mục dưới 60 ký tự đều là rác điều hướng
        // — không mục nội dung thật nào của MDN/NN-g ngắn tới vậy. Ý định test không đổi: tách theo
        // heading + strip tag.
        var html = "<h2>Hooks</h2><p>useState giữ <b>state</b> giữa các lần render và trả về cặp giá trị cùng hàm cập nhật.</p>"
                 + "<h3>Effects</h3><p>useEffect chạy sau khi render xong, dùng để đồng bộ với hệ thống bên ngoài.</p>";
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

    // ── RAG-1 (22/08) — lọc rác nguồn CÀO TỪ WEB ────────────────────────────────────────────
    //
    // Đo trên corpus thật (687 chunk / 25 nguồn, 22/08): **59** chunk dưới 60 ký tự, KHÔNG cái
    // nào có nghĩa ("Agile Agile" 11 ký tự · "About Books FAQ" 15 · "will-change" 11); và **19**
    // chunk là mã analytics (`window.dataLayer … gtag()`). Chúng đi thẳng vào truy hồi ngữ nghĩa
    // rồi được trích cho người học như nguồn kiểm chứng.
    //
    // 🔑 Hai bộ lọc KHÔNG thay thế được nhau, và số đo nói rõ vì sao: 19 chunk JavaScript kia mỗi
    // cái **1.800 ký tự** — sàn độ dài không đụng tới chúng; ngược lại "Agile Agile" là HTML hợp lệ
    // nên gỡ script không đụng tới nó.
    //
    // ⛔ Đã CÂN NHẮC VÀ BỎ bộ lọc thứ ba (chặn tiêu đề điều hướng "Related Topics"/"Notes"/…): đo
    // trên đúng corpus đó nó bắt được **0** mục, trong khi "Notes" là tiêu đề THẬT rất phổ biến của
    // tài liệu kỹ thuật ⇒ nó chỉ mang rủi ro chặn nhầm mà không mua được gì. Mutation "làm rỗng
    // danh sách" ra XANH chính là dấu hiệu đó — bộ lọc không hề làm việc độc lập nào.
    //
    // 🔑 Cả ba bộ lọc CHỈ áp cho `Url`. `Manual` do admin tự tay soạn và `Context7` là snippet đã
    // phân đoạn sẵn — lọc ở đó là đổi một lỗi NHÌN THẤY ĐƯỢC (rác lọt vào) lấy một lỗi KHÔNG nhìn
    // thấy được (mất bài đã duyệt). Test `Url_KhongLocNguonManual` khoá đúng ranh giới đó.

    private const string DoanThat =
        "<h2>Nguyên tắc heuristic</h2><p>Hệ thống nên luôn cho người dùng biết chuyện gì đang diễn "
        + "ra, thông qua phản hồi phù hợp trong khoảng thời gian hợp lý.</p>";

    [Fact]
    public void Url_BoNguyenKhoiScriptAnalytics()
    {
        // Rác quan sát được: khối <script> lọt vào corpus vì StripHtml cũ chỉ gỡ THẺ, giữ RUỘT.
        var html = "<script>window.dataLayer=window.dataLayer||[];function gtag(){dataLayer.push(arguments);}"
                 + "gtag('js',new Date());gtag('config','G-XXXX');</script>" + DoanThat;
        var chunks = Sut.Chunk(KnowledgeSourceType.Url, html);

        Assert.Single(chunks);
        Assert.DoesNotContain("dataLayer", chunks[0].Content);
        Assert.DoesNotContain("gtag", chunks[0].Content);
        Assert.Equal("Nguyên tắc heuristic", chunks[0].SectionTitle);
    }


    [Fact]
    public void Url_BoMucQuaNgan()
    {
        // "Agile Agile" — mục thật quan sát được trong corpus, 11 ký tự.
        var html = "<h2>Agile</h2><p>Agile</p>" + DoanThat;
        var chunks = Sut.Chunk(KnowledgeSourceType.Url, html);

        Assert.Single(chunks);
        Assert.Equal("Nguyên tắc heuristic", chunks[0].SectionTitle);
    }

    [Fact]
    public void Url_BinhLuanChuaDauLonHonKhongLotVaoNoiDung()
    {
        // 🔑 Bình luận THƯỜNG đã bị `<[^>]+>` xoá hộ rồi — nếu test dừng ở đó thì nó không đo gì.
        // Phần việc RIÊNG của bước gỡ bình luận là ca bình luận **chứa dấu `>`** (bình luận điều
        // kiện, cú pháp template): `<[^>]+>` khớp tới dấu `>` ĐẦU TIÊN nên cắt giữa chừng và thả
        // phần đuôi vào văn bản. Đặt bình luận GIỮA thân mục dài để sàn độ dài không che mất.
        var html = "<h2>Nguyên tắc heuristic</h2><p>Hệ thống nên luôn cho người dùng biết chuyện gì "
                 + "<!--[if lt IE 9]> nội-dung-điều-kiện <![endif]-->đang diễn ra, thông qua phản "
                 + "hồi phù hợp trong khoảng thời gian hợp lý.</p>";
        var chunks = Sut.Chunk(KnowledgeSourceType.Url, html);

        Assert.Single(chunks);
        Assert.DoesNotContain("endif", chunks[0].Content);
        Assert.DoesNotContain("nội-dung-điều-kiện", chunks[0].Content);
        Assert.Contains("phản hồi phù hợp", chunks[0].Content);
    }

    [Fact]
    public void Url_KhongLocNguonManual()
    {
        // 🔑 Cùng một mục ngắn y hệt: `Url` BỎ, `Manual` GIỮ. Admin soạn tay một mục ngắn là cố ý;
        // im lặng vứt nó đi là đổi lỗi nhìn thấy được lấy lỗi không nhìn thấy được.
        const string md = "## Ghi chú\nMột dòng thôi.";
        Assert.Empty(Sut.Chunk(KnowledgeSourceType.Url, "<h2>Ghi chú</h2><p>Một dòng thôi.</p>"));
        Assert.Single(Sut.Chunk(KnowledgeSourceType.Manual, md));
    }
}
