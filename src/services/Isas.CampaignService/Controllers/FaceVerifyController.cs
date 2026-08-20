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
    /// AIService đọc CHUNG bucket SeaweedFS → chỉ truyền KEY (GEN-5), không truyền ảnh.
    ///
    /// BK25/DATA-3 — ảnh chỉ nằm trong S3, nhưng KEY phải được ghi vào <c>face_images</c> TRƯỚC khi
    /// upload. Trước đây <c>face-check</c> vứt key đi ⇒ 1 ảnh khuôn mặt mỗi ~30 giây suốt buổi thi
    /// trở thành object mồ côi không đếm được, không join được, không dọn được.
    /// Việc dọn theo hạn giữ do <see cref="FaceImagePurger"/> lo.
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

            var (membership, error) = await ResolveMembershipAsync(campaignId, candidateId.Value, sessionId, ct);
            if (error is not null) return error;

            var key = BuildKey($"campaigns/{campaignId}/candidates/{candidateId}/face-reference", image);
            var previousKey = membership!.ReferenceImageKey;

            // BK25 — ghi sổ TRƯỚC khi upload (xem bất biến ở FaceImage): không được để object nằm
            // trong S3 mà không có dòng nào trỏ tới. Sổ trỏ vào object chưa tồn tại thì vô hại.
            await RecordImageAsync(FaceImageKind.Reference, key, campaignId, candidateId.Value, null, ct);

            await _file.UploadAsync(image, key, ct);

            membership.ReferenceImageKey = key;
            membership.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);

            // DATA-2 "ảnh tham chiếu 1 bản/ứng viên/campaign": key deterministic THEO ĐUÔI FILE
            // (BuildKey nối `Path.GetExtension`), nên enroll .jpg rồi enroll lại .png sinh HAI object
            // trong khi membership chỉ trỏ được vào cái sau ⇒ cái trước thành mồ côi. Dọn bản bị thay
            // thế ngay tại đây (best-effort): S3 trước, dòng sổ sau. S3 lỗi → GIỮ dòng sổ để
            // FaceImagePurger dọn khi tới hạn, và KHÔNG làm hỏng lần enroll vừa thành công.
            if (!string.IsNullOrWhiteSpace(previousKey) && previousKey != key)
                await DeleteSupersededReferenceAsync(previousKey!, ct);

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

            var (membership, error) = await ResolveMembershipAsync(campaignId, candidateId.Value, sessionId, ct);
            if (error is not null) return error;

            var campaign = membership!.Campaign;

            // Campaign không bật face-verify → no-op (không upload/không cờ). Không phải "thi" → 204.
            if (!campaign.FaceVerifyEnabled)
                return NoContent();

            var liveKey = BuildKey($"campaigns/{campaignId}/sessions/{sessionId}/face-live-{Guid.NewGuid():N}", image);

            // BK25 — ghi sổ TRƯỚC khi upload. Đây là nguồn phình chính (1 ảnh/~30s/buổi thi); thiếu
            // dòng này thì object thành mồ côi KHÔNG LIỆT KÊ NỔI, không có gì để join khi muốn dọn.
            await RecordImageAsync(FaceImageKind.Live, liveKey, campaignId, candidateId.Value, sessionId, ct);

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

            FaceVerifyResult result;
            try
            {
                result = await _faceVerify.VerifyAsync(membership.ReferenceImageKey!, liveKey, null, ct);
            }
            catch (DownstreamServiceException ex)
            {
                // Lỗi hạ tầng AIService (timeout/5xx/body hỏng) — KHÔNG phải lỗi của ứng viên/HR, và KHÔNG
                // được để lộ 500 trần (mất log rõ ràng, không nhất quán với mọi controller khác trong service
                // vốn đều map DownstreamServiceException → 502, mẫu CampaignController.cs GetSessionTranscript).
                // Ảnh live + dòng sổ (BK25) đã ghi trước đó nên KHÔNG mồ côi dù so khớp thất bại.
                _logger.LogError(ex,
                    "SEC-2 check: AIService face-verify lỗi hạ tầng (candidate {CandidateId} campaign {CampaignId}).",
                    candidateId, campaignId);
                return StatusCode(StatusCodes.Status502BadGateway, new { error = ex.Message });
            }

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
        //
        // Q4 — buộc `sessionId` của ROUTE phải là buổi của CHÍNH caller (mirror SessionFlagController).
        // Thiếu vế này thì `face-check` là lỗ Y HỆT đường `flags`, mà còn rộng hơn: sessionId đi vào CẢ
        // khoá S3 của ảnh live (`campaigns/{c}/sessions/{s}/face-live-*`) LẪN session_flags ⇒ thành viên
        // cùng campaign vừa cắm được cờ danh tính vào buổi người khác, vừa nhét ảnh vào thư mục buổi đó.
        // Áp cho cả `face-enroll` (dùng chung helper): ở đó sessionId hiện chỉ nằm trong route và không
        // được dùng, nên chặn sớm rẻ hơn là để một tham số không ai kiểm nằm trên đường ghi.
        private async Task<(CampaignMembership? membership, IActionResult? error)> ResolveMembershipAsync(
            Guid campaignId, Guid candidateId, Guid sessionId, CancellationToken ct)
        {
            var membership = await _db.CampaignMemberships
                .Include(m => m.Campaign)
                .FirstOrDefaultAsync(m => m.CampaignId == campaignId && m.CandidateId == candidateId
                    && m.SessionId == sessionId, ct);

            if (membership is null)
            {
                // Phân biệt campaign không tồn tại (404) vs không phải thành viên / sai buổi (403).
                // Hai ca sau CỐ Ý gộp làm 403: tách ra sẽ để lộ "buổi này có tồn tại nhưng không phải của bạn".
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

        // BK25 — ghi 1 dòng sổ cho object sinh trắc sắp đẩy lên S3 (DATA-3: có retention + purge).
        // Gọi TRƯỚC UploadAsync: bất biến của tính năng là "không object nào tồn tại mà không có dòng
        // trỏ tới" (chi tiết ở FaceImage). Ảnh THAM CHIẾU dùng key deterministic nên enroll lại cùng
        // đuôi file sẽ trùng key — khi đó chỉ dời CapturedAt (giữ đúng 1 dòng/1 object, hợp DATA-2)
        // thay vì insert dòng thứ hai và vỡ UNIQUE(storage_key).
        private async Task RecordImageAsync(
            FaceImageKind kind, string storageKey, Guid campaignId, Guid candidateId,
            Guid? sessionId, CancellationToken ct)
        {
            var existing = await _db.FaceImages.FirstOrDefaultAsync(x => x.StorageKey == storageKey, ct);
            if (existing is not null)
            {
                existing.CapturedAt = DateTime.UtcNow;   // ảnh mới đè lên cùng key → hạn giữ tính lại từ đây
                existing.SessionId = sessionId;
            }
            else
            {
                _db.FaceImages.Add(new FaceImage
                {
                    Id = Guid.NewGuid(),
                    CampaignId = campaignId,
                    CandidateId = candidateId,
                    SessionId = sessionId,
                    Kind = kind,
                    StorageKey = storageKey,
                    CapturedAt = DateTime.UtcNow
                });
            }
            await _db.SaveChangesAsync(ct);
        }

        // DATA-2 — dọn ảnh tham chiếu ĐÃ BỊ THAY THẾ. Thứ tự bắt buộc: S3 trước, dòng sổ sau (ngược
        // lại = bỏ lại ảnh khuôn mặt không ai trỏ tới = đúng con bug BK25). Best-effort: lần enroll
        // vừa rồi ĐÃ thành công và đã commit, không được để việc dọn rác làm nó trả lỗi cho ứng viên.
        private async Task DeleteSupersededReferenceAsync(string previousKey, CancellationToken ct)
        {
            try
            {
                await _file.DeleteAsync(previousKey, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "DATA-2: xoá ảnh tham chiếu cũ '{Key}' thất bại — GIỮ dòng sổ để FaceImagePurger dọn khi tới hạn",
                    previousKey);
                return;   // KHÔNG xoá dòng sổ: mất dòng = mất dấu vết object vẫn còn trong S3
            }

            await _db.FaceImages.Where(x => x.StorageKey == previousKey).ExecuteDeleteAsync(ct);
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
