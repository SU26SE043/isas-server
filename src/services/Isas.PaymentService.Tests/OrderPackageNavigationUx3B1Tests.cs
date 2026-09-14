using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace Isas.PaymentService.Tests;

/// <summary>
/// UX3-B1 — đường TẠO đơn phải GÁN TƯỜNG MINH navigation <c>Package</c>, không dựa vào EF
/// relationship fixup.
///
/// <para><b>Vì sao cần lưới này.</b> <c>OrderResponse.ToResponse</c> đọc
/// <c>order.Package?.InterviewCredits</c>. Ở hai đường ĐỌC (<c>GetOrderAsync</c>,
/// <c>GetOwnerOrdersAsync</c>) navigation được nạp bằng <c>Include(o =&gt; o.Package)</c> nên an toàn.
/// Nhưng ở đường TẠO, <c>order</c> là thực thể vừa dựng trong bộ nhớ: nếu chỉ set <c>PackageId</c>
/// thì <c>order.Package</c> chỉ được điền nhờ EF fixup, mà fixup CHỈ chạy khi <c>package</c> tình cờ
/// được nạp CÓ tracking.</para>
///
/// <para><b>Đo được, không phải phỏng đoán.</b> Trước khi có bản vá này: thêm
/// <c>.AsNoTracking()</c> vào câu nạp gói trong <c>CreateOrderAsync</c> — một tinh chỉnh hiệu năng
/// rất thường gặp — thì <c>interviewCredits</c> thành <c>null</c> IM LẶNG trên response tạo đơn, mà
/// <b>608/608 test vẫn XANH</b>. Không lỗi, không cảnh báo; biên lai chỉ đơn giản mất một dòng.</para>
///
/// <para><b>Vì sao là lưới QUÉT MÃ NGUỒN chứ không phải test hành vi.</b> Đường tạo đơn gọi PayOS
/// ngay sau <c>ToResponse</c> (<c>response.CheckoutUrl = await CreatePayosLinkAsync(...)</c>), nên
/// không chạy hết được nếu không có credential thật. Thứ cần khoá lại là một BẤT BIẾN VỀ CẤU TRÚC —
/// "khối khởi tạo nào set <c>PackageId</c> thì phải set cả <c>Package</c>" — và bất biến đó kiểm
/// được bằng cách đọc chính mã nguồn. Cùng khuôn với các lưới quét sẵn có trong repo.</para>
///
/// <para>⚠ Đơn tất toán hoá đơn (<c>Kind = InvoiceSettlement</c>) KHÔNG gắn gói: nó set
/// <c>InvoiceId</c> chứ không set <c>PackageId</c>, nên không rơi vào luật này và
/// <c>InterviewCredits</c> ở đó là <c>null</c> — đúng ý đồ.</para>
/// </summary>
public class OrderPackageNavigationUx3B1Tests
{
    [Fact]
    public void KhoiTaoOrder_SetPackageId_ThiPhaiSetCaNavigationPackage()
    {
        var source = File.ReadAllText(OrderServicePath());

        var blocks = NewOrderInitializers(source);
        Assert.NotEmpty(blocks);   // đối chứng: nếu regex không bắt được gì thì lưới là đồng hồ chết

        var thieu = blocks
            .Where(b => b.Contains("PackageId = package.Id"))
            .Where(b => !b.Contains("Package = package,"))
            .ToList();

        Assert.True(
            thieu.Count == 0,
            $"{thieu.Count} khối `new Order {{ ... }}` set PackageId nhưng KHÔNG set navigation Package.\n\n" +
            "OrderResponse.ToResponse đọc order.Package?.InterviewCredits. Ở đường TẠO đơn, thực thể\n" +
            "vừa dựng trong bộ nhớ chỉ được điền navigation nhờ EF relationship fixup — và fixup chỉ\n" +
            "chạy khi `package` được nạp CÓ tracking. Thêm .AsNoTracking() vào câu nạp gói là\n" +
            "interviewCredits thành null IM LẶNG trên biên lai, không test nào kêu.\n\n" +
            "Sửa: thêm `Package = package,` vào khối khởi tạo.\n\n" +
            "Khối thiếu:\n" + string.Join("\n---\n", thieu));
    }

    [Fact]
    public void DuongTatToanHoaDon_KhongGanGoi_KhongBiLuatNayRangBuoc()
    {
        // Khẳng định luật trên KHÔNG bắt oan đơn tất toán hoá đơn: nó set InvoiceId, không set
        // PackageId, nên InterviewCredits null ở đó là ĐÚNG chứ không phải thiếu sót.
        var blocks = NewOrderInitializers(File.ReadAllText(OrderServicePath()));

        var invoiceBlocks = blocks.Where(b => b.Contains("InvoiceId = invoice.Id")).ToList();

        Assert.NotEmpty(invoiceBlocks);
        Assert.All(invoiceBlocks, b => Assert.DoesNotContain("PackageId = package.Id", b));
    }

    /// <summary>Cắt mọi khối khởi tạo <c>new Order { ... }</c> trong mã nguồn.</summary>
    private static List<string> NewOrderInitializers(string source)
    {
        var found = new List<string>();
        foreach (Match m in Regex.Matches(source, @"new Order\s*\{"))
        {
            var open = source.IndexOf('{', m.Index);
            var depth = 0;
            for (var i = open; i < source.Length; i++)
            {
                if (source[i] == '{') depth++;
                else if (source[i] == '}')
                {
                    depth--;
                    if (depth == 0) { found.Add(source[open..(i + 1)]); break; }
                }
            }
        }
        return found;
    }

    // Neo theo đường dẫn file NGUỒN lúc biên dịch — chạy đúng cả trong git worktree, nơi `.git` là
    // một FILE con trỏ chứ không phải thư mục (đi tìm thư mục .git sẽ leo tới `/` rồi ném).
    private static string OrderServicePath([CallerFilePath] string here = "")
        => Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(here)!, "..",
            "Isas.PaymentService", "Services", "OrderService.cs"));
}
