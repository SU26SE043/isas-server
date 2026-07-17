using System.Security.Claims;
using Isas.CampaignService.DTOs;
using Isas.CampaignService.Models;
using Isas.CampaignService.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Isas.CampaignService.Controllers
{
    /// <summary>
    /// SEC-2 — cổng xác minh khuôn mặt (face-verify) cho ứng viên B2B. 2 endpoint (JWT Candidate + phải là
    /// thành viên campaign — mirror <see cref="SessionFlagController"/>):
    ///  1) <c>face-enroll</c>: upload ảnh THAM CHIẾU → S3 KEY → gán <c>CampaignMembership.ReferenceImageKey</c>.
    ///  2) <c>face-check</c>: chỉ khi campaign bật <c>FaceVerifyEnabled</c>; upload ảnh LIVE → S3 KEY →
    ///     gọi AIService so khớp → mỗi tín hiệu (no_face/multiple_faces/face_mismatch) → 1 cờ session_flags cho HR.
    /// D13/SEC-5: CHỈ FLAG cho HR, KHÔNG auto-chặn; thiếu ảnh tham chiếu ≠ gian lận (cờ identity_unverified).
    /// AIService đọc CHUNG bucket SeaweedFS → chỉ truyền KEY (GEN-5), không truyền ảnh. Ảnh live chỉ nằm S3 (DATA-3).
    /// </summary>
    [ApiController]
    [Authorize(Roles = "Candidate")]
    public class FaceVerifyController : ControllerBase
    {
        private readonly CampaignDbContext _db;
        private readonly IFileService _file;
        private readonly IAiServiceFaceVerifyClient _faceVerify;
        private readonly ILogger<FaceVerifyController> _logger;

        public FaceVerifyController(
            CampaignDbContext db,
            IFileService file,
            IAiServiceFaceVerifyClient faceVerify,
            ILogger<FaceVerifyController> logger)
        {
            _db = db;
            _file = file;
            _faceVerify = faceVerify;
            _logger = logger;
        }

        // Tín hiệu DANH TÍNH (mirror SessionFlagController) — lưu khi anti_cheat HOẶC face_verify bật.
        private static readonly HashSet<string> IdentitySignals = new(StringComparer.OrdinalIgnoreCase)
            { "face_mismatch", "no_face", "multiple_faces", "identity_unverified" };

        private Guid? GetCandidateId()
            => Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub"), out var g)
                ? g : (Guid?)null;

        // ── 1) POST /campaign/{campaignId}/sessions/{sessionId}/face-enroll — ảnh tham chiếu ─────────
        [HttpPost("campaign/{campaignId:guid}/sessions/{sessionId:guid}/face-enroll")]
        [Consumes("multipart/form-data")]
        public async Task<IActionResult> Enroll(
            Guid campaignId, Guid sessionId, [FromForm] IFormFile image, CancellationToken ct)
        {
            var candidateId = GetCandidateId();
            if (candidateId is null) return Unauthorized();
            if (image is null || image.Length == 0)
                return BadRequest(new { error = "Ảnh tham chiếu rỗng." });

            var (membership, error) = await ResolveMembershipAsync(campaignId, candidateId.Value, ct);
            if (error is not null) return error;

            var key = BuildKey($"campaigns/{campaignId}/candidates/{candidateId}/face-reference", image);
            await _file.UploadAsync(image, key, ct);

            membership!.ReferenceImageKey = key;
            membership.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);

            _logger.LogInformation(
                "SEC-2 enroll: candidate {CandidateId} đặt ảnh tham chiếu campaign {CampaignId} (key {Key}).",
                candidateId, campaignId, key);
            return NoContent();
        }

        // ── 2) POST /campaign/{campaignId}/sessions/{sessionId}/face-check — giám sát khuôn mặt ──────
        [HttpPost("campaign/{campaignId:guid}/sessions/{sessionId:guid}/face-check")]
        [Consumes("multipart/form-data")]
        public async Task<IActionResult> Check(
            Guid campaignId, Guid sessionId, [FromForm] IFormFile image, CancellationToken ct)
        {
            var candidateId = GetCandidateId();
            if (candidateId is null) return Unauthorized();
            if (image is null || image.Length == 0)
                return BadRequest(new { error = "Ảnh giám sát rỗng." });

            var (membership, error) = await ResolveMembershipAsync(campaignId, candidateId.Value, ct);
            if (error is not null) return error;

            var campaign = membership!.Campaign;

            // Campaign không bật face-verify → no-op (không upload/không cờ). Không phải "thi" → 204.
            if (!campaign.FaceVerifyEnabled)
                return NoContent();

            var liveKey = BuildKey($"campaigns/{campaignId}/sessions/{sessionId}/face-live-{Guid.NewGuid():N}", image);
            await _file.UploadAsync(image, liveKey, ct);

            // Chưa enroll ảnh tham chiếu → không so được → cờ identity_unverified (HR duyệt), KHÔNG chặn (SEC-5).
            if (string.IsNullOrWhiteSpace(membership.ReferenceImageKey))
            {
                await RecordFlagsAsync(campaign, sessionId, candidateId.Value,
                    new[] { "identity_unverified" }, "Chưa có ảnh tham chiếu để đối chiếu.", ct);
                return Ok(new FaceCheckResponse
                {
                    Match = false,
                    FaceCount = 0,
                    Signals = new List<string> { "identity_unverified" }
                });
            }

            var result = await _faceVerify.VerifyAsync(membership.ReferenceImageKey!, liveKey, null, ct);

            // Mỗi tín hiệu AIService (no_face/multiple_faces/face_mismatch) → 1 cờ session_flags cho HR (D13).
            await RecordFlagsAsync(campaign, sessionId, candidateId.Value, result.Signals, null, ct);

            _logger.LogInformation(
                "SEC-2 check: candidate {CandidateId} campaign {CampaignId} → match={Match} faces={Faces} signals=[{Signals}]",
                candidateId, campaignId, result.Match, result.FaceCount, string.Join(",", result.Signals));

            return Ok(new FaceCheckResponse
            {
                Match = result.Match,
                FaceCount = result.FaceCount,
                Signals = result.Signals.ToList()
            });
        }

        // ── helpers ──────────────────────────────────────────────────────────────────

        // Membership của candidate trong campaign (kèm Campaign nav). Không tồn tại → 403 (mirror SessionFlagController).
        // Campaign không tồn tại → 404. DB16: membership ở bảng campaign_membership (ReferenceImageKey nằm đây).
        private async Task<(CampaignMembership? membership, IActionResult? error)> ResolveMembershipAsync(
            Guid campaignId, Guid candidateId, CancellationToken ct)
        {
            var membership = await _db.CampaignMemberships
                .Include(m => m.Campaign)
                .FirstOrDefaultAsync(m => m.CampaignId == campaignId && m.CandidateId == candidateId, ct);

            if (membership is null)
            {
                // Phân biệt campaign không tồn tại (404) vs không phải thành viên (403).
                var campaignExists = await _db.Campaigns.AnyAsync(c => c.Id == campaignId, ct);
                return campaignExists
                    ? (null, Forbid())
                    : (null, NotFound(new { error = "Campaign not found." }));
            }
            return (membership, null);
        }

        // Ghi cờ session_flags cho từng tín hiệu (mirror SessionFlagController.RecordFlagAsync gate).
        // Chỉ lưu tín hiệu danh tính khi face_verify bật (hoặc anti_cheat bật) — else no-op idempotent.
        private async Task RecordFlagsAsync(
            Campaign campaign, Guid sessionId, Guid candidateId,
            IEnumerable<string> signals, string? note, CancellationToken ct)
        {
            var added = false;
            foreach (var raw in signals)
            {
                var normalized = raw.Trim().ToLowerInvariant();
                if (string.IsNullOrEmpty(normalized)) continue;

                bool identity = IdentitySignals.Contains(normalized);
                bool shouldPersist = campaign.AntiCheatEnabled || (identity && campaign.FaceVerifyEnabled);
                if (!shouldPersist)
                {
                    _logger.LogDebug(
                        "Bỏ qua cờ '{Signal}' (session {SessionId}): campaign {CampaignId} tắt giám sát.",
                        normalized, sessionId, campaign.Id);
                    continue;
                }

                _db.SessionFlags.Add(new SessionFlag
                {
                    Id = Guid.NewGuid(),
                    SessionId = sessionId,
                    CampaignId = campaign.Id,
                    CandidateId = candidateId,
                    SignalType = normalized,
                    Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
                    DetectedAt = DateTime.UtcNow
                });
                added = true;
            }

            if (added) await _db.SaveChangesAsync(ct);
        }

        // Key S3 deterministic + giữ đuôi file gốc (fallback .jpg). GEN-5: lưu KEY, không full URL.
        private static string BuildKey(string prefix, IFormFile image)
        {
            var ext = Path.GetExtension(image.FileName);
            if (string.IsNullOrWhiteSpace(ext)) ext = ".jpg";
            return prefix + ext;
        }
    }
}
