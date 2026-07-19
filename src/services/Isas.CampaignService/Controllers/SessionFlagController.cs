using System.Security.Claims;
using Isas.CampaignService.DTOs;
using Isas.CampaignService.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Isas.CampaignService.Controllers
{
    /// <summary>
    /// SEC-1 ingest — NHẬN + LƯU cờ chống gian lận cho HR (D13/CAMP-12: FLAG cho HR, KHÔNG auto-hủy).
    /// Backend KHÔNG tự phát hiện gian lận — nguồn phát là FE (webcam/tab-switch, repo riêng) và AIService
    /// (face-match/multi-voice, service riêng). 2 đường, đều idempotent → 204:
    ///  1) Cờ FE/ứng viên (tab_switch/paste/focus_lost): JWT Candidate + phải là thành viên campaign.
    ///  2) Cờ AIService (face_mismatch/no_face/multiple_faces/multi_voice/identity_unverified): X-Internal-Token
    ///     (KHÔNG qua gateway — GEN-1), mirror InternalCampaignCandidatesController.
    /// Chỉ lưu khi campaign bật anti_cheat_enabled (hoặc face_verify_enabled cho tín hiệu danh tính) — else 204 no-op.
    /// Dùng CampaignDbContext trực tiếp (không service DI riêng) → Program.cs không đổi.
    /// </summary>
    [ApiController]
    public class SessionFlagController : ControllerBase
    {
        private readonly CampaignDbContext _db;
        private readonly IConfiguration _config;
        private readonly ILogger<SessionFlagController> _logger;

        public SessionFlagController(
            CampaignDbContext db, IConfiguration config, ILogger<SessionFlagController> logger)
        {
            _db = db;
            _config = config;
            _logger = logger;
        }

        // Cờ do FE phát (ứng viên đang làm bài). Danh tính KHÔNG thuộc nhóm FE → không nhận qua đường này.
        // F4 — `camera_blocked`: OS/trình duyệt từ chối quyền camera. Trước F4, FE nuốt lỗi này và ứng viên
        // thi tiếp KHÔNG bị giám sát mặt mà KHÔNG có cờ nào ⇒ HR không phân biệt được "sạch" với "camera
        // chưa từng bật". Đây là cờ MÔI TRƯỜNG (thiết bị), KHÔNG phải tín hiệu danh tính → CỐ Ý không thêm
        // vào IdentitySignals: làm vậy sẽ đổi điều kiện lưu (lưu cả khi chỉ bật face_verify_enabled).
        private static readonly HashSet<string> FeSignals = new(StringComparer.OrdinalIgnoreCase)
            { "tab_switch", "paste", "focus_lost", "camera_blocked" };

        // Cờ do AIService phát (giám sát khuôn mặt/giọng nói).
        private static readonly HashSet<string> AiSignals = new(StringComparer.OrdinalIgnoreCase)
            { "face_mismatch", "no_face", "multiple_faces", "multi_voice", "identity_unverified" };

        // Tín hiệu DANH TÍNH (face-verify) — được lưu khi anti_cheat_enabled HOẶC face_verify_enabled bật.
        private static readonly HashSet<string> IdentitySignals = new(StringComparer.OrdinalIgnoreCase)
            { "face_mismatch", "no_face", "multiple_faces", "identity_unverified" };

        private Guid? GetCandidateId()
            => Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub"), out var g)
                ? g : (Guid?)null;

        // ── Đường 1: cờ FE/ứng viên ────────────────────────────────────────────────
        // POST /campaign/{campaignId}/sessions/{sessionId}/flags  (qua gateway /api/v1/...)
        [HttpPost("campaign/{campaignId:guid}/sessions/{sessionId:guid}/flags")]
        [Authorize(Roles = "Candidate")]
        public async Task<IActionResult> ReportCandidateFlag(
            Guid campaignId, Guid sessionId, [FromBody] CandidateFlagRequest req, CancellationToken ct)
        {
            var candidateId = GetCandidateId();
            if (candidateId is null) return Unauthorized();

            var campaign = await _db.Campaigns.FirstOrDefaultAsync(c => c.Id == campaignId, ct);
            if (campaign is null) return NotFound(new { error = "Campaign not found." });

            // Ownership: caller phải là thành viên campaign (CampaignMembership theo candidateId, DB16) → ngoài = 403.
            var isMember = await _db.CampaignMemberships
                .AnyAsync(m => m.CampaignId == campaignId && m.CandidateId == candidateId, ct);
            if (!isMember) return Forbid();

            if (!FeSignals.Contains(req.SignalType?.Trim() ?? string.Empty))
                return BadRequest(new { error = $"Unknown signal_type '{req.SignalType}'." });

            await RecordFlagAsync(campaign, sessionId, candidateId.Value, req.SignalType!, req.Note, ct);
            return NoContent();
        }

        // ── Đường 2: cờ AIService (internal, X-Internal-Token — KHÔNG qua gateway, GEN-1) ─────
        [HttpPost("internal/session-flags")]
        [AllowAnonymous]
        public async Task<IActionResult> ReportInternalFlag(
            [FromBody] InternalFlagRequest req,
            [FromHeader(Name = "X-Internal-Token")] string? token,
            CancellationToken ct)
        {
            if (!IsValidInternalToken(token))
                return Unauthorized(new { error = "Invalid internal token" });

            var campaign = await _db.Campaigns.FirstOrDefaultAsync(c => c.Id == req.CampaignId, ct);
            if (campaign is null) return NotFound(new { error = "Campaign not found." });

            if (!AiSignals.Contains(req.SignalType?.Trim() ?? string.Empty))
                return BadRequest(new { error = $"Unknown signal_type '{req.SignalType}'." });

            await RecordFlagAsync(campaign, req.SessionId, req.CandidateId, req.SignalType!, req.Note, ct);
            return NoContent();
        }

        // Lưu 1 row session_flags NẾU campaign còn "mở cửa" cho loại tín hiệu này; ngược lại no-op (caller vẫn 204).
        // Anti-cheat tắt + không phải tín hiệu danh tính (hoặc face-verify tắt) → không lưu (giám sát tắt).
        private async Task RecordFlagAsync(
            Campaign campaign, Guid sessionId, Guid candidateId, string signalType, string? note, CancellationToken ct)
        {
            var normalized = signalType.Trim().ToLowerInvariant();
            bool identity = IdentitySignals.Contains(normalized);
            bool shouldPersist = campaign.AntiCheatEnabled || (identity && campaign.FaceVerifyEnabled);
            if (!shouldPersist)
            {
                _logger.LogDebug(
                    "Bỏ qua cờ '{Signal}' (session {SessionId}): campaign {CampaignId} tắt giám sát.",
                    normalized, sessionId, campaign.Id);
                return;   // no-op idempotent (SEC-1 toggle off)
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
            await _db.SaveChangesAsync(ct);
        }

        private bool IsValidInternalToken(string? token)
        {
            var expected = _config["Internal:Token"];
            if (string.IsNullOrEmpty(expected) || token != expected)
            {
                _logger.LogWarning("Ingest cờ chống gian lận bị từ chối: X-Internal-Token sai.");
                return false;
            }
            return true;
        }
    }
}
