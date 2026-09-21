using System.Text;
using Isas.Shared.Files;
using Xunit;

namespace Isas.Shared.Tests.Files;

/// <summary>
/// Đo trên prod 21/09: PdfPig trả <c>"Pro\0cient"</c> cho chữ ghép "fi" của một CV mẫu ⇒ Postgres
/// <c>22021</c> ở INSERT <c>cv_submission</c> ⇒ cả lô 4 CV đổ. Các ca dưới khoá: (1) sanitizer bỏ
/// đúng lớp ký tự và GIỮ cấu trúc dòng; (2) bộ trích DÙNG CHUNG thật sự đi qua sanitizer — fixture
/// là một PDF tự dựng (~600 byte, Helvetica, chuỗi text mang byte 0x00/0x01) mà PdfPig đã được đo
/// là trả nguyên byte đó về (đối chứng dương ở test cuối).
/// </summary>
public class ExtractedTextSanitizerTests
{
    [Fact]
    public void Strip_BoNul_VaC0C1_GiuXuongDongTab()
    {
        var input = "Pro\0cient" + (char)0x01 + " in\tPHP\r\nand" + (char)0x85 + " My" + (char)0x1F + "SQL";

        var result = ExtractedTextSanitizer.Strip(input, out var removed);

        Assert.Equal("Procient in\tPHP\r\nand MySQL", result);
        Assert.Equal(4, removed);
    }

    [Fact]
    public void Strip_TextSach_TraCungInstance_Removed0()
    {
        var input = "Backend Developer\n6 năm PHP/MySQL\tHCM";

        var result = ExtractedTextSanitizer.Strip(input, out var removed);

        Assert.Same(input, result);
        Assert.Equal(0, removed);
    }

    [Fact]
    public void Strip_KhongBiaKyTuThayThe()
    {
        // \0 nghĩa là "không biết glyph gì" — không được đoán thành "fi" hay khoảng trắng.
        Assert.Equal("Procient", ExtractedTextSanitizer.Strip("Pro\0cient"));
    }

    // ── Bộ trích thật: PDF tự dựng có byte 0x00 và 0x01 trong chuỗi text ─────────────────────
    [Fact]
    public async Task PdfTextExtractor_LocNulKhoiTextTrichDuoc()
    {
        using var pdf = new MemoryStream(BuildPdfWithControlBytes("Pro\0cient in PHP" + (char)0x01 + " and MySQL"));

        var result = await new PdfTextExtractor().ExtractAsync(pdf);

        Assert.DoesNotContain('\0', result.RawText);
        Assert.DoesNotContain((char)0x01, result.RawText);
        Assert.Contains("Procient in PHP and MySQL", result.RawText);
        Assert.Equal(1, result.PageCount);
    }

    // Đối chứng dương cho fixture: KHÔNG qua sanitizer thì PdfPig có trả \0 thật không? Nếu fixture
    // không sinh \0 thì test trên xanh vô nghĩa. Dựng PDF rồi đọc thẳng bằng PdfPig.
    [Fact]
    public void Fixture_PdfPigTraNguyenByteDieuKhien_KhiKhongLoc()
    {
        using var document = UglyToad.PdfPig.PdfDocument.Open(BuildPdfWithControlBytes("Pro\0cient" + (char)0x01));
        var words = string.Join(" ", document.GetPages().First().GetWords().Select(w => w.Text));

        Assert.Contains('\0', words);
        Assert.Contains((char)0x01, words);
    }

    /// <summary>PDF 1.4 không nén, 1 trang, Helvetica, một chuỗi <c>(…) Tj</c> chứa nguyên byte điều khiển.</summary>
    private static byte[] BuildPdfWithControlBytes(string text)
    {
        var textBytes = Encoding.Latin1.GetBytes(text);
        var content = Concat(Encoding.ASCII.GetBytes("BT /F1 12 Tf 50 700 Td ("), textBytes, Encoding.ASCII.GetBytes(") Tj ET"));
        var objects = new List<byte[]>
        {
            Encoding.ASCII.GetBytes("<< /Type /Catalog /Pages 2 0 R >>"),
            Encoding.ASCII.GetBytes("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            Encoding.ASCII.GetBytes("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>"),
            Concat(Encoding.ASCII.GetBytes($"<< /Length {content.Length} >>\nstream\n"), content, Encoding.ASCII.GetBytes("\nendstream")),
            Encoding.ASCII.GetBytes("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"),
        };

        using var ms = new MemoryStream();
        void Write(string s) { var b = Encoding.ASCII.GetBytes(s); ms.Write(b, 0, b.Length); }
        Write("%PDF-1.4\n");
        var offsets = new List<long>();
        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(ms.Position);
            Write($"{i + 1} 0 obj\n");
            ms.Write(objects[i], 0, objects[i].Length);
            Write("\nendobj\n");
        }
        var xref = ms.Position;
        Write($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var off in offsets) Write($"{off:D10} 00000 n \n");
        Write($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return ms.ToArray();
    }

    private static byte[] Concat(params byte[][] parts)
    {
        var result = new byte[parts.Sum(p => p.Length)];
        var pos = 0;
        foreach (var p in parts) { Buffer.BlockCopy(p, 0, result, pos, p.Length); pos += p.Length; }
        return result;
    }
}
