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
/// `GET /files/{id}/parsed-text` phải phân biệt "chờ thêm" với "hỏng hẳn".
///
/// <para>Client bước JD của B2C POLL endpoint này. Trước đây mọi trường hợp không-200 đều gộp vào
/// một <c>422</c> kèm message chung, nên client không có cách nào biết nên poll tiếp hay dừng: file
/// đang parse và file parse hỏng trông giống hệt nhau. Kết quả là hoặc poll vô hạn một file không
/// bao giờ có nội dung, hoặc bỏ cuộc trên file chỉ cần chờ thêm một nhịp.</para>
///
/// <para>Giá trị <c>parse_status</c> thực tế trong DB là `completed`/`failed` (StorageService ghi
/// lúc upload) còn hằng <c>Enums.ParseStatus</c> khai báo `pending`/`done`/`failed` — test đi qua cả
/// hai bộ để việc thống nhất hai bộ giá trị sau này không âm thầm đổi status code.</para>
/// </summary>
public class ParsedTextStatusTests
{
    private static readonly Guid Owner = Guid.NewGuid();

    private static InterviewController Build(Mock<IStorageService> storage)
        => new(Mock.Of<ICVParserService>(), storage.Object, NullLogger<InterviewController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        [new Claim(ClaimTypes.NameIdentifier, Owner.ToString())], "test")),
                },
            },
        };

    private static Mock<IStorageService> StorageWith(Guid fileId, string parseStatus, string? parsedText)
    {
        var storage = new Mock<IStorageService>();
        storage.Setup(s => s.GetMetadata(fileId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FileRecord
            {
                Id = fileId,
                UserId = Owner,
                FileType = "jd",
                OriginalName = "jd.pdf",
                StoragePath = $"jd/{fileId}.pdf",
                StorageBucket = "isas-files",
                MimeType = "application/pdf",
                FileSize = 1024,
                ParsedText = parsedText,
                ParseStatus = parseStatus,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
        if (parsedText is not null)
            storage.Setup(s => s.GetParseTextAsync(fileId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(parsedText);
        return storage;
    }

    private static string? StatusOf(object? value)
        => value?.GetType().GetProperty("parsedStatus")?.GetValue(value) as string;

    [Fact]
    public async Task ParseXong_Tra200_KemParsedStatusCompleted()
    {
        var fileId = Guid.NewGuid();
        var ctrl = Build(StorageWith(fileId, "completed", "Nội dung JD"));

        var result = await ctrl.GetParsedText(fileId, default);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Equal("completed", StatusOf(ok.Value));
        Assert.Equal("Nội dung JD",
            ok.Value!.GetType().GetProperty("parsedText")!.GetValue(ok.Value));
    }

    // `done` là hằng trong Enums.ParseStatus; phải cùng nghĩa với `completed` mà StorageService ghi.
    [Fact]
    public async Task ParseStatusDone_CungTinhLaHoanTat_200()
    {
        var fileId = Guid.NewGuid();
        var ctrl = Build(StorageWith(fileId, "done", "Nội dung JD"));

        var ok = Assert.IsType<OkObjectResult>(await ctrl.GetParsedText(fileId, default));
        Assert.Equal("completed", StatusOf(ok.Value));
    }

    [Fact]
    public async Task DangParse_Tra202_DeClientPollTiep()
    {
        var fileId = Guid.NewGuid();
        var ctrl = Build(StorageWith(fileId, "pending", null));

        var accepted = Assert.IsType<AcceptedResult>(await ctrl.GetParsedText(fileId, default));
        Assert.Equal(StatusCodes.Status202Accepted, accepted.StatusCode);
        Assert.Equal("pending", StatusOf(accepted.Value));
    }

    // Chưa có parsed_text mà trạng thái không nói gì (`completed` do row cũ) vẫn là "chưa có nội
    // dung để trả" — 202 chứ không phải 200 với chuỗi rỗng, vì 200 kèm text rỗng làm client tưởng
    // JD trắng thật và ghi đè ô nhập của user.
    [Fact]
    public async Task ChuaCoParsedText_Tra202_KhongPhai200Rong()
    {
        var fileId = Guid.NewGuid();
        var ctrl = Build(StorageWith(fileId, "completed", null));

        var accepted = Assert.IsType<AcceptedResult>(await ctrl.GetParsedText(fileId, default));
        Assert.Equal("pending", StatusOf(accepted.Value));
    }

    [Fact]
    public async Task ParseHong_Tra409_DeClientNgungPoll()
    {
        var fileId = Guid.NewGuid();
        var ctrl = Build(StorageWith(fileId, "failed", null));

        var conflict = Assert.IsType<ConflictObjectResult>(await ctrl.GetParsedText(fileId, default));
        Assert.Equal(StatusCodes.Status409Conflict, conflict.StatusCode);
        Assert.Equal("failed", StatusOf(conflict.Value));
    }

    // Lỗi hạ tầng (DB/storage) KHÁC ba trạng thái parse ở trên: 500, không phải 422 chung như cũ.
    [Fact]
    public async Task LoiHaTang_Tra500_KhongLanVoiTrangThaiParse()
    {
        var fileId = Guid.NewGuid();
        var storage = StorageWith(fileId, "completed", "Nội dung JD");
        storage.Setup(s => s.GetParseTextAsync(fileId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("DB down"));
        var ctrl = Build(storage);

        var error = Assert.IsType<ObjectResult>(await ctrl.GetParsedText(fileId, default));
        Assert.Equal(StatusCodes.Status500InternalServerError, error.StatusCode);
    }
}
