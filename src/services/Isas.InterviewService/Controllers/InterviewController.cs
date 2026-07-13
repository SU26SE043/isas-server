using System.Security.Claims;
using Isas.InterviewService.DTOs;
using Isas.InterviewService.Entities;
using Isas.InterviewService.Models;
using Isas.InterviewService.Services;
using Isas.InterviewService.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Isas.InterviewService.Controllers
{
    [ApiController]
    [Route("api/files")]
    [Authorize(Roles = "Candidate")] // A5 — Files CV/JD = ứng viên tự luyện B2C (interview.md §Files). B2B JD ở CampaignService.
    public class InterviewController : ControllerBase
    {
        private readonly IStorageService _storage;
        private readonly ICVParserService _cvParser;
        private readonly ILogger<InterviewController> _logger;

        private const long MaxFileSizeBytes = 10 * 1024 * 1024;
        private static readonly string[] AllowedExtensions = [".pdf"];
        private static readonly string[] ValidFileTypes = ["cv", "jd"];

        public InterviewController(ICVParserService cvParser, IStorageService storage, ILogger<InterviewController> logger)
        {
            _cvParser = cvParser;
            _storage = storage;
            _logger = logger;
        }

        [HttpPost("upload")]
        [RequestSizeLimit(10_485_760)]
        public async Task<IActionResult> Upload(IFormFile file, [FromQuery] string fileType, CancellationToken ct)
        {
            if (file is null || file.Length == 0)
                return BadRequest(new { error = "No file provided." });

            if (file.Length > MaxFileSizeBytes)
                return BadRequest(new { error = "File exceeds 10 MB limit." });

            var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
            if (!AllowedExtensions.Contains(ext))
                return BadRequest(new { error = "Only PDF files are accepted." });

            fileType = fileType?.ToLowerInvariant() ?? string.Empty;
            if (!ValidFileTypes.Contains(fileType))
                return BadRequest(new { error = $"fileType must be one of: {string.Join(", ", ValidFileTypes)}" });

            // Parse claim an toàn, gán Guid.Empty nếu đang test tắt Auth
            var userIdString = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            var userId = Guid.TryParse(userIdString, out var uid) ? uid : Guid.Empty;

            var fileId = Guid.NewGuid(); // Dùng thẳng Guid

            _logger.LogInformation("Upload request — user={UserId} type={FileType} file={FileName} size={Size}", userId, fileType, file.FileName, file.Length);

            CVParseResult? parsedCv = null;
            await using var stream = file.OpenReadStream();

            if (fileType == "cv" || fileType == "jd")
            {
                try
                {
                    parsedCv = await _cvParser.ParseAsync(stream, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Parsing failed for file {FileName}, continuing with upload.", file.FileName);
                }

                if (stream.CanSeek)
                    stream.Position = 0;
            }

            string storagePath;
            try
            {
                storagePath = await _storage.UploadAsync(
                    fileStream: stream,
                    fileType: fileType,
                    userId: userId, // Truyền Guid
                    fileId: fileId, // Truyền Guid
                    ext: "pdf",
                    contentType: "application/pdf",
                    ct: ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Storage upload failed for user={UserId} fileId={FileId}", userId, fileId);
                return StatusCode(500, new { error = "File storage failed. Please try again." });
            }
            
            FileRecord fileRecord;
            if (parsedCv != null)
            {
                var src = parsedCv.RawText;
                var buf = new char[src.Length];
                int j = 0;

                for (int i = 0; i < src.Length; i++)
                    if (src[i] != '\u0000')
                        buf[j++] = src[i];

                if (j != src.Length)
                    parsedCv.RawText = new string(buf, 0, j);
            }

            try
            {
                fileRecord = await _storage.SaveMetadata(
                    fileId: fileId, // Truyền Guid
                    userId: userId, // Truyền Guid
                    fileType: fileType,
                    originalName: file.FileName,
                    storagePath: storagePath,
                    storageBucket: "isas-files",
                    mimeType: "application/pdf",
                    fileSize: file.Length,
                    parsedCv: parsedCv,
                    ct: ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "DB save failed for fileId={FileId} — file is stored but metadata lost.", fileId);
                return StatusCode(500, new { error = "File uploaded but metadata save failed." });
            }

            return Ok(new UploadFileResponse
            {
                FileId = fileId.ToString(), // Trả string ra cho Client
                FileType = fileType,
                OriginalName = fileRecord.OriginalName,
                FileSize = fileRecord.FileSize,
                MimeType = fileRecord.MimeType,
                ParsedStatus = parsedCv != null ? "completed" : "failed",
                CreatedAt = fileRecord.CreatedAt
            });
        }

        // Đổi tham số sang Guid và kiểm tra an toàn
        [HttpGet("{id:guid}")]
        public async Task<ActionResult<FileRecord>> GetFileMetadata(Guid id, CancellationToken ct)
        {
            var fileRecord = await _storage.GetMetadata(id, ct);
            if (fileRecord == null) return NotFound("File không tồn tại");

            var userIdString = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (!Guid.TryParse(userIdString, out var userId) || fileRecord.UserId != userId)
                return Forbid("Bạn không có quyền truy cập file này");

            return fileRecord;
        }

        [HttpGet("{id:guid}/download")]
        public async Task<IActionResult> DownloadFile(Guid id, CancellationToken ct)
        {
            var fileRecord = await _storage.GetMetadata(id, ct);
            if (fileRecord == null) return NotFound("File không tồn tại");

            var userIdString = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (!Guid.TryParse(userIdString, out var userId) || fileRecord.UserId != userId)
                return Forbid("Bạn không có quyền truy cập file này");

            try
            {
                var fileStream = await _storage.DownloadAsync(fileRecord.StoragePath, ct);
                return File(fileStream, fileRecord.MimeType, fileRecord.OriginalName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error downloading file {FileId}", id);
                return StatusCode(500, "Lỗi khi tải file. Vui lòng thử lại.");
            }
        }

        [HttpGet("{id:guid}/parsed-text")]
        public async Task<IActionResult> GetParsedText(Guid id, CancellationToken ct)
        {
            var fileRecord = await _storage.GetMetadata(id, ct);
            if (fileRecord == null) return NotFound("File không tồn tại");

            var userIdString = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (!Guid.TryParse(userIdString, out var userId) || fileRecord.UserId != userId)
                return Forbid("Bạn không có quyền truy cập file này");

            try
            {
                // Gọi hàm có hậu tố Async
                var parsedText = await _storage.GetParseTextAsync(id, ct);
                return Ok(new { parsedText });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving parsed text for file {FileId}", id);
                return StatusCode(422, "Lỗi khi lấy dữ liệu. Vui lòng thử lại.");
            }
        }

        [HttpGet("files")]
        public async Task<IActionResult> GetUserFiles(CancellationToken ct)
        {
            var userIdString = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (!Guid.TryParse(userIdString, out var userId))
                return Forbid("Bạn không có quyền truy cập file này");

            try
            {
                var files = await _storage.GetFilesByUserId(userId, ct);
                return Ok(files);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving files for user {UserId}", userId);
                return StatusCode(500, "Lỗi khi lấy dữ liệu. Vui lòng thử lại.");
            }
        }

        [HttpDelete("{id:guid}")]
        public async Task<IActionResult> DeleteFile(Guid id, CancellationToken ct)
        {
            var fileRecord = await _storage.GetMetadata(id, ct);
            if (fileRecord == null) return NotFound("File không tồn tại");

            var userIdString = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (!Guid.TryParse(userIdString, out var userId) || fileRecord.UserId != userId)
                return Forbid("Bạn không có quyền xóa file này");

            try
            {
                await _storage.DeleteFileRecord(id, ct);
                return NoContent();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting file {FileId}", id);
                return StatusCode(500, "Lỗi khi xóa file. Vui lòng thử lại.");
            }
        }

        [HttpPut("{id:guid}")]
        public async Task<IActionResult> UpdateFile(Guid id, IFormFile newFile, CancellationToken ct)
        {
            var fileRecord = await _storage.GetMetadata(id, ct);
            if (fileRecord == null) return NotFound("File không tồn tại");

            var userIdString = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (!Guid.TryParse(userIdString, out var userId) || fileRecord.UserId != userId)
                return Forbid();

            if (newFile == null || newFile.Length == 0) return BadRequest("Không có file.");

            var ext = Path.GetExtension(newFile.FileName).ToLowerInvariant();
            if (!AllowedExtensions.Contains(ext)) return BadRequest("Chỉ PDF.");

            CVParseResult? parsedCv = null;
            await using var stream = newFile.OpenReadStream();

            try
            {
                if (fileRecord.FileType == "cv" || fileRecord.FileType == "jd")
                {
                    parsedCv = await _cvParser.ParseAsync(stream, ct);
                    if (stream.CanSeek) stream.Position = 0;
                }

                await _storage.UpdateFileRecord(
                    fileId: id, // Truyền Guid
                    stream: stream,
                    originalName: newFile.FileName,
                    fileSize: newFile.Length,
                    contentType: "application/pdf",
                    parsedCv: parsedCv,
                    ct: ct);

                return Ok(new { message = "Updated successfully", parsedCv });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Update failed {FileId}", id);
                return StatusCode(500);
            }
        }
    }
}