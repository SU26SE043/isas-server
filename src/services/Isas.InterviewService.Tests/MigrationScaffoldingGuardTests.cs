using System.Runtime.CompilerServices;
using System.Text;

namespace Isas.InterviewService.Tests;

/// <summary>
/// Quét MÃ NGUỒN migration để chặn những thứ EF scaffold ra được nhưng Postgres từ chối — mà nền
/// test KHÔNG THỂ bắt.
///
/// <para><b>Vì sao phải quét nguồn thay vì test hành vi:</b> test dùng SQLite + <c>EnsureCreated</c>,
/// vốn dựng schema từ MODEL và <b>bỏ qua migration hoàn toàn</b>. Một migration vỡ vẫn để 1300+ test
/// xanh 100%. Repo đã dính đúng lớp này ba lần: <c>Sql()</c> thiếu dấu <c>;</c> (DB14, vỡ idempotent
/// script), <c>defaultValue: ""</c> cho cột jsonb (F15, chuỗi rỗng không phải JSON hợp lệ), và lần
/// này <c>UpdateData</c> với <c>columns: new string[0]</c>.</para>
///
/// <para><b>⚠ PHẢI bỏ chú thích trước khi quét.</b> Lượt đầu của chính bộ test này ĐỎ vì nó bắt đúng
/// đoạn comment GIẢI THÍCH lỗi — cả trong migration mới lẫn trong
/// <c>AddScoringScopeAndQuestionTargets</c>, nơi ai đó đã gặp và ghi lại y hệt. Tài liệu mô tả một
/// mẫu sai sẽ bị chính scanner tính là vi phạm; dương tính giả làm guard tệ hơn không có, vì người
/// ta sẽ tắt nó đi.</para>
/// </summary>
public class MigrationScaffoldingGuardTests
{
    // ⚠ `[CallerFilePath]` chứ không phải đi ngược tìm thư mục `.git`: trong git WORKTREE `.git` là
    // một FILE (con trỏ `gitdir:`) chứ không phải thư mục, nên `Directory.Exists` không bao giờ đúng
    // và phép dò sẽ chạy tới `/` rồi ném. Mà worktree chính là cách repo này chạy nhiều agent song
    // song ⇒ mọi worker sẽ thấy test ĐỎ GIẢ ngay trên commit chưa sửa gì.
    private static string MigrationsDir([CallerFilePath] string thisFile = "")
        => Path.Combine(Path.GetDirectoryName(thisFile)!, "..", "Isas.InterviewService", "Migrations");

    private static (string Name, string Code)[] Migrations()
    {
        var dir = MigrationsDir();
        Assert.True(Directory.Exists(dir), $"không thấy thư mục migration: {dir}");
        var files = Directory.GetFiles(dir, "*.cs")
            .Where(f => !f.EndsWith(".Designer.cs") && !f.EndsWith("ModelSnapshot.cs"))
            .Select(f => (Path.GetFileName(f), StripComments(File.ReadAllText(f))))
            .ToArray();
        // Đối chứng dương: quét 0 file thì mọi khẳng định dưới đây đúng một cách vô nghĩa.
        Assert.NotEmpty(files);
        return files;
    }

    /// <summary>
    /// Bỏ DÒNG chú thích. Cố ý chỉ xét dòng có phần trim BẮT ĐẦU bằng ký hiệu chú thích, không cố
    /// phân tích `//` giữa dòng: câu SQL trong migration chứa được cả `--` lẫn `//`, và một bộ tách
    /// thông minh nửa vời sẽ cắt nhầm vào chính nội dung cần kiểm.
    /// </summary>
    internal static string StripComments(string code)
    {
        var sb = new StringBuilder();
        var inBlock = false;
        foreach (var line in code.Split('\n'))
        {
            var trimmed = line.TrimStart();
            if (inBlock)
            {
                if (trimmed.Contains("*/")) inBlock = false;
                sb.Append('\n');
                continue;
            }
            if (trimmed.StartsWith("/*")) { inBlock = !trimmed.Contains("*/"); sb.Append('\n'); continue; }
            if (trimmed.StartsWith("//") || trimmed.StartsWith('*')) { sb.Append('\n'); continue; }
            sb.Append(line).Append('\n');
        }
        return sb.ToString();
    }

    [Fact]
    public void KhongMigrationNao_CoUpdateDataRong()
    {
        // `columns: new string[0]` sinh ra `UPDATE <bảng> SET  WHERE id = ...` — LỖI CÚ PHÁP
        // Postgres. Vì cả script nằm trong MỘT transaction, nó kéo đổ toàn bộ migration.
        //
        // EF phát ra dạng này khi row seed có giá trị đúng bằng mặc định của cột mới: không cột nào
        // cần SET, nhưng nó vẫn scaffold một lời gọi rỗng.
        var offenders = Migrations()
            .Where(m => m.Code.Contains("new string[0]"))
            .Select(m => m.Name)
            .ToList();

        Assert.True(offenders.Count == 0,
            "Migration có UpdateData rỗng (`columns: new string[0]`) — sinh ra `UPDATE ... SET  WHERE`, "
            + "lỗi cú pháp Postgres làm đổ cả transaction. Xoá hẳn những lời gọi đó (chúng không set gì "
            + $"nên xoá là vô hại). File: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void MoiCauSqlThoTrongMigration_KetThucBangDauChamPhay()
    {
        // Tiền lệ DB14: `migrationBuilder.Sql()` thiếu `;` cuối làm VỠ idempotent script lúc deploy
        // (`END) END IF` syntax error) dù `dotnet ef database update` vẫn chạy được — nên cả L3 lẫn
        // test đều không bắt.
        var (offenders, scanned) = ScanRawSql(Migrations());

        // ĐỐI CHỨNG DƯƠNG: "0 vi phạm" chỉ có nghĩa nếu bộ quét thật sự tìm thấy câu SQL để kiểm.
        // Thiếu vế này thì một parser hỏng sẽ báo XANH mãi mãi — đúng dạng "đồng hồ chết".
        Assert.True(scanned > 0, "không quét được câu SQL thô nào — bộ quét hỏng, kết quả vô nghĩa");

        Assert.True(offenders.Count == 0,
            "Câu SQL thô trong migration không kết thúc bằng `;` — idempotent script deploy sẽ vỡ. "
            + $"Vị trí: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void BoQuet_ThatSuBatDuocViPham_DoiChungDuong()
    {
        // Chứng minh hai phép quét trên KHÔNG phải đồng hồ chết, bằng cách cho chúng ăn mẫu vi phạm.
        Assert.Contains("new string[0]", StripComments("            columns: new string[0],"));

        var (offenders, scanned) = ScanRawSql(
            [("Fake.cs", "migrationBuilder.Sql(@\"UPDATE t SET a = 1\");")]);
        Assert.Equal(1, scanned);
        Assert.Single(offenders);

        // ...và KHÔNG bắt oan câu hợp lệ có chứa `);` bên trong (chính chỗ bộ quét ngây thơ bị lừa).
        var (ok, n) = ScanRawSql(
            [("Fake.cs", "migrationBuilder.Sql(@\"CREATE INDEX ix ON t (a);\");")]);
        Assert.Equal(1, n);
        Assert.Empty(ok);
    }

    /// <summary>
    /// Rút nội dung từng <c>migrationBuilder.Sql(@"...")</c>. Đọc verbatim string ĐÚNG CÁCH (tôn
    /// trọng escape <c>""</c>) thay vì tìm <c>");</c> đầu tiên — câu SQL hợp lệ chứa được <c>);</c>
    /// bên trong (vd <c>CREATE INDEX ix ON t (a);</c>) nên bộ quét ngây thơ sẽ cắt sớm rồi báo oan.
    /// </summary>
    private static (List<string> Offenders, int Scanned) ScanRawSql((string Name, string Code)[] files)
    {
        const string marker = "migrationBuilder.Sql(";
        var offenders = new List<string>();
        var scanned = 0;

        foreach (var (name, code) in files)
        {
            var i = 0;
            while ((i = code.IndexOf(marker, i, StringComparison.Ordinal)) >= 0)
            {
                var p = i + marker.Length;
                while (p < code.Length && char.IsWhiteSpace(code[p])) p++;
                if (p + 1 >= code.Length || code[p] != '@' || code[p + 1] != '"') { i = p; continue; }

                p += 2;
                var sb = new StringBuilder();
                while (p < code.Length)
                {
                    if (code[p] == '"')
                    {
                        if (p + 1 < code.Length && code[p + 1] == '"') { sb.Append('"'); p += 2; continue; }
                        break;
                    }
                    sb.Append(code[p++]);
                }

                scanned++;
                if (!sb.ToString().TrimEnd().EndsWith(';')) offenders.Add($"{name} @ offset {i}");
                i = p;
            }
        }
        return (offenders, scanned);
    }
}
