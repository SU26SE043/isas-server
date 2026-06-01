using Isas.InterviewService.DTOs;
using Isas.InterviewService.Models;
using Isas.InterviewService.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Isas.InterviewService.Controllers
{
    [ApiController]
    [Route("api/files")]
    [Authorize]
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
            // ── 1. Validate inputs ─────────────────────────────────────────
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

            // ── 2. Resolve current user ────────────────────────────────────
            var userId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? "anonymous";

            var fileId = Guid.NewGuid().ToString("N");

            _logger.LogInformation("Upload request — user={UserId} type={FileType} file={FileName} size={Size}", userId, fileType, file.FileName, file.Length);

            // ── 3. Parse CV if applicable ──────────────────────────────────
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
                    // Parsing failure is non-fatal — we still store the file
                }

                // Reset stream after parsing
                if (stream.CanSeek)
                    stream.Position = 0;
            }

            // ── 4. Upload to SeaweedFS ─────────────────────────────────────
            string storagePath;
            try
            {
                storagePath = await _storage.UploadAsync(
                    fileStream: stream,
                    fileType: fileType,
                    userId: userId,
                    fileId: fileId,
                    ext: "pdf",
                    contentType: "application/pdf",
                    ct: ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Storage upload failed for user={UserId} fileId={FileId}", userId, fileId);
                return StatusCode(500, new { error = "File storage failed. Please try again." });
            }

            // ── 5. Save metadata to DB ─────────────────────────────────────
            FileRecord fileRecord;
            try
            {
                fileRecord = await _storage.SaveMetadata(
                    fileId: fileId,
                    userId: userId,
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
                // Non-fatal: file is already in SeaweedFS, return partial response
                return StatusCode(500, new { error = "File uploaded but metadata save failed." });
            }

            // ── 6. Generate presigned URL ──────────────────────────────────
            //var presignedUrl = _storage.GetPresignedUrl(storagePath, expiryMinutes: 60);

            // ── 7. Return response ─────────────────────────────────────────
            return Ok(new UploadFileResponse
            {
                FileId = fileId,
                FileType = fileType,
                OriginalName = fileRecord.OriginalName,
                FileSize = fileRecord.FileSize,
                MimeType = fileRecord.MimeType,
                ParsedStatus = parsedCv != null ? "completed" : "failed",
                CreatedAt = fileRecord.CreatedAt
            });
        }

        [HttpGet("{id}")]
        public async Task<ActionResult<FileRecord>> GetFileMetadata(string id, CancellationToken ct)
        {
            var fileRecord = await _storage.GetMetadata(id);

            if (fileRecord == null)
                return NotFound("File không tồn tại");

            var userId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier).Value;

            if (userId == null)
                return Forbid("Bạn không có quyền truy cập file này");

            return fileRecord;
        }

        [HttpGet("{id}/download")]
        public async Task<IActionResult> DownloadFile(string id, CancellationToken ct)
        {
            var fileRecord = await _storage.GetMetadata(id, ct);

            if (fileRecord == null)
                return NotFound("File không tồn tại");

            var userId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier).Value;

            if (userId == null || fileRecord.UserId != Guid.Parse(userId))
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

        [HttpGet("{id}/parsed-text")]
        public async Task<IActionResult> GetParsedText(string id, CancellationToken ct)
        {
            var fileRecord = await _storage.GetMetadata(id, ct);

            if (fileRecord == null)
                return NotFound("File không tồn tại");

            var userId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier).Value;

            if (userId == null || fileRecord.UserId != Guid.Parse(userId))
                return Forbid("Bạn không có quyền truy cập file này");

            try
            {
                var parsedText = await _storage.GetParseText(id, ct);
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
            var userId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier).Value;

            if (userId == null)
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

        [HttpDelete("{id}")]
        public async Task<IActionResult> DeleteFile(string id, CancellationToken ct)
        {
            var fileRecord = await _storage.GetMetadata(id, ct);

            if (fileRecord == null)
                return NotFound("File không tồn tại");

            var userId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier).Value;

            if (userId == null || fileRecord.UserId != Guid.Parse(userId))
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

        [HttpPut("{id}")]
        public async Task<IActionResult> UpdateFile(string id, IFormFile newFile, CancellationToken ct)
        {
            var fileRecord = await _storage.GetMetadata(id, ct);

            if (fileRecord == null)
                return NotFound("File không tồn tại");

            var userId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;

            if (userId == null || fileRecord.UserId != Guid.Parse(userId))
                return Forbid();

            if (newFile == null || newFile.Length == 0)
                return BadRequest("Không có file.");

            var ext = Path.GetExtension(newFile.FileName).ToLowerInvariant();

            if (!AllowedExtensions.Contains(ext))
                return BadRequest("Chỉ PDF.");

            CVParseResult? parsedCv = null;

            await using var stream = newFile.OpenReadStream();

            try
            {
                if (fileRecord.FileType == "cv" || fileRecord.FileType == "jd")
                {
                    parsedCv = await _cvParser.ParseAsync(stream,ct);

                    if (stream.CanSeek)
                        stream.Position = 0;
                }

                await _storage.UpdateFileRecord(
                    fileId: id,
                    stream: stream,
                    originalName: newFile.FileName,
                    fileSize: newFile.Length,
                    contentType: "application/pdf",
                    parsedCv: parsedCv,
                    ct: ct);

                return Ok(new
                {
                    message = "Updated successfully", parsedCv
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Update failed {FileId}", id);
                return StatusCode(500);
            }
        }
    }
}
