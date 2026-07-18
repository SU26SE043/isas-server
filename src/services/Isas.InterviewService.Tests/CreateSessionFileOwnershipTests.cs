using Isas.InterviewService.ApplicationDbContext;
using Isas.InterviewService.Entities;
using Isas.InterviewService.Services;
using Isas.InterviewService.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Isas.InterviewService.Tests;

/// <summary>
/// Lỗ IDOR bắt khi rà e2e 2026-07-18: `interview.md` §Validation ghi `cvId`/`jdId` khi tạo session
/// phải là FileRecord **của chính user**, nhưng code không kiểm — `GetParseTextAsync` chỉ lọc theo
/// `Id`. Candidate A truyền `cvId` của candidate B thì CV của B được nạp vào prompt sinh câu hỏi,
/// nên A đọc được nội dung CV người khác thông qua các câu hỏi.
///
/// `CvAnalysisService` kiểm đúng (403) — chính sự lệch đó cho thấy đây là thiếu sót, không phải
/// thiết kế. Bản vá: đọc owner-scoped; file người khác → rỗng, **y như id không tồn tại** (tiền lệ
/// BK15 "non-owner không xác nhận sự tồn tại"), tránh đổi status code làm FE đăng xuất oan.
/// </summary>
public class CreateSessionFileOwnershipTests
{
    // Chỉ dùng nhánh đọc DB nên S3/options không được chạm tới.
    private static StorageService NewStorage(TestDb tdb) =>
        new(NullLogger<StorageService>.Instance, null!,
            Options.Create(new FileStorageOptions()), tdb.Db);

    private static async Task<Guid> SeedFileAsync(InterviewDbContext db, Guid ownerId, string parsedText)
    {
        var file = new FileRecord
        {
            Id = Guid.NewGuid(),
            UserId = ownerId,
            FileType = "CV",
            OriginalName = "cv.pdf",
            StoragePath = "cv/abc.pdf",
            StorageBucket = "isas",
            MimeType = "application/pdf",
            FileSize = 1024,
            ParseStatus = "Parsed",
            ParsedText = parsedText,
            CreatedAt = DateTime.UtcNow,
        };
        db.FileRecords.Add(file);
        await db.SaveChangesAsync();
        return file.Id;
    }

    // Chính lỗ hổng: file của người khác KHÔNG được trả nội dung.
    [Fact]
    public async Task FileCuaNguoiKhac_TraRong()
    {
        using var tdb = new TestDb();
        var owner = Guid.NewGuid();
        var keTanCong = Guid.NewGuid();
        var fileId = await SeedFileAsync(tdb.Db, owner, "BÍ MẬT: CV của người khác");
        var storage = NewStorage(tdb);

        var text = await storage.GetOwnedParsedTextAsync(fileId, keTanCong);

        Assert.Equal(string.Empty, text);
    }

    // Không-regression: chính chủ vẫn đọc được bình thường.
    [Fact]
    public async Task FileCuaChinhChu_DocDuoc()
    {
        using var tdb = new TestDb();
        var owner = Guid.NewGuid();
        var fileId = await SeedFileAsync(tdb.Db, owner, "Nội dung CV của tôi");
        var storage = NewStorage(tdb);

        var text = await storage.GetOwnedParsedTextAsync(fileId, owner);

        Assert.Equal("Nội dung CV của tôi", text);
    }

    // Id không tồn tại → rỗng: cùng hình dạng kết quả với "file người khác" nên không lộ sự tồn tại.
    [Fact]
    public async Task FileKhongTonTai_TraRong_GiongHetFileNguoiKhac()
    {
        using var tdb = new TestDb();
        var storage = NewStorage(tdb);

        var text = await storage.GetOwnedParsedTextAsync(Guid.NewGuid(), Guid.NewGuid());

        Assert.Equal(string.Empty, text);
    }
}
