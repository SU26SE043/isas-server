using System.Globalization;
using Isas.CampaignService.DTOs;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Isas.CampaignService.Services
{
    /// <summary>
    /// F16 (FR14, gộp BK8) — xuất bảng kết quả campaign (E5) ra PDF.
    ///
    /// Nguồn số liệu = <see cref="CampaignResultsResponse"/> y hệt bản CSV (E6) — KHÔNG tính lại gì.
    /// Đây là ràng buộc quan trọng nhất của F16: CSV và PDF phải vá được bằng MỘT chỗ, nếu không thì
    /// hai bản xuất của cùng một chiến dịch sẽ trôi khỏi nhau và HR không biết tin bản nào.
    ///
    /// Thư viện: QuestPDF (Community MIT — miễn phí cho tổ chức doanh thu &lt; 1M USD, hợp capstone).
    /// BK8 hoãn PDF vì SkiaSharp cần native lib (libfontconfig/libfreetype) trong container Linux;
    /// nay dùng <c>SkiaSharp.NativeAssets.Linux.NoDependencies</c> theo đúng khuyến nghị của QuestPDF
    /// → libSkiaSharp tự chứa, KHÔNG cần apt-get gì thêm trong Dockerfile.
    /// </summary>
    internal static class CampaignResultsPdf
    {
        static CampaignResultsPdf()
        {
            // Phải đặt TRƯỚC lần sinh PDF đầu tiên. Để trong static ctor (không phải Program.cs) để
            // unit test — vốn gọi thẳng service, không qua host — cũng chạy được.
            QuestPDF.Settings.License = LicenseType.Community;

            // Mặc định QuestPDF NÉM khi font thiếu glyph. Giữ nguyên (không tắt): tên ứng viên tiếng Việt
            // mà rơi mất dấu trong một văn bản HR dùng để ra quyết định thì im lặng còn tệ hơn lỗi.
        }

        /// <param name="results">Bảng kết quả E5 — đã sắp xếp + gán rank + pass/fail sẵn.</param>
        /// <param name="campaignTitle">Tiêu đề chiến dịch (đầu trang, cho HR biết đang cầm báo cáo nào).</param>
        /// <param name="generatedAt">Mốc sinh file — in ra để không nhầm bản cũ với bản mới.</param>
        public static byte[] Build(
            CampaignResultsResponse results, string campaignTitle, DateTime generatedAt)
        {
            return Document.Create(doc =>
            {
                doc.Page(page =>
                {
                    // Landscape: 7 cột, trong đó email + họ tên là chuỗi dài — portrait sẽ ép xuống dòng liên tục.
                    page.Size(PageSizes.A4.Landscape());
                    page.Margin(24);
                    page.DefaultTextStyle(x => x.FontSize(9));

                    page.Header().Column(col =>
                    {
                        col.Item().Text(campaignTitle).FontSize(15).Bold();
                        col.Item().Text($"Kết quả phỏng vấn · xuất lúc {generatedAt.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture)} UTC")
                            .FontSize(9).FontColor(Colors.Grey.Darken1);
                        col.Item().Text(results.PassScorePct is int pct
                                ? $"Ngưỡng đạt: {pct}%"
                                : "Ngưỡng đạt: chưa đặt — kết quả do HR quyết")
                            .FontSize(9).FontColor(Colors.Grey.Darken1);
                        col.Item().PaddingTop(6);
                    });

                    page.Content().Table(table =>
                    {
                        table.ColumnsDefinition(columns =>
                        {
                            columns.ConstantColumn(34);    // Hạng
                            columns.RelativeColumn(3);     // Họ tên
                            columns.RelativeColumn(4);     // Email
                            columns.ConstantColumn(50);    // Điểm
                            columns.ConstantColumn(60);    // Kết quả
                            columns.ConstantColumn(95);    // Chấm lúc
                            columns.RelativeColumn(3);     // Cờ
                        });

                        table.Header(header =>
                        {
                            header.Cell().Element(HeaderCell).Text("Hạng");
                            header.Cell().Element(HeaderCell).Text("Họ tên");
                            header.Cell().Element(HeaderCell).Text("Email");
                            header.Cell().Element(HeaderCell).AlignRight().Text("Điểm");
                            header.Cell().Element(HeaderCell).Text("Kết quả");
                            header.Cell().Element(HeaderCell).Text("Chấm lúc");
                            header.Cell().Element(HeaderCell).Text("Cờ");

                            static IContainer HeaderCell(IContainer c) => c
                                .Background(Colors.Grey.Lighten3).BorderBottom(1)
                                .BorderColor(Colors.Grey.Darken1).Padding(5)
                                .DefaultTextStyle(x => x.Bold());
                        });

                        static IContainer Cell(IContainer c) => c
                            .BorderBottom(1).BorderColor(Colors.Grey.Lighten2).Padding(5);

                        foreach (var r in results.Results)
                        {
                            table.Cell().Element(Cell).Text(r.Rank.ToString());
                            // Null = chưa tra được danh tính (đường-1 mời thẳng, chưa có CV) — ô trống,
                            // khớp CSV; KHÔNG bịa "N/A" vào một văn bản dùng để đối chất.
                            table.Cell().Element(Cell).Text(r.FullName ?? string.Empty);
                            table.Cell().Element(Cell).Text(r.Email ?? string.Empty);
                            // InvariantCulture BẮT BUỘC: máy chạy locale vi-VN sẽ in "91,5" còn CSV
                            // (CsvHelper, InvariantCulture) in "91.5" ⇒ hai bản xuất của CÙNG chiến dịch
                            // mâu thuẫn nhau về con số, tuỳ locale của server. Đã bị test bắt tại chỗ.
                            table.Cell().Element(Cell).AlignRight()
                                .Text(r.TotalScore.ToString("0.##", CultureInfo.InvariantCulture));
                            table.Cell().Element(Cell).Text(r.Result ?? string.Empty);
                            table.Cell().Element(Cell)
                                .Text(r.ScoredAt.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture));
                            // SEC-4 + MON1-B4: "type(source):count" ngăn bởi "; " — HELPER DÙNG CHUNG
                            // với CSV (BuildResultsCsv). Fork định dạng ở đây = F16 lệch, không test bắt.
                            table.Cell().Element(Cell)
                                .Text(FlagDto.SummarizeForExport(r.Flags));
                        }

                        // R7: ứng viên có cờ mà CHƯA Scored — nối SAU bảng ranking. Hạng/Điểm/Chấm-lúc để TRỐNG
                        // (chưa chấm); Kết quả = "Chưa chấm". Cùng nguồn `results` với CSV ⇒ hai bản xuất không lệch.
                        foreach (var u in results.UnscoredFlagged)
                        {
                            table.Cell().Element(Cell).Text(string.Empty);
                            table.Cell().Element(Cell).Text(u.FullName ?? string.Empty);
                            table.Cell().Element(Cell).Text(u.Email ?? string.Empty);
                            table.Cell().Element(Cell).AlignRight().Text(string.Empty);
                            table.Cell().Element(Cell).Text("Chưa chấm");
                            table.Cell().Element(Cell).Text(string.Empty);
                            table.Cell().Element(Cell)
                                .Text(FlagDto.SummarizeForExport(u.Flags));   // MON1-B4: cùng helper
                        }
                    });

                    page.Footer().AlignRight().Text(t =>
                    {
                        t.CurrentPageNumber();
                        t.Span(" / ");
                        t.TotalPages();
                    });
                });
            }).GeneratePdf();
        }
    }
}
