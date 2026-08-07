using System.Reflection;

namespace Isas.CampaignService.Tests;

/// <summary>
/// DB25b — CampaignService bật <c>EnableRetryOnFailure</c> (<c>Program.cs</c>) và AN TOÀN chỉ vì nó
/// KHÔNG có site <c>BeginTransactionAsync</c> nào. Dưới execution strategy có retry, transaction do
/// người dùng tự mở sẽ ném <c>InvalidOperationException</c> ở MỌI request Postgres — trong khi
/// SQLite (nền của bộ test này) KHÔNG chạy execution strategy nên CI vẫn xanh 100%.
///
/// Interview và Payment được khoá bằng <c>TransactionSiteScanner</c> ("mọi site phải bọc
/// <c>DbRetry.RunAsync</c>"). Campaign thì bất biến KHÁC: **không có site nào**, và service này
/// cũng chưa có class <c>DbRetry</c> để mà bọc. Trước test này, thứ duy nhất giữ bất biến đó là một
/// dòng COMMENT trong <c>Program.cs</c> — mà comment không làm đỏ build.
///
/// Rủi ro không hề lý thuyết: vòng gộp 2026-08-07 vừa thêm nguyên một background service
/// (<c>FaceImagePurger</c>, BK25) vào chính service này.
/// </summary>
public class CampaignRetryInvariantDb25bTests
{
    [Fact]
    public void CampaignService_KhongDuocCoTransactionTuMo_ChungNaoChuaCoDbRetry()
    {
        var serviceDir = Path.Combine(RepoRoot(), "src", "services", "Isas.CampaignService");
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(serviceDir, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                || file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
                continue;

            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var t = lines[i].TrimStart();
                if (t.StartsWith("//") || t.StartsWith("*") || t.StartsWith("/*")) continue;
                if (!lines[i].Contains("BeginTransactionAsync")) continue;
                offenders.Add($"{Path.GetFileName(file)}:{i + 1}: {lines[i].Trim()}");
            }
        }

        Assert.True(offenders.Count == 0,
            "CampaignService bật EnableRetryOnFailure nên transaction tự mở sẽ ném trên Postgres "
            + "(SQLite không bắt được). Cần transaction thì thêm class DbRetry cho Campaign theo mẫu "
            + "Isas.PaymentService/Services/DbRetry.cs rồi bọc, và đổi test này sang TransactionSiteScanner:\n  "
            + string.Join("\n  ", offenders));
    }

    /// <summary>
    /// ĐỐI CHỨNG DƯƠNG. Một luật quét mã nguồn có thể "sạch" chỉ vì nó ĐÃ CHẾT (sai thư mục, regex
    /// trượt) — lúc đó nó im lặng đúng bằng lúc code sạch. Test này chứng minh đường quét còn sống:
    /// thư mục có thật, và có file .cs để quét.
    /// </summary>
    [Fact]
    public void DuongQuet_ConSong_KhongPhaiXanhVoNghia()
    {
        var serviceDir = Path.Combine(RepoRoot(), "src", "services", "Isas.CampaignService");
        Assert.True(Directory.Exists(serviceDir), $"Không thấy thư mục service: {serviceDir}");

        var scanned = Directory.EnumerateFiles(serviceDir, "*.cs", SearchOption.AllDirectories)
            .Count(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"));

        Assert.True(scanned > 10, $"Chỉ quét được {scanned} file .cs — nghi đường dẫn sai.");
    }

    private static string RepoRoot()
    {
        var dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, ".git")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("Không tìm được gốc repo.");
    }
}
