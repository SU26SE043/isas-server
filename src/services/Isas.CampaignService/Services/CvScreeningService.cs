using Isas.CampaignService.DTOs;
using Isas.CampaignService.Models;
using Isas.Shared.Pagination;
using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;
using System.Globalization;

namespace Isas.CampaignService.Services
{
    /// <summary>
    /// C14 — Sàng CV B2B async (D18/D19). Publish job AI chấm khớp cho ứng viên <c>Filtered</c>,
    /// nhận callback ghi điểm/tiêu chí (idempotent, chống ảo giác), shortlist + PATCH email/fullName.
    /// TÁI DÙNG <c>campaign_criteria</c> làm rubric; KHÔNG trừ credit (D19). Tách khỏi
    /// <see cref="CampaignService"/> để giữ nguyên constructor service C13.
    /// </summary>
    public class CvScreeningService : ICvScreeningService
    {
        private readonly CampaignDbContext _db;
        private readonly ICvScreeningPublisher _publisher;
        private readonly IConfiguration _config;
        private readonly ILogger<CvScreeningService> _logger;

        public CvScreeningService(
            CampaignDbContext db,
            ICvScreeningPublisher publisher,
            IConfiguration config,
            ILogger<CvScreeningService> logger)
        {
            _db = db;
            _publisher = publisher;
            _config = config;
            _logger = logger;
        }

        // ── Publish job sàng cho các ứng viên Filtered → Analyzing ──────────────────────────
        // Best-effort per-candidate: publish hụt → giữ Filtered (last_screening_published_at=null) →
        // C15 StuckScreeningRepublisher đẩy lại. TÁI DÙNG campaign_criteria làm rubric gửi kèm job.
        public async Task<int> PublishScreeningJobsAsync(Guid orgId, Guid campaignId, CancellationToken ct)
        {
            var campaign = await _db.Campaigns
                .Include(c => c.Criteria)
                .FirstOrDefaultAsync(c => c.Id == campaignId && c.OrgId == orgId, ct)
                ?? throw new KeyNotFoundException($"Campaign {campaignId} not found.");

            var candidates = await _db.CvSubmissions
                .Where(c => c.CampaignId == campaignId && c.Status == CvSubmissionStatus.Filtered)
                .ToListAsync(ct);

            if (candidates.Count == 0)
                return 0;

            var criteria = campaign.Criteria
                .OrderBy(c => c.OrderNo)
                .Select(c => new CvScreeningCriterion(c.Id, c.Name, c.Description, c.MaxScore))
                .ToList();

            // callbackBase đi kèm job vì worker mặc định trỏ Interview — B2B phải trỏ CampaignService (ai.md).
            var callbackBase = _config["Internal:CallbackBase"] ?? "http://localhost:8080";
            var now = DateTime.UtcNow;
            int published = 0;

            foreach (var cand in candidates)
            {
                try
                {
                    await _publisher.PublishAsync(new CvScreeningJob(
                        cand.Id,
                        cand.CvParsedText ?? string.Empty,
                        campaign.Domain,
                        campaign.JDText,
                        criteria,
                        callbackBase), ct);

                    cand.Status = CvSubmissionStatus.Analyzing;
                    cand.LastScreeningPublishedAt = now;
                    cand.UpdatedAt = now;
                    published++;
                }
                catch (Exception ex)
                {
                    // Giữ Filtered để C15 republisher đẩy lại — KHÔNG chặn cả batch vì 1 CV publish hụt.
                    _logger.LogError(ex, "Publish cv_screening_queue thất bại cho candidate {CandidateId}", cand.Id);
                }
            }

            if (published > 0)
                await _db.SaveChangesAsync(ct);

            return published;
        }

        // ── BK30: HR đẩy lại sàng CV cho MỘT ứng viên ───────────────────────────────────────
        // Trước BK30 không có đường nào: PublishScreeningJobsAsync lọc cứng `Filtered`, còn
        // StuckScreeningRepublisher chỉ quét `Filtered`/`Analyzing` — nên ứng viên đã `Analyzed`
        // (thiếu full_name, thiếu điểm) hay `AnalysisFailed` (quá trần bỏ cuộc) là điểm CHẾT, phải
        // sửa tay trong DB. Chính StuckScreeningRepublisher tự ghi chú lỗ này.
        //
        // Đây CỐ Ý là đường riêng, không phải nới điều kiện của sweeper: tự động đẩy lại phải khác
        // với HR bấm tay. Sweeper vẫn không bao giờ chạm `Analyzed`.
        public async Task RescreenCandidateAsync(Guid orgId, Guid campaignId, Guid candidateId, CancellationToken ct)
        {
            var campaign = await _db.Campaigns
                .Include(c => c.Criteria)
                .FirstOrDefaultAsync(c => c.Id == campaignId && c.OrgId == orgId, ct)
                ?? throw new KeyNotFoundException($"Campaign {campaignId} not found.");

            var candidate = await _db.CvSubmissions
                .FirstOrDefaultAsync(c => c.Id == candidateId && c.CampaignId == campaignId, ct)
                ?? throw new KeyNotFoundException($"Candidate {candidateId} not found.");

            // `Invited` bị chặn vì SaveCvResultAsync bỏ qua nó (không lật kết quả đã chốt) ⇒ chạy tiếp
            // chỉ tổ đốt token Gemini rồi vứt kết quả.
            // `Analyzing` bị chặn vì job đang bay — và đây cũng chính là cooldown miễn phí chống bấm
            // liên tục: rescreen đặt trạng thái về Analyzing ngay, nên lần bấm kế bị từ chối.
            if (candidate.Status is not (CvSubmissionStatus.Filtered
                or CvSubmissionStatus.Analyzed
                or CvSubmissionStatus.AnalysisFailed))
            {
                throw new InvalidOperationException(
                    $"Chỉ đẩy lại được ứng viên Filtered/Analyzed/AnalysisFailed (hiện: {candidate.Status}).");
            }

            if (string.IsNullOrWhiteSpace(candidate.CvParsedText))
                throw new InvalidOperationException("CV không có nội dung đọc được — upload lại thay vì đẩy lại.");

            var criteria = campaign.Criteria
                .OrderBy(c => c.OrderNo)
                .Select(c => new CvScreeningCriterion(c.Id, c.Name, c.Description, c.MaxScore))
                .ToList();

            var callbackBase = _config["Internal:CallbackBase"] ?? "http://localhost:8080";

            // Publish TRƯỚC rồi mới đổi trạng thái: publish ném thì ứng viên giữ nguyên trạng thái cũ,
            // HR bấm lại được. Đổi trạng thái trước rồi publish hụt sẽ đẩy ứng viên vào `Analyzing`
            // mồ côi và phải chờ hết 15' của sweeper.
            await _publisher.PublishAsync(new CvScreeningJob(
                candidate.Id,
                candidate.CvParsedText!,
                campaign.Domain,
                campaign.JDText,
                criteria,
                callbackBase), ct);

            var now = DateTime.UtcNow;
            candidate.Status = CvSubmissionStatus.Analyzing;
            candidate.LastScreeningPublishedAt = now;
            candidate.UpdatedAt = now;
            await _db.SaveChangesAsync(ct);

            _logger.LogInformation(
                "BK30 — HR đẩy lại sàng CV cho candidate {CandidateId} (campaign {CampaignId}).",
                candidateId, campaignId);
        }

        // ── Callback cv-result → ghi điểm + Analyzed (idempotent, chống ảo giác, recover ngoài thứ tự) ──
        public async Task<CvResultOutcome> SaveCvResultAsync(Guid candidateId, CvResultCallbackRequest req, CancellationToken ct)
        {
            var candidate = await _db.CvSubmissions
                .FirstOrDefaultAsync(c => c.Id == candidateId, ct)
                ?? throw new KeyNotFoundException($"Candidate {candidateId} not found.");

            // Callback MUỘN sau khi HR đã mời (Invited) → bỏ qua (không lật kết quả đã chốt).
            if (candidate.Status == CvSubmissionStatus.Invited)
            {
                _logger.LogInformation("cv-result cho candidate {CandidateId} bị bỏ qua: đã Invited.", candidateId);
                return CvResultOutcome.SkippedInvited;
            }

            // Chống ảo giác: chỉ nhận criterion_id có THẬT trong campaign_criteria của campaign này.
            var criteria = await _db.CampaignCriteria
                .Where(c => c.CampaignId == candidate.CampaignId)
                .ToDictionaryAsync(c => c.Id, ct);

            // Idempotent: xoá điểm cũ rồi ghi lại → callback 2 lần KHÔNG nhân đôi (EF xếp DELETE trước
            // INSERT trong 1 SaveChanges dù trùng UNIQUE(candidate_id, criterion_id) — như replace-all C12).
            var existingScores = await _db.CandidateCriterionScores
                .Where(s => s.CvSubmissionId == candidateId)
                .ToListAsync(ct);
            _db.CandidateCriterionScores.RemoveRange(existingScores);

            var now = DateTime.UtcNow;
            foreach (var m in req.CriterionMatches ?? new List<CriterionMatchItem>())
            {
                if (!criteria.TryGetValue(m.CriterionId, out var crit))
                    continue;   // bỏ criterion_id AI bịa (FK Restrict cũng chặn, lọc sớm cho sạch)

                _db.CandidateCriterionScores.Add(new CandidateCriterionScore
                {
                    Id = Guid.NewGuid(),
                    CvSubmissionId = candidateId,
                    CriterionId = m.CriterionId,
                    MatchScore = Math.Clamp(m.MatchScore, 0m, crit.MaxScore),   // kẹp [0, max_score] (INT-9)
                    Reasoning = m.Reasoning,
                    CreatedAt = now
                });
            }

            // BK28 — AI CHỈ ĐIỀN CHỖ TRỐNG, KHÔNG BAO GIỜ ghi đè người (`??=`, cùng ngữ nghĩa
            // ParticipationService.ApplyInvitationLink). `StuckScreeningRepublisher` đẩy lại job cho
            // ứng viên kẹt `Analyzing` nên cv-result tới NHIỀU LẦN — gán thẳng `=` sẽ xoá đúng cái
            // tên HR vừa sửa tay qua PATCH ở lần callback kế tiếp.
            // Cắt 255 vì `cv_submission.full_name` là varchar(255): tràn → Postgres ném lúc
            // SaveChanges → callback 500 → worker nack → vòng republish. Không chỉ trông vào guard
            // phía Python (mẫu 2 lớp của `Math.Clamp` bên trên) — callback là endpoint mở với
            // X-Internal-Token, không phải chỉ worker của mình mới gọi được.
            var aiFullName = req.FullName?.Trim();
            if (!string.IsNullOrEmpty(aiFullName))
                candidate.FullName ??= aiFullName.Length > 255 ? aiFullName[..255] : aiFullName;

            candidate.Skills = req.Skills;
            candidate.YearsExperience = req.YearsExperience;
            candidate.Summary = req.Summary;
            candidate.OverallMatchScore = Math.Clamp(req.OverallMatchScore, 0, 100);
            candidate.RejectReason = null;   // xoá lý do AnalysisFailed cũ khi recover (retry thành công)
            candidate.Status = CvSubmissionStatus.Analyzed;   // recover cả từ Analyzing lẫn AnalysisFailed (doc)
            candidate.UpdatedAt = now;

            await _db.SaveChangesAsync(ct);
            return CvResultOutcome.Analyzed;
        }

        // ── Callback cv-failed → AnalysisFailed (absorbing: đã Analyzed/Invited → no-op) ────────────
        public async Task<CvFailedOutcome> MarkCvFailedAsync(Guid candidateId, string? reason, CancellationToken ct)
        {
            var candidate = await _db.CvSubmissions
                .FirstOrDefaultAsync(c => c.Id == candidateId, ct)
                ?? throw new KeyNotFoundException($"Candidate {candidateId} not found.");

            // Đã Invited → bỏ qua (không lật trạng thái sau khi mời).
            if (candidate.Status == CvSubmissionStatus.Invited)
                return CvFailedOutcome.SkippedInvited;

            // Đã Analyzed (kết quả tốt về trước) → KHÔNG để cv-failed muộn xoá kết quả.
            if (candidate.Status == CvSubmissionStatus.Analyzed)
                return CvFailedOutcome.SkippedAnalyzed;

            candidate.Status = CvSubmissionStatus.AnalysisFailed;
            candidate.RejectReason = string.IsNullOrWhiteSpace(reason) ? "Phân tích CV thất bại." : reason.Trim();
            candidate.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
            return CvFailedOutcome.Failed;
        }

        /// <summary>
        /// Shortlist ứng viên sàng CV — màn HR dùng nhiều nhất. Mặc định <c>sort=score</c> DESC theo
        /// <c>overall_match_score</c> (ranking), hoặc <c>sort=name</c>. Lọc <c>status</c>/<c>minScore</c>/
        /// <c>search</c> (tên HOẶC email, case-insensitive) — TẤT CẢ đẩy xuống SQL; sort cũng đẩy xuống SQL
        /// (trước đây `ToListAsync()` nạp TOÀN BỘ bảng rồi mới lọc/sắp trong C#, nên thêm filter làm
        /// response nhỏ đi mà query không hề rẻ hơn, và `max_candidates` là `int?` = có thể KHÔNG có trần).
        /// Keyset-paged theo convention DB8; xem <c>skill</c> bên dưới để biết ngoại lệ quan trọng.
        /// <para>
        /// ⚠ <b><c>skill</c> lọc SAU khi phân trang.</b> <c>Skills</c> là jsonb <c>string[]</c>, không có
        /// cách push xuống SQL portable cho cả Npgsql lẫn SQLite ⇒ vẫn lọc trong C# trên đúng trang vừa
        /// đọc. Hệ quả PHẢI biết khi gọi API: một trang có thể trả **ít hơn <c>limit</c>, thậm chí rỗng,
        /// mà VẪN còn trang sau** ⇒ client phải đi theo <c>X-Next-Cursor</c> cho tới khi header vắng mặt,
        /// KHÔNG được dừng khi thấy trang ngắn. Cursor luôn neo vào dòng cuối của trang DB (trước khi lọc
        /// skill) nên không sót/không trùng dòng nào.
        /// </para>
        /// </summary>
        public async Task<KeysetPage<CandidateListItem>> GetCandidatesAsync(
            Guid orgId, Guid campaignId, string? status, int? minScore, string? skill, string? sort,
            string? search, string? cursor, int? limit, CancellationToken ct)
        {
            // Ownership: campaign phải của org (query filter loại soft-deleted) → không thấy = 404.
            var owns = await _db.Campaigns.AnyAsync(c => c.Id == campaignId && c.OrgId == orgId, ct);
            if (!owns)
                throw new KeyNotFoundException($"Campaign {campaignId} not found.");

            var take = KeysetPaging.ClampLimit(limit);
            var cur = SortKeysetCursor.Decode(cursor);
            var normalizedSort = string.IsNullOrWhiteSpace(sort) ? "score" : sort.Trim().ToLowerInvariant();

            var q = _db.CvSubmissions.Where(c => c.CampaignId == campaignId);

            if (!string.IsNullOrWhiteSpace(status) &&
                Enum.TryParse<CvSubmissionStatus>(status, ignoreCase: true, out var st))
                q = q.Where(c => c.Status == st);

            if (minScore is int min)
                q = q.Where(c => c.OverallMatchScore != null && c.OverallMatchScore >= min);

            // search: khớp tên HOẶC email. `.ToLower()` ở CẢ hai vế → dịch được trên Npgsql lẫn SQLite và
            // cho kết quả xác định, không phụ thuộc collation mặc định của từng provider.
            if (!string.IsNullOrWhiteSpace(search))
            {
                var needle = search.Trim().ToLowerInvariant();
                q = q.Where(c => (c.FullName != null && c.FullName.ToLower().Contains(needle))
                              || (c.Email != null && c.Email.ToLower().Contains(needle)));
            }

            List<CvSubmission> rows;
            string nextKey;

            if (normalizedSort == "name")
            {
                // Khoá keyset = lower(coalesce(full_name,'')) ASC, id ASC. COALESCE để khoá KHÔNG BAO GIỜ
                // NULL (hợp đồng SortKeysetCursor) — NULL trong predicate keyset cho UNKNOWN và loại nhầm cả trang.
                if (cur is not null)
                    q = q.Where(c => string.Compare((c.FullName ?? string.Empty).ToLower(), cur.Key) > 0
                        || ((c.FullName ?? string.Empty).ToLower() == cur.Key && c.Id.CompareTo(cur.Id) > 0));

                rows = await q
                    .OrderBy(c => (c.FullName ?? string.Empty).ToLower())
                    .ThenBy(c => c.Id)
                    .Take(take)
                    .ToListAsync(ct);

                nextKey = rows.Count > 0 ? (rows[^1].FullName ?? string.Empty).ToLowerInvariant() : string.Empty;
            }
            else
            {
                // Khoá keyset = coalesce(overall_match_score, -1) DESC, id DESC. Điểm ∈ [0,100] nên -1 nằm
                // dưới mọi điểm thật ⇒ ứng viên chưa Analyzed xuống cuối, ĐÚNG hành vi cũ (`?? int.MinValue`)
                // mà không phải mượn cú pháp NULLS LAST (Postgres mặc định NULLS FIRST khi DESC, SQLite thì khác).
                var curScore = cur?.KeyAsInt();
                if (cur is not null && curScore is int cs)
                    q = ApplyScoreKeyset(q, cs, cur.Id);

                rows = await ApplyScoreOrder(q)
                    .Take(take)
                    .ToListAsync(ct);

                nextKey = rows.Count > 0
                    ? (rows[^1].OverallMatchScore ?? -1).ToString(CultureInfo.InvariantCulture)
                    : string.Empty;
            }

            // Cursor neo vào dòng cuối của TRANG DB — phải tính TRƯỚC khi lọc skill, nếu không sẽ nhảy cóc
            // qua những dòng bị skill loại và mất dữ liệu ở trang sau.
            var next = rows.Count == take
                ? new SortKeysetCursor(nextKey, rows[^1].Id).Encode()
                : null;

            // skill: Skills là jsonb string[] → lọc trong C# (không query trong JSON — portable Npgsql/SQLite).
            // Xem cảnh báo ở XML doc: đây là lý do trang có thể ngắn hơn limit mà vẫn còn trang sau.
            var page = rows.AsEnumerable();
            if (!string.IsNullOrWhiteSpace(skill))
            {
                var needle = skill.Trim();
                page = page.Where(c => c.Skills != null &&
                    c.Skills.Any(s => s.Contains(needle, StringComparison.OrdinalIgnoreCase)));
            }

            var items = page.Select(c => new CandidateListItem
            {
                Id = c.Id,
                FullName = c.FullName,
                Email = c.Email,
                Status = c.Status.ToString(),
                OverallMatchScore = c.OverallMatchScore,
                Skills = c.Skills
            }).ToList();

            return new KeysetPage<CandidateListItem>(items, next);
        }

        /// <summary>
        /// Sắp xếp shortlist theo điểm: <c>COALESCE(overall_match_score, -1) DESC, id DESC</c>.
        /// <para>
        /// ⚠ <c>COALESCE</c> ở đây KHÔNG phải trang trí. Postgres coi NULL là LỚN NHẤT nên
        /// <c>ORDER BY score DESC</c> đẩy ứng viên chưa chấm lên ĐẦU shortlist; SQLite thì ngược lại
        /// (NULL nhỏ nhất → xuống cuối). Nghĩa là bỏ <c>COALESCE</c> đi thì test SQLite VẪN XANH mà
        /// production Postgres hiển thị sai hoàn toàn. Ép về -1 (dưới mọi điểm thật 0..100) làm thứ tự
        /// giống nhau trên cả hai provider — và đó là lý do hàm này tách riêng: test
        /// <c>ListQueryTranslationTests</c> soi SQL Npgsql sinh từ CHÍNH hàm này, nên mọi thay đổi ở
        /// đây đều bị bắt.
        /// </para>
        /// </summary>
        public static IOrderedQueryable<CvSubmission> ApplyScoreOrder(IQueryable<CvSubmission> q) =>
            q.OrderByDescending(c => c.OverallMatchScore ?? -1)
             .ThenByDescending(c => c.Id);

        /// <summary>
        /// Predicate keyset cho ordering ở <see cref="ApplyScoreOrder"/> — PHẢI dùng đúng biểu thức khoá
        /// (<c>COALESCE(score,-1)</c>) như ORDER BY, lệch một chút là phân trang trượt dòng.
        /// </summary>
        public static IQueryable<CvSubmission> ApplyScoreKeyset(
            IQueryable<CvSubmission> q, int curScore, Guid curId) =>
            q.Where(c => (c.OverallMatchScore ?? -1) < curScore
                || ((c.OverallMatchScore ?? -1) == curScore && c.Id.CompareTo(curId) < 0));

        // ── Chi tiết 1 ứng viên + điểm từng tiêu chí (reasoning) ─────────────────────────────────────
        public async Task<CandidateDetailResponse> GetCandidateAsync(
            Guid orgId, Guid campaignId, Guid candidateId, CancellationToken ct)
        {
            var candidate = await _db.CvSubmissions
                .FirstOrDefaultAsync(
                    c => c.Id == candidateId && c.CampaignId == campaignId && c.Campaign.OrgId == orgId, ct)
                ?? throw new KeyNotFoundException($"Candidate {candidateId} not found.");

            var scores = await (from s in _db.CandidateCriterionScores
                                join cr in _db.CampaignCriteria on s.CriterionId equals cr.Id
                                where s.CvSubmissionId == candidateId
                                orderby cr.OrderNo
                                select new CriterionScoreItem
                                {
                                    CriterionId = s.CriterionId,
                                    CriterionName = cr.Name,
                                    MatchScore = s.MatchScore,
                                    MaxScore = cr.MaxScore,
                                    Reasoning = s.Reasoning
                                }).ToListAsync(ct);

            return new CandidateDetailResponse
            {
                Id = candidate.Id,
                FullName = candidate.FullName,
                Email = candidate.Email,
                Status = candidate.Status.ToString(),
                OverallMatchScore = candidate.OverallMatchScore,
                Skills = candidate.Skills,
                YearsExperience = candidate.YearsExperience,
                Summary = candidate.Summary,
                RejectReason = candidate.RejectReason,
                CvFileUrl = candidate.CvFileUrl,
                CriterionScores = scores
            };
        }

        // ── PATCH email/fullName (parse thiếu) → audit_logs; đã Invited → 409; trùng email → 400 ──────
        public async Task PatchCandidateAsync(
            Guid orgId, Guid actorUserId, Guid campaignId, Guid candidateId, PatchCandidateRequest req, CancellationToken ct)
        {
            var candidate = await _db.CvSubmissions
                .FirstOrDefaultAsync(
                    c => c.Id == candidateId && c.CampaignId == campaignId && c.Campaign.OrgId == orgId, ct)
                ?? throw new KeyNotFoundException($"Candidate {candidateId} not found.");

            // Đã phát magic-link (email đã dùng) → khoá sửa → 409.
            if (candidate.Status == CvSubmissionStatus.Invited)
                throw new InvalidOperationException("Không sửa được ứng viên đã Invited.");

            var changed = new List<string>();

            if (req.Email is not null)
            {
                var email = req.Email.Trim();
                if (email.Length == 0)
                    throw new ArgumentException("email không được rỗng.");
                if (!new EmailAddressAttribute().IsValid(email))
                    throw new ArgumentException("Định dạng email không hợp lệ.");
                email = email.ToLowerInvariant();   // dedup/lưu chuẩn hoá lowercase (như C13)

                // UNIQUE(campaign_id, email): trùng ứng viên KHÁC trong campaign → 400 (chặn trước DB).
                var dup = await _db.CvSubmissions.AnyAsync(
                    c => c.CampaignId == campaignId && c.Id != candidateId && c.Email == email, ct);
                if (dup)
                    throw new ArgumentException($"Email '{email}' đã tồn tại trong campaign.");

                candidate.Email = email;
                changed.Add("email");
            }

            if (req.FullName is not null)
            {
                candidate.FullName = string.IsNullOrWhiteSpace(req.FullName) ? null : req.FullName.Trim();
                changed.Add("fullName");
            }

            if (changed.Count == 0)
                throw new ArgumentException("Cần ít nhất 1 trường (email/fullName) để cập nhật.");

            candidate.UpdatedAt = DateTime.UtcNow;
            _db.AuditLogs.Add(new AuditLog
            {
                Id = Guid.NewGuid(),
                OrgId = orgId,                  // BK4: ORG sở hữu campaign (ownership context)
                ActorUserId = actorUserId,      // cá nhân HR thao tác (user sub, không phải org)
                Action = AuditAction.EditCandidate,
                Entity = "CvSubmission",
                EntityId = candidateId,
                Summary = $"Sửa {string.Join("/", changed)} ứng viên sàng CV",
                At = DateTime.UtcNow
            });
            await _db.SaveChangesAsync(ct);
        }
    }
}
