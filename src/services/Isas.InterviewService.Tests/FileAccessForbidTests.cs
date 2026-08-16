using System.Runtime.CompilerServices;
using System.Security.Claims;
using Isas.InterviewService.Controllers;
using Isas.InterviewService.Entities;
using Isas.InterviewService.Services;
using Isas.InterviewService.Services.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Isas.InterviewService.Tests;

/// <summary>
/// File của người khác phải trả <b>403</b>, không phải 500.
///
/// <para><b>Lỗi được sửa:</b> 5 endpoint file dùng <c>Forbid("Bạn không có quyền truy cập file này")</c>.
/// Overload đó là <c>Forbid(params string[] authenticationSchemes)</c> nên chuỗi tiếng Việt bị hiểu là
/// TÊN authentication scheme. Lúc thực thi kết quả, <c>ForbidAsync</c> tra handler theo tên đó, không
/// thấy, và ném <c>InvalidOperationException</c>; InterviewService không có <c>UseExceptionHandler</c>
/// ⇒ ra 500 kèm stack trace. Với tới được chỉ bằng cách đọc file của người khác — và trước vòng này
/// KHÔNG test nào chạm 5 endpoint đó.</para>
///
/// <para><b>Vì sao assert vào KIỂU kết quả chứ không chạy nó:</b> ngoại lệ chỉ nổ lúc
/// <c>ExecuteResultAsync</c>, mà thực thi được một <c>IActionResult</c> thì cần gần đủ bộ DI của
/// ASP.NET (repo này cố ý không dùng <c>WebApplicationFactory</c>). Thứ kiểm được — và cũng chính là
/// khiếm khuyết — là action có trả về <c>ForbidResult</c> mang scheme bịa hay không.</para>
/// </summary>
public class FileAccessForbidTests
{
    private static InterviewController Build(Mock<IStorageService> storage, Guid? callerId)
    {
        var claims = callerId is Guid id
            ? new[] { new Claim(ClaimTypes.NameIdentifier, id.ToString()) }
            : Array.Empty<Claim>();

        return new InterviewController(
            Mock.Of<ICVParserService>(), storage.Object, NullLogger<InterviewController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")),
                },
            },
        };
    }

    private static Mock<IStorageService> StorageWithFileOwnedBy(Guid ownerId)
    {
        var storage = new Mock<IStorageService>();
        storage.Setup(s => s.GetMetadata(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FileRecord
            {
                Id = Guid.NewGuid(),
                UserId = ownerId,
                OriginalName = "cv.pdf",
                MimeType = "application/pdf",
                StoragePath = "files/cv.pdf",
                ParseStatus = "Parsed",
                CreatedAt = DateTime.UtcNow,
            });
        return storage;
    }

    /// <summary>403 thật (ObjectResult), KHÔNG phải ForbidResult mang scheme bịa.</summary>
    private static void Assert403(IActionResult? result)
    {
        Assert.IsNotType<ForbidResult>(result);

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status403Forbidden, obj.StatusCode);
    }

    [Fact]
    public async Task GetFileMetadata_FileCuaNguoiKhac_403_KhongPhaiForbidResult()
    {
        var storage = StorageWithFileOwnedBy(Guid.NewGuid());
        var controller = Build(storage, callerId: Guid.NewGuid());

        var result = await controller.GetFileMetadata(Guid.NewGuid(), default);

        Assert403(result.Result);
    }

    [Fact]
    public async Task DownloadFile_FileCuaNguoiKhac_403()
    {
        var storage = StorageWithFileOwnedBy(Guid.NewGuid());
        var controller = Build(storage, callerId: Guid.NewGuid());

        Assert403(await controller.DownloadFile(Guid.NewGuid(), default));
    }

    [Fact]
    public async Task GetParsedText_FileCuaNguoiKhac_403()
    {
        var storage = StorageWithFileOwnedBy(Guid.NewGuid());
        var controller = Build(storage, callerId: Guid.NewGuid());

        Assert403(await controller.GetParsedText(Guid.NewGuid(), default));
    }

    [Fact]
    public async Task DeleteFile_FileCuaNguoiKhac_403_VaKhongXoaGi()
    {
        var storage = StorageWithFileOwnedBy(Guid.NewGuid());
        var controller = Build(storage, callerId: Guid.NewGuid());

        Assert403(await controller.DeleteFile(Guid.NewGuid(), default));

        // Chặn phải đứng TRƯỚC lời gọi xoá — 403 mà file vẫn bị xoá thì tệ hơn cả 500.
        storage.Verify(s => s.DeleteFileRecord(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetUserFiles_SubClaimKhongParseDuoc_403()
    {
        var storage = new Mock<IStorageService>();
        var controller = Build(storage, callerId: null);

        Assert403(await controller.GetUserFiles(default));
    }

    /// <summary>
    /// Guard cho CẢ LỚP lỗi, không riêng 5 chỗ đã sửa: <c>Forbid("…")</c> với đối số chuỗi là sai ở
    /// mọi controller. Quét mã nguồn vì đây là lỗi lúc CHẠY (scheme không tồn tại), trình biên dịch
    /// không kêu và test hành vi chỉ phủ đúng endpoint nào có người nghĩ tới việc viết test cho nó.
    /// </summary>
    [Fact]
    public void KhongControllerNaoDuocDungForbidVoiChuoi()
    {
        var controllers = Directory.GetFiles(
            Path.Combine(ServiceRoot(), "Controllers"), "*.cs", SearchOption.AllDirectories);

        Assert.NotEmpty(controllers);   // đối chứng: đường dẫn sai thì test này vô nghĩa

        // Bỏ qua dòng chú thích: chính file này lẫn XML doc của NoFileAccess đều VIẾT RA mẫu sai để
        // giải thích nó. Quét cả comment thì test đỏ vì tài liệu, không phải vì code.
        static bool LaDongCode(string dong)
        {
            var t = dong.TrimStart();
            return !(t.StartsWith("//", StringComparison.Ordinal)
                     || t.StartsWith("*", StringComparison.Ordinal)
                     || t.StartsWith("/*", StringComparison.Ordinal));
        }

        var viPham = controllers
            .Where(f => File.ReadLines(f).Any(d => LaDongCode(d) && d.Contains("Forbid(\"", StringComparison.Ordinal)))
            .Select(Path.GetFileName)
            .ToList();

        Assert.True(viPham.Count == 0,
            "Forbid(\"…\") nhận đối số là TÊN authentication scheme, không phải thông điệp — sẽ ném " +
            "InvalidOperationException lúc thực thi và ra 500 thay vì 403. Dùng " +
            "StatusCode(StatusCodes.Status403Forbidden, new { error = … }). Vi phạm: " +
            string.Join(", ", viPham));
    }

    // Neo theo vị trí file test (không phải thư mục làm việc): worktree của agent chạy ở chỗ khác,
    // và `.git` trong worktree là FILE chứ không phải thư mục nên cách dò ngược `.git` sẽ hỏng.
    private static string ServiceRoot([CallerFilePath] string thisFile = "")
        => Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(thisFile)!, "..", "Isas.InterviewService"));
}
