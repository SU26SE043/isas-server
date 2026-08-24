using System.Text;
using System.Text.RegularExpressions;
using Isas.InterviewService.Enums;
using Isas.InterviewService.Services.Interfaces;

namespace Isas.InterviewService.Services;

// RAG grounding — chunker theo source_type. Xấp xỉ token ~4 ký tự (đủ cho ranh giới ngữ nghĩa).
// KHÔNG xé code khỏi giải thích: tách ở heading/đoạn TRƯỚC, cửa sổ chỉ khi 1 section vẫn quá dài.
public partial class Chunker : IChunker
{
    // ~350–500 token/window, overlap ~60–80 token → quy ra ký tự (×4).
    private const int MaxChunkChars = 1800;   // ~450 token
    private const int OverlapChars = 280;     // ~70 token
    private const int MinChunkChars = 120;    // merge mảnh quá ngắn (~30 token)

    // RAG-1 — SÀN ĐỘ DÀI cho một section. Khác `MinChunkChars` (vốn chỉ dùng để GỘP mảnh đuôi
    // vào window trước, không phải để LOẠI): trước bản này không có sàn nào cho section, nên
    // "Agile Agile" hay một cụm hai chữ dưới nút điều hướng vẫn thành một chunk và vẫn được
    // nhét vào prompt sinh bài giảng như thể là tài liệu.
    //
    // Chọn 60 THEO SỐ ĐO, không theo cảm tính: đo trên các chunk đã thực sự được dùng để sinh
    // bài, 57 chunk dưới 60 ký tự thì KHÔNG cái nào mang nghĩa. Đặt thấp hơn `MinChunkChars`
    // một cách có chủ đích — 120 là ngưỡng "đủ dài để đứng riêng", còn đây là ngưỡng "đủ dài để
    // KHÔNG phải rác"; nhập hai ngưỡng làm một sẽ vứt luôn những đoạn ngắn nhưng có nội dung.
    private const int MinSectionChars = 60;

    // RAG-1 — heading do NÚT ĐIỀU HƯỚNG của trang web sinh ra, không phải tiêu đề nội dung.
    // Quan sát thật trên corpus: "Help improve MDN" (49 chunk của NN/g toàn dạng này),
    // "Related Topics", "Notes", "In this article". Giữ thành hằng số CÓ TÊN để nhìn là biết

    public IReadOnlyList<TextChunk> Chunk(KnowledgeSourceType type, string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return Array.Empty<TextChunk>();

        var sections = type switch
        {
            // Context7 snippet đã phân đoạn sẵn (title + mô tả + code ghép cùng) → 1 section, KHÔNG parse HTML.
            KnowledgeSourceType.Context7 => new List<(string Title, string Body)> { (string.Empty, content.Trim()) },
            KnowledgeSourceType.Url => SplitHtmlByHeading(content),
            _ => SplitMarkdownByHeading(content),   // Manual
        };

        // RAG-1 — hai bộ lọc dưới chỉ áp cho nguồn CÀO TỪ WEB. Đo được: 100% chunk rác đến từ
        // `Url` (MDN, NN/g), còn `Manual` là admin TỰ TAY soạn và `Context7` là snippet đã phân
        // đoạn sẵn — áp sàn ở đó là âm thầm vứt nội dung có người cố ý curate, tức đổi một lỗi
        // nhìn thấy được (rác lọt vào) lấy một lỗi KHÔNG nhìn thấy được (mất bài đã duyệt).
        var isScraped = type == KnowledgeSourceType.Url;

        var chunks = new List<TextChunk>();
        foreach (var (title, body) in sections)
        {
            if (string.IsNullOrWhiteSpace(body)) continue;
            // Section quá ngắn thì LOẠI hẳn (xem `MinSectionChars`). Trước đây chỉ bỏ section
            // RỖNG, nên mọi mẩu hai ba chữ ("Agile Agile") đều lọt vào corpus.
            if (isScraped && body.Trim().Length < MinSectionChars) continue;
            var sectionTitle = string.IsNullOrWhiteSpace(title) ? null : title.Trim();
            foreach (var window in Window(body.Trim()))
                chunks.Add(new TextChunk(window, sectionTitle));
        }

        return chunks;
    }

    // Markdown: tách theo dòng heading `#`/`##`/`###` (giữ code block cùng section ngay dưới heading).
    private static List<(string Title, string Body)> SplitMarkdownByHeading(string md)
    {
        var sections = new List<(string, string)>();
        var currentTitle = string.Empty;
        var sb = new StringBuilder();

        void Flush()
        {
            var body = sb.ToString().Trim();
            if (body.Length > 0) sections.Add((currentTitle, body));
            sb.Clear();
        }

        foreach (var line in md.Replace("\r\n", "\n").Split('\n'))
        {
            var m = MarkdownHeadingRegex().Match(line);
            if (m.Success)
            {
                Flush();
                currentTitle = m.Groups[1].Value.Trim();
                continue;
            }
            sb.Append(line).Append('\n');
        }
        Flush();

        // Không có heading nào → toàn văn 1 section.
        if (sections.Count == 0)
            sections.Add((string.Empty, md.Trim()));
        return sections;
    }

    // HTML: tách theo h1–h3. Lấy text heading + strip tag phần thân → plain text section.
    private static List<(string Title, string Body)> SplitHtmlByHeading(string html)
    {
        var sections = new List<(string, string)>();
        var matches = HtmlHeadingRegex().Matches(html);

        if (matches.Count == 0)
        {
            sections.Add((string.Empty, StripHtml(html)));
            return sections;
        }

        // Phần trước heading đầu tiên (nếu có nội dung) = 1 section không tiêu đề.
        var firstStart = matches[0].Index;
        if (firstStart > 0)
        {
            var lead = StripHtml(html[..firstStart]);
            if (!string.IsNullOrWhiteSpace(lead)) sections.Add((string.Empty, lead));
        }

        for (int i = 0; i < matches.Count; i++)
        {
            var title = StripHtml(matches[i].Groups[2].Value);
            var bodyStart = matches[i].Index + matches[i].Length;
            var bodyEnd = i + 1 < matches.Count ? matches[i + 1].Index : html.Length;
            var body = StripHtml(html[bodyStart..bodyEnd]);
            sections.Add((title, body));
        }
        return sections;
    }

    // Cửa sổ trượt trong 1 section: tách theo ĐOẠN (dòng trống) trước, gộp tới MaxChunkChars; đoạn đơn
    // quá dài thì cắt cứng theo ký tự. Overlap để giữ ngữ cảnh giữa 2 window liền kề.
    private static IEnumerable<string> Window(string text)
    {
        if (text.Length <= MaxChunkChars)
        {
            yield return text;
            yield break;
        }

        var paragraphs = ParagraphSplitRegex().Split(text)
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .ToList();

        var buffer = new StringBuilder();
        string? lastEmitted = null;

        foreach (var para in paragraphs)
        {
            // Đoạn đơn dài hơn cả window → cắt cứng theo ký tự (hiếm; code/JSON dài).
            if (para.Length > MaxChunkChars)
            {
                if (buffer.Length > 0) { lastEmitted = buffer.ToString().Trim(); yield return lastEmitted; buffer.Clear(); }
                for (int i = 0; i < para.Length; i += MaxChunkChars - OverlapChars)
                    yield return para.Substring(i, Math.Min(MaxChunkChars, para.Length - i));
                continue;
            }

            if (buffer.Length + para.Length + 2 > MaxChunkChars && buffer.Length > 0)
            {
                lastEmitted = buffer.ToString().Trim();
                yield return lastEmitted;
                buffer.Clear();
                // Overlap: mang đuôi window trước sang đầu window sau.
                if (lastEmitted.Length > OverlapChars)
                    buffer.Append(lastEmitted[^OverlapChars..]).Append("\n\n");
            }
            buffer.Append(para).Append("\n\n");
        }

        var tail = buffer.ToString().Trim();
        if (tail.Length == 0) yield break;
        // Merge mảnh đuôi quá ngắn vào window trước (tránh chunk 1 câu lạc lõng).
        if (tail.Length < MinChunkChars && lastEmitted is not null)
            yield return (lastEmitted + "\n\n" + tail).Trim();
        else
            yield return tail;
    }

    // Tiêu đề có phải nút điều hướng không — so sau khi chuẩn hoá hoa/thường, khớp theo tiền tố
    // vì trang web hay nối thêm tên site ("Help improve MDN").

    private static string StripHtml(string html)
    {
        // RAG-1 — gỡ RUỘT của script/style/noscript và comment TRƯỚC khi bóc thẻ. Trước bản này
        // `StripHtml` chỉ xoá THẺ (`<[^>]+>`), nên `<script>window.dataLayer…gtag("consent"…)</script>`
        // mất thẻ rồi phần JavaScript SỐNG SÓT vào corpus y như văn xuôi — và nó thật sự đã được
        // nhét vào prompt sinh bài giảng (19/687 chunk đo được mang mã cookie-consent, mỗi cái 1.800 ký tự).
        var cleaned = ScriptStyleRegex().Replace(html, " ");
        cleaned = HtmlCommentRegex().Replace(cleaned, " ");
        var noTags = HtmlTagRegex().Replace(cleaned, " ");
        var decoded = System.Net.WebUtility.HtmlDecode(noTags);
        return WhitespaceRegex().Replace(decoded, " ").Trim();
    }

    [GeneratedRegex(@"^#{1,3}\s+(.+)$")]
    private static partial Regex MarkdownHeadingRegex();

    [GeneratedRegex(@"<(h[1-3])\b[^>]*>(.*?)</\1>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex HtmlHeadingRegex();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex HtmlTagRegex();

    // Khớp CẢ cặp thẻ lẫn ruột. `Singleline` để `.` ăn cả xuống dòng — script thật luôn nhiều dòng.
    [GeneratedRegex(@"<(script|style|noscript)\b[^>]*>.*?</\1>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ScriptStyleRegex();

    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex HtmlCommentRegex();

    [GeneratedRegex(@"\n\s*\n")]
    private static partial Regex ParagraphSplitRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
