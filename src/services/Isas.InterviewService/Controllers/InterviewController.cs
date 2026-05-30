using Isas.InterviewService.DTOs;
using Isas.InterviewService.Models;
using Isas.InterviewService.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Isas.InterviewService.Controllers
{
    [ApiController]
    [Route("api/files")]
    //[Authorize]
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

            if (fileType == "cv")
            {
                try
                {
                    parsedCv = await _cvParser.ParseAsync(stream, ct);
                    _logger.LogInformation("CV parsed — email={Email} skills={SkillCount}", parsedCv.Email, parsedCv.Skills.Count);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "CV parsing failed for file {FileName}, continuing with upload.", file.FileName);
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
            var presignedUrl = _storage.GetPresignedUrl(storagePath, expiryMinutes: 60);

            // ── 7. Return response ─────────────────────────────────────────
            return Ok(new UploadFileResponse
            {
                FileId = fileId,
                StoragePath = storagePath,
                PresignedUrl = presignedUrl,
                FileName = file.FileName,
                SizeBytes = file.Length,
                ParsedCv = parsedCv
            });
        }
    }
}
