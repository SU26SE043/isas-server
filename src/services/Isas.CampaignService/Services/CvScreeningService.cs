using Isas.CampaignService.DTOs;
using Isas.CampaignService.Models;
using Isas.Shared.Pagination;
using Isas.Shared.Scoring;
using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;
using System.Globalization;

namespace Isas.CampaignService.Services
{
    /// <summary>
    /// Sàng CV B2B async (D18/D19) — vai <b>HR technical screener</b>. Publish job cho ứng viên
    /// <c>Filtered</c>, nhận callback ghi kết quả (idempotent, chống ảo giác), shortlist + PATCH
    /// email/fullName. KHÔNG trừ credit (D19).
    ///
    /// Thước đo là <c>campaigns.job_needs</c> — nhu cầu công việc suy từ JD, chốt một lần cho cả
    /// campaign. KHÔNG còn tái dùng <c>campaign_criteria</c>: đó là rubric chấm CÂU TRẢ LỜI NÓI của
    /// buổi phỏng vấn ("Giao tiếp &amp; Tiếng Anh", mức neo "1-4 điểm (Kém)…"), CV là giấy nên model
    /// chỉ có thể đoán — đo trên prod, hai ứng viên khác hẳn nhau đều nhận đúng 7/10 ở tiêu chí đó.
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

        /// <summary>
        /// Bộ nhu cầu công việc gửi kèm job. Rỗng ⇒ ném: không có thước thì không đo được, và một
        /// ứng viên "đã sàng" mà không đối chiếu với gì là kết quả sai nhìn như đúng.
        /// </summary>
        private static List<CvScreeningNeed> RequireJobNeeds(Campaign campaign)
        {
            var needs = (campaign.JobNeeds ?? new List<JobNeed>())
                .Where(n => !string.IsNullOrWhiteSpace(n.Text))
                .Select(n => new CvScreeningNeed(n.NeedId, n.Category, n.Text))
                .ToList();

            if (needs.Count == 0)
                throw new InvalidOperationException(
                    "Campaign chưa chốt nhu cầu công việc (job needs) — không sàng CV được. "
                    + "Publish lại campaign hoặc khai nhu cầu trước.");

            return needs;
        }

        // ── Publish job sàng cho các ứng viên Filtered → Analyzing ──────────────────────────
        // Best-effort per-candidate: publish hụt → giữ Filtered (last_screening_published_at=null) →
        // C15 StuckScreeningRepublisher đẩy lại.
        public async Task<int> PublishScreeningJobsAsync(Guid orgId, Guid campaignId, CancellationToken ct)
        {
            var campaign = await _db.Campaigns
                .FirstOrDefaultAsync(c => c.Id == campaignId && c.OrgId == orgId, ct)
                ?? throw new KeyNotFoundException($"Campaign {campaignId} not found.");

            var candidates = await _db.CvSubmissions
                .Where(c => c.CampaignId == campaignId && c.Status == CvSubmissionStatus.Filtered)
                .ToListAsync(ct);

            if (candidates.Count == 0)
                return 0;

            var jobNeeds = RequireJobNeeds(campaign);

            // callbackBase đi kèm job vì worker mặc định trỏ Interview — B2B phải trỏ CampaignService (ai.md).
            var callbackBase = _config["Internal:CallbackBase"] ?? "http://localhost:8080";
            var now = DateTime.UtcNow;
            int published = 0;

            // SCP1 · B5 — GHIM chính sách chấm CV cho LẦN ĐÁNH GIÁ này, TẠI ĐÂY (lúc đẩy job), không
            // lúc upload. `??=` : chỉ set khi chưa có ⇒ chạy lại hàm này hoặc republisher đẩy lại
            // KHÔNG đổi pin (retry = cùng một lần đánh giá). HR bấm rescreen mới re-pin
            // (RescreenCandidateAsync). null (campaign chưa áp chính sách CV) ⇒ chấm mặc định.
            foreach (var cand in candidates)
                cand.ScoringPolicyVersion ??= campaign.CvPolicyVersion;

            foreach (var cand in candidates)
            {
                try
                {
                    await _publisher.PublishAsync(new CvScreeningJob(
                        cand.Id,
                        cand.CvParsedText ?? string.Empty,
                        campaign.Domain,
                        jobNeeds,
                        campaign.Language,
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

            // LUÔN lưu (không chỉ khi published > 0): pin ScoringPolicyVersion đã set ở trên phải bền
            // vững kể cả khi publish HỤT cho mọi CV — republisher đẩy lại sau đó GIỮ pin này (retry).
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

            var jobNeeds = RequireJobNeeds(campaign);
            var callbackBase = _config["Internal:CallbackBase"] ?? "http://localhost:8080";

            // Publish TRƯỚC rồi mới đổi trạng thái: publish ném thì ứng viên giữ nguyên trạng thái cũ,
            // HR bấm lại được. Đổi trạng thái trước rồi publish hụt sẽ đẩy ứng viên vào `Analyzing`
            // mồ côi và phải chờ hết 15' của sweeper.
            await _publisher.PublishAsync(new CvScreeningJob(
                candidate.Id,
                candidate.CvParsedText!,
                campaign.Domain,
                jobNeeds,
                campaign.Language,
                callbackBase), ct);

            var now = DateTime.UtcNow;
            candidate.Status = CvSubmissionStatus.Analyzing;
            candidate.LastScreeningPublishedAt = now;
            // SCP1 · B5 — HR bấm rescreen = LẦN ĐÁNH GIÁ MỚI ⇒ RE-PIN theo chính sách chấm CV HIỆN
            // HÀNH (kể cả về null nếu chính sách đã bị gỡ). Khác retry của republisher (giữ pin cũ).
            candidate.ScoringPolicyVersion = campaign.CvPolicyVersion;
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

            // Chống ảo giác: chỉ nhận needId có THẬT trong bộ nhu cầu của campaign này.
            var campaignNeeds = await _db.Campaigns
                .Where(c => c.Id == candidate.CampaignId)
                .Select(c => c.JobNeeds)
                .FirstOrDefaultAsync(ct) ?? new List<JobNeed>();
            var allowed = campaignNeeds
                .Where(n => !string.IsNullOrWhiteSpace(n.NeedId))
                .ToDictionary(n => n.NeedId, n => n);

            var assessments = new List<NeedAssessment>();
            var seen = new HashSet<string>();
            foreach (var a in req.Assessments ?? new List<NeedAssessmentItem>())
            {
                var needId = a.NeedId?.Trim();
                if (string.IsNullOrEmpty(needId) || !allowed.TryGetValue(needId, out var need) || !seen.Add(needId))
                    continue;   // id BỊA hoặc trùng → bỏ (AI-3)

                // Mức lạ ⇒ Weak, KHÔNG phải Partial: mặc định an toàn ở đây là "chưa chứng minh
                // được", vì mọi hướng khác đều cho không ứng viên một phần điểm mà không ai đọc
                // được bằng chứng nào. Hai lớp guard (Python + đây) là cố ý — callback là endpoint
                // mở với X-Internal-Token, không phải chỉ worker của mình mới gọi được.
                var level = NeedLevels.IsValid(a.Level) ? a.Level! : NeedLevels.Weak;
                var evidence = a.Evidence?.Trim();
                if (string.IsNullOrEmpty(evidence))
                {
                    // Không trích được gì trong CV chính là "không thấy bằng chứng" — hạ về Weak
                    // thay vì để một mức cao không ai kiểm chứng được.
                    level = NeedLevels.Weak;
                    evidence = NeedEvidence.NotFound;
                }

                assessments.Add(new NeedAssessment
                {
                    NeedId = needId,
                    Area = string.IsNullOrWhiteSpace(a.Area) ? need.Text : a.Area!.Trim(),
                    Level = level,
                    Evidence = evidence,
                });
            }

            var now = DateTime.UtcNow;

            // ⚠ Điểm xếp hạng TÍNH TỪ BẰNG CHỨNG, KHÔNG nhận số nào của AI.
            // Đo trên prod trước bản này: bốn CV có bằng chứng GIỐNG HỆT nhau nhận điểm tổng
            // 70/70/55/55, và ứng viên yếu hơn xếp trên ứng viên mạnh hơn — số holistic do model
            // phán mâu thuẫn với chính bằng chứng nó vừa liệt kê. Cùng bộ level ⇒ cùng điểm là
            // tính chất bắt buộc, có test khoá.
            // Trung bình ĐỀU giữa các nhu cầu (không đặt trọng số giữa 4 nhóm): không có dữ liệu
            // nào nói technical đáng gấp mấy lần communication, mà bịa hằng số rồi trưng ra như
            // chuẩn ngành đúng thứ F14 đã từ chối làm. HR đọc breakdown 4 nhóm để tự nặng nhẹ.
            int? defaultScore = assessments.Count == 0
                ? null
                : (int)Math.Round(
                    100m * assessments.Sum(a => NeedLevels.Credit(a.Level)) / assessments.Count,
                    MidpointRounding.AwayFromZero);

            // SCP1 · B7 — nếu LẦN ĐÁNH GIÁ này đã ghim chính sách (cv_submission.scoring_policy_version,
            // B5) → điểm = đánh giá biểu thức ĐÃ GHIM. Đọc đúng bản đã ghim, KHÔNG con trỏ hiện hành
            // (campaigns.cv_policy_version) — HR đổi policy giữa chừng KHÔNG hồi tố ứng viên đã sàng.
            var (jobFitScore, scoreFallback) = await ResolvePolicyScoreAsync(
                candidate, campaignNeeds, assessments, defaultScore, ct);

            candidate.Strengths = assessments.Where(a => a.Level != NeedLevels.Weak).ToList();
            candidate.Gaps = assessments.Where(a => a.Level == NeedLevels.Weak).ToList();
            candidate.BonusSignals = req.BonusSignals;
            candidate.VerifyQuestions = req.VerifyQuestions?.Take(3).ToList();
            // Cờ cho HR, CỐ Ý không nhập vào điểm: gộp hai thứ khác bản chất vào một con số là lặp
            // lại đúng sai lầm bản này đang sửa — sau đó không ai giải thích được con số nữa.
            candidate.VerificationRisk = VerificationRisks.IsValid(req.VerificationRisk)
                ? req.VerificationRisk
                : VerificationRisks.Medium;   // không đọc được ⇒ "chưa rõ", không phải "yên tâm"
            candidate.FitSummary = req.FitSummary;
            candidate.ScreeningVersion = ScreeningVersions.JobFitFromEvidence;

            // BK28 — AI CHỈ ĐIỀN CHỖ TRỐNG, KHÔNG BAO GIỜ ghi đè người (`??=`, cùng ngữ nghĩa
            // ParticipationService.ApplyInvitationLink). `StuckScreeningRepublisher` đẩy lại job cho
            // ứng viên kẹt `Analyzing` nên cv-result tới NHIỀU LẦN — gán thẳng `=` sẽ xoá đúng cái
            // tên HR vừa sửa tay qua PATCH ở lần callback kế tiếp.
            // Cắt 255 vì `cv_submission.full_name` là varchar(255): tràn → Postgres ném lúc
            // SaveChanges → callback 500 → worker nack → vòng republish. Không chỉ trông vào guard
            // phía Python (mẫu 2 lớp của mức/bằng chứng bên trên) — callback là endpoint mở với
            // X-Internal-Token, không phải chỉ worker của mình mới gọi được.
            var aiFullName = req.FullName?.Trim();
            if (!string.IsNullOrEmpty(aiFullName))
                candidate.FullName ??= aiFullName.Length > 255 ? aiFullName[..255] : aiFullName;

            candidate.Skills = req.Skills;
            candidate.YearsExperience = req.YearsExperience;
            candidate.Summary = req.FitSummary;
            candidate.OverallMatchScore = jobFitScore;
            candidate.ScoreFallback = scoreFallback;   // SCP1 · B7 — cờ lùi an toàn (HĐ-5)
            candidate.RejectReason = null;   // xoá lý do AnalysisFailed cũ khi recover (retry thành công)
            candidate.Status = CvSubmissionStatus.Analyzed;   // recover cả từ Analyzing lẫn AnalysisFailed (doc)
            candidate.UpdatedAt = now;

            await _db.SaveChangesAsync(ct);
            return CvResultOutcome.Analyzed;
        }

        // ── SCP1 · B7 — điểm sàng CV = biểu thức chính sách ĐÃ GHIM (B5), lùi an toàn như B6 ─────────
        private async Task<(int? Score, bool Fallback)> ResolvePolicyScoreAsync(
            CvSubmission candidate, List<JobNeed> campaignNeeds, List<NeedAssessment> assessments,
            int? defaultScore, CancellationToken ct)
        {
            // (5) Chưa ghim chính sách (campaign chưa áp / sàng trước SCP1) → công thức CAMP-14 mặc định.
            if (candidate.ScoringPolicyVersion is not int pinnedVersion)
                return (defaultScore, false);

            // Đọc biểu thức của ĐÚNG bản đã ghim. Campaign SỞ HỮU bảng, dòng BẤT BIẾN (B2) ⇒ (campaign,
            // CvScreening, version) resolve về một biểu thức cố định — KHÔNG đọc con trỏ cv_policy_version.
            var expression = await _db.ScoringPolicies
                .AsNoTracking()
                .Where(p => p.CampaignId == candidate.CampaignId
                    && p.Kind == ScoringExpressionKind.CvScreening
                    && p.Version == pinnedVersion)
                .Select(p => p.Expression)
                .FirstOrDefaultAsync(ct);

            if (string.IsNullOrWhiteSpace(expression))
            {
                _logger.LogWarning(
                    "SCP1/B7: candidate {CandidateId} ghim policy CvScreening v{Ver} nhưng KHÔNG tìm thấy "
                    + "dòng scoring_policies ⇒ lùi về CAMP-14, scoreFallback = true.", candidate.Id, pinnedVersion);
                return (defaultScore, true);
            }

            // (1) 6 biến từ assessments đã qua guard + bộ nhu cầu campaign.
            var strong = assessments.Count(a => a.Level == NeedLevels.Strong);
            var partial = assessments.Count(a => a.Level == NeedLevels.Partial);
            var weak = assessments.Count(a => a.Level == NeedLevels.Weak);
            var needCount = campaignNeeds.Count;   // CAMP-14 "số nhu cầu" = bộ nhu cầu campaign đã chốt

            // (4) need_count = 0 trong khi đã ghim chính sách = BẤT BIẾN HỆ THỐNG bị vi phạm (EVA1-B6:
            // "Active + needs rỗng" KHÔNG thể bắt đầu sàng). BÁO LỖI ĐÁNH GIÁ — không lùi an toàn (nó
            // che một trạng thái hỏng), không bịa điểm. Ném để có người điều tra.
            if (needCount <= 0)
            {
                _logger.LogError(
                    "SCP1/B7: candidate {CandidateId} (campaign {CampaignId}) đã ghim chính sách sàng CV "
                    + "v{Ver} nhưng need_count = 0 — bất biến hệ thống bị vi phạm, KHÔNG tính điểm.",
                    candidate.Id, candidate.CampaignId, pinnedVersion);
                throw new InvalidOperationException(
                    $"SCP1: candidate {candidate.Id} có need_count = 0 với chính sách sàng CV đã ghim.");
            }

            // RNK1 · HĐ-6 — must_have_* đếm CHỈ nhu cầu IsMustHave (bỏ "mọi nhu cầu coi là bắt buộc").
            // NGUỒN TÍNH DUY NHẤT = CvMustHaveEvaluator, dùng chung với ScoringPolicyService.ScoreCv.
            var mh = CvMustHaveEvaluator.Evaluate(
                campaignNeeds,
                assessments.Where(a => a.Level != NeedLevels.Weak),
                assessments.Where(a => a.Level == NeedLevels.Weak));

            var ctx = ScoringContext.ForCvScreening(new CvScreeningScoringInputs(
                StrongCount: strong, PartialCount: partial, WeakCount: weak,
                NeedCount: needCount, MustHaveTotal: mh.MustHaveTotal, MustHaveMet: mh.MustHaveMet));

            // Parse + eval + phân loại lỗi đi qua ScoringPolicyRunner — CÙNG một hàm đường xem-trước/áp
            // (B8) dùng. Lùi-an-toàn + log giữ ở đây (B7 làm tròn AwayFromZero khác B6).
            var outcome = ScoringPolicyRunner.Evaluate(expression, ctx);
            if (outcome.Exception is not null)
                _logger.LogError(outcome.Exception, "SCP1/B7: bộ đánh giá ném cho candidate {CandidateId}", candidate.Id);

            if (outcome.Value is decimal ps)
            {
                _logger.LogInformation(
                    "SCP1/B7: candidate {CandidateId} chấm bằng chính sách sàng CV v{Ver} = {Score}",
                    candidate.Id, pinnedVersion, ps);
                return ((int)Math.Round(ps, MidpointRounding.AwayFromZero), false);
            }

            // (3) LÙI AN TOÀN + cờ (như B6). KHÔNG clamp (clamp che lỗi policy). KHÔNG nuốt lỗi.
            _logger.LogWarning(
                "SCP1/B7: candidate {CandidateId} — chính sách sàng CV v{Ver} LỖI [{Reason}] ⇒ lùi về "
                + "CAMP-14 = {Default}, scoreFallback = true.",
                candidate.Id, pinnedVersion, outcome.FailReason, defaultScore);
            return (defaultScore, true);
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
            // RNK1 · HĐ-6 — nạp job_needs CÙNG lượt (jsonb trên campaigns, không phải nav) để đánh giá
            // điều kiện loại read-time cho từng dòng; KHÔNG query mới.
            var campaignRow = await _db.Campaigns
                .Where(c => c.Id == campaignId && c.OrgId == orgId)
                .Select(c => new { c.JobNeeds })
                .FirstOrDefaultAsync(ct);
            if (campaignRow is null)
                throw new KeyNotFoundException($"Campaign {campaignId} not found.");
            var jobNeeds = campaignRow.JobNeeds ?? new List<JobNeed>();

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

            var items = page.Select(c =>
            {
                // RNK1 · HĐ-6 — điều kiện loại đánh giá READ-TIME trên dòng đã ở bộ nhớ (như
                // VerificationRisk bên dưới — LINQ-to-Objects, KHÔNG query). job_needs cố định cho cả
                // campaign nên eligible ổn định.
                var mh = CvMustHaveEvaluator.Evaluate(jobNeeds, c.Strengths, c.Gaps);
                return new CandidateListItem
                {
                    Id = c.Id,
                    FullName = c.FullName,
                    Email = c.Email,
                    Status = c.Status.ToString(),
                    OverallMatchScore = c.OverallMatchScore,
                    Skills = c.Skills,
                    // EVA1-B2 — cờ rủi ro + con dấu thang điểm phải ra tới màn DANH SÁCH, không chỉ
                    // màn chi tiết: đó chính là chỗ HR đặt ứng viên cạnh nhau để so.
                    VerificationRisk = c.VerificationRisk,
                    ScreeningVersion = c.ScreeningVersion,
                    ScoreFallback = c.ScoreFallback,   // SCP1 · B7 (HĐ-5)
                    Eligible = mh.Eligible,            // RNK1 · HĐ-6
                    MustHaveMet = mh.MustHaveMet,
                    MustHaveTotal = mh.MustHaveTotal,
                };
            }).ToList();

            // RNK1 · HĐ-6 — sort mặc định "Eligible desc, rồi điểm". `eligible` KHÔNG phải cột nên
            // KHÔNG vào được ORDER BY / khoá keyset của DB → chỉ đảo thứ tự TRONG TRANG đang xem, sau
            // khi `next` cursor đã chốt từ dòng cuối theo thứ tự DB (score DESC, id DESC). Hệ quả:
            // ứng viên không đủ điều kiện chìm xuống đáy TRANG HR đang xem, không xuyên trang — cùng
            // lớp giới hạn đã ghi cho `?skill=`. Chỉ áp cho sort mặc định (score); `sort=name` không đụng.
            if (normalizedSort != "name")
                items = items
                    .OrderByDescending(i => i.Eligible)
                    .ThenByDescending(i => i.OverallMatchScore ?? -1)
                    .ThenByDescending(i => i.Id)
                    .ToList();

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

            // RNK1 · HĐ-6 — job_needs (jsonb trên campaigns) cho điều kiện loại read-time. 1 projection
            // scalar, không nạp cả entity campaign.
            var jobNeeds = await _db.Campaigns
                .Where(c => c.Id == campaignId)
                .Select(c => c.JobNeeds)
                .FirstOrDefaultAsync(ct) ?? new List<JobNeed>();
            var mh = CvMustHaveEvaluator.Evaluate(jobNeeds, candidate.Strengths, candidate.Gaps);

            static List<NeedAssessmentItem> Map(List<NeedAssessment>? items) =>
                (items ?? new List<NeedAssessment>())
                    .Select(a => new NeedAssessmentItem
                    {
                        NeedId = a.NeedId,
                        Area = a.Area,
                        Level = a.Level,
                        Evidence = a.Evidence,
                    }).ToList();

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
                ScreeningVersion = candidate.ScreeningVersion,
                ScoreFallback = candidate.ScoreFallback,   // SCP1 · B7 (HĐ-5)
                Eligible = mh.Eligible,                    // RNK1 · HĐ-6
                MustHaveMet = mh.MustHaveMet,
                MustHaveTotal = mh.MustHaveTotal,
                MissingMustHave = mh.Missing.Select(n => n.Text).ToList(),
                FitSummary = candidate.FitSummary,
                // Strong trước Partial trong `strengths`: HR đọc từ trên xuống, thứ chắc chắn nhất
                // phải nằm trên. `gaps` toàn Weak nên giữ nguyên thứ tự nhu cầu.
                Strengths = Map(candidate.Strengths)
                    .OrderBy(a => a.Level == NeedLevels.Strong ? 0 : 1).ToList(),
                Gaps = Map(candidate.Gaps),
                BonusSignals = candidate.BonusSignals ?? new List<string>(),
                VerificationRisk = candidate.VerificationRisk,
                VerifyQuestions = candidate.VerifyQuestions ?? new List<string>(),
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
