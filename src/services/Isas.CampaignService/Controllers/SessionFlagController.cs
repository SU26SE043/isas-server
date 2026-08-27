using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
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
    ///  1) Cờ FE/ứng viên (tab_switch/paste/focus_lost/camera_blocked/monitoring_gap): JWT Candidate +
    ///     phải là thành viên campaign.
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
        //
        // AC1 — `monitoring_gap`: nhịp giám sát 30s bị đứt (tab ngủ / máy sleep / mạng rớt) nên KHÔNG CÓ
        // ảnh nào để so trong khoảng đó. Cùng lập luận F4 và CỐ Ý cũng KHÔNG vào IdentitySignals: nó nói
        // "không quan sát được", KHÔNG nói "sai người" — thêm vào danh tính là đổi điều kiện lưu sang cả
        // nhánh chỉ-bật-face_verify, tức bắt đầu ghi cờ ở campaign mà HR đã tắt anti-cheat.
        private static readonly HashSet<string> FeSignals = new(StringComparer.OrdinalIgnoreCase)
            { "tab_switch", "paste", "focus_lost", "camera_blocked", "monitoring_gap" };

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
            //
            // Q4 — vế `m.SessionId == sessionId` là BẮT BUỘC, không phải siết cho chặt: `sessionId` đến từ
            // ROUTE và đi thẳng vào session_flags. Chỉ kiểm "là thành viên campaign" thì MỌI thành viên
            // cắm được cờ vào buổi thi của NGƯỜI KHÁC cùng campaign (đã xảy ra trên prod: 1 buổi có cờ do
            // 2 candidate khác nhau gửi). Hại thật: `unscoredFlagged` (R7) đẩy buổi đáng ngờ lên đầu cho
            // HR ⇒ đối thủ bơm cờ là đẩy được ứng viên khác lên đầu danh sách; cột candidate_id có lưu
            // thủ phạm nhưng đường đọc gom theo session_id nên HR không phân biệt được.
            // AC1 thu hẹp một phần chứ KHÔNG đóng: thứ tự nay theo TẦNG trước, mà cờ FE chỉ chạm tầng
            // hành vi/môi trường (không bơm lên tầng danh tính được) — nhưng tổng số cờ vẫn là tie-break
            // TRONG cùng tầng, nên guard này vẫn là thứ duy nhất chặn.
            // KHÔNG chặn nhầm: membership.SessionId chỉ được ghi lúc Start (ParticipationService), mà ứng
            // viên cũng chỉ có sessionId sau khi Start trả về ⇒ không tồn tại ca "gửi cờ trước Start".
            var isOwnSession = await _db.CampaignMemberships
                .AnyAsync(m => m.CampaignId == campaignId && m.CandidateId == candidateId
                    && m.SessionId == sessionId, ct);
            if (!isOwnSession) return Forbid();

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
            // Fail-closed: token chưa cấu hình → từ chối hết (không mở toang). Loại null/rỗng trước khi so
            // khớp (FixedTimeEquals cần 2 span; guard sớm giữ nguyên hành vi cũ) — mẫu InternalCreditsController
            // (Payment, commit 0a55343).
            if (string.IsNullOrEmpty(expected) || string.IsNullOrEmpty(token))
            {
                _logger.LogWarning("Ingest cờ chống gian lận bị từ chối: X-Internal-Token sai/thiếu.");
                return false;
            }

            // So khớp HẰNG-THỜI-GIAN trên UTF-8 bytes: `token != expected` rò rỉ timing (string compare
            // thoát sớm ở byte lệch đầu tiên) → kẻ tấn công dò được từng ký tự token nội bộ. Cùng ranh giới
            // tin cậy (GEN-7, X-Internal-Token) mà Payment/Interview đã sửa — Campaign đồng bộ theo.
            var ok = CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(token), Encoding.UTF8.GetBytes(expected));
            if (!ok)
                _logger.LogWarning("Ingest cờ chống gian lận bị từ chối: X-Internal-Token sai/thiếu.");
            return ok;
        }
    }
}
