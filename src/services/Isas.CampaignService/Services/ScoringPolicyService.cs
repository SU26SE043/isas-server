using System.Text;
using System.Text.Json;
using Isas.CampaignService.DTOs;
using Isas.CampaignService.Models;
using Isas.Shared.Scoring;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Isas.CampaignService.Services
{
    /// <inheritdoc />
    public sealed class ScoringPolicyService : IScoringPolicyService
    {
        private readonly CampaignDbContext _db;
        private readonly ILogger<ScoringPolicyService> _logger;

        // RNK1 · HĐ-1 — mẫu bị RÚT khỏi danh sách employer (KHÔNG xoá row: policy đã tạo có
        // sourceTemplateId trỏ tới, và CreatePolicyAsync còn kiểm id này là mẫu hệ thống hợp lệ).
        // "Phạt bỏ câu" 5c900002 nay là LUẬT engine (CAMP-21), không phải lựa chọn.
        private static readonly IReadOnlySet<Guid> RetiredTemplateIds = new HashSet<Guid>
        {
            new("5c900002-0000-0000-0000-000000000000"),
        };

        // logger OPTIONAL (mặc định NullLogger): DI container vẫn tiêm bản thật vì ILogger<T> luôn
        // đăng ký; default chỉ để `new ScoringPolicyService(db)` trong test khỏi phải sửa hàng loạt.
        public ScoringPolicyService(CampaignDbContext db, ILogger<ScoringPolicyService>? logger = null)
        {
            _db = db;
            _logger = logger ?? NullLogger<ScoringPolicyService>.Instance;
        }

        public async Task<ScoringPolicyValidateResponse> ValidateExpressionAsync(
            Guid orgId, Guid campaignId, ScoringExpressionKind kind, string expression,
            CancellationToken ct = default)
        {
            // Chỉ để chặn dò campaign của org khác — 1 index probe, KHÔNG chạm dữ liệu ứng viên.
            // Query filter soft-delete (D11) tự lọc ⇒ campaign đã xoá cũng ra 404.
            var owned = await _db.Campaigns.AnyAsync(c => c.Id == campaignId && c.OrgId == orgId, ct);
            if (!owned) throw new KeyNotFoundException($"Campaign {campaignId} not found.");

            // BỘ MẪU nằm trong code (ScoringContext.Sample) — endpoint chạy được cả khi campaign chưa
            // có ứng viên nào. Không ghi gì.
            var r = ScoringExpression.Validate(kind, expression ?? string.Empty);
            return new ScoringPolicyValidateResponse(
                r.Valid,
                r.Valid ? r.SampleScore : null,
                r.Valid ? null : r.Errors);
        }

        public async Task<IReadOnlyList<ScoringPolicyResponse>> GetTemplatesAsync(CancellationToken ct = default)
        {
            // Chỉ mẫu hệ thống. Materialize rồi sắp + map trong bộ nhớ (≤ vài chục dòng):
            //   · sắp theo `Kind` = giá trị ENUM (Interview 0 trước CvScreening 1), KHÔNG theo chuỗi
            //     đã convert — cột lưu "CvScreening"/"Interview" nên ORDER BY ở SQL sẽ ra C trước I,
            //     lệ thuộc collation DB. Sắp trong bộ nhớ cho tất định giữa SQLite (test) và Postgres.
            //   · `Kind.ToString()` cũng để trong bộ nhớ, không nhờ EF dịch.
            var rows = await _db.ScoringPolicies
                .AsNoTracking()
                .Where(p => p.CampaignId == null)
                .ToListAsync(ct);

            return rows
                .Where(p => !RetiredTemplateIds.Contains(p.Id))   // RNK1 · HĐ-1 — bỏ mẫu "Phạt bỏ câu"
                .OrderBy(p => p.Kind)
                .ThenBy(p => p.Name, StringComparer.Ordinal)
                .Select(Map)
                .ToList();
        }

        public async Task<IReadOnlyList<ScoringPolicyResponse>> ListPoliciesAsync(
            Guid orgId, Guid campaignId, string? kind, CancellationToken ct = default)
        {
            // Bộ lọc TUỲ CHỌN — null/rỗng ⇒ trả cả hai loại; giá trị lạ ⇒ 400 (cùng câu với đường tạo).
            ScoringExpressionKind? kindFilter = kind switch
            {
                null or "" => null,
                "Interview" => ScoringExpressionKind.Interview,
                "CvScreening" => ScoringExpressionKind.CvScreening,
                _ => throw new ArgumentException("kind phải là 'Interview' hoặc 'CvScreening'."),
            };

            // Chặn dò campaign của org khác (query filter soft-delete D11 tự lọc campaign đã xoá).
            var owned = await _db.Campaigns.AnyAsync(c => c.Id == campaignId && c.OrgId == orgId, ct);
            if (!owned) throw new KeyNotFoundException($"Campaign {campaignId} not found.");

            // Chỉ BẢN CỦA campaign này (campaign_id != NULL) — mẫu hệ thống có route riêng.
            var q = _db.ScoringPolicies.AsNoTracking()
                .Where(p => p.CampaignId == campaignId);
            if (kindFilter is { } k)
                q = q.Where(p => p.Kind == k);
            var rows = await q.ToListAsync(ct);

            // Sắp trong bộ nhớ cho tất định giữa SQLite (test) và Postgres — Kind theo giá trị ENUM
            // (Interview 0 trước CvScreening 1), rồi Version GIẢM DẦN (bản mới nhất lên đầu).
            return rows
                .OrderBy(p => p.Kind)
                .ThenByDescending(p => p.Version)
                .Select(Map)
                .ToList();
        }

        public async Task<ScoringPolicyResponse> CreatePolicyAsync(
            Guid orgId, Guid actorUserId, bool isOrgAdmin,
            Guid campaignId, CreateScoringPolicyRequest req, CancellationToken ct = default)
        {
            var kind = req.Kind switch
            {
                "Interview" => ScoringExpressionKind.Interview,
                "CvScreening" => ScoringExpressionKind.CvScreening,
                _ => throw new ArgumentException("kind phải là 'Interview' hoặc 'CvScreening'."),
            };
            RejectCvPassScore(kind, req.PassScorePct);   // B9 — sàng CV không có đạt/trượt
            PassScorePctRule.Validate(req.PassScorePct); // B11 — ngoài [0,100] → 400, KHÔNG để CHECK DB nổ thành 500
            if (string.IsNullOrWhiteSpace(req.Name))
                throw new ArgumentException("name là bắt buộc.");
            var expression = req.Expression ?? string.Empty;

            var campaign = await _db.Campaigns
                .FirstOrDefaultAsync(c => c.Id == campaignId && c.OrgId == orgId, ct)
                ?? throw new KeyNotFoundException($"Campaign {campaignId} not found.");

            if (campaign.Status is CampaignStatus.Closed or CampaignStatus.Archived)
                throw new InvalidOperationException("Chiến dịch đã đóng — không tạo chính sách chấm mới.");

            // Đã có người được chấm (theo LOẠI) ⇒ tạo version mới lúc này KHÔNG được tự dời con trỏ:
            // dời ngay = đổi kết quả người ta trong im lặng. VẪN cho tạo dòng (để HR có id + biểu thức
            // mà xem trước / áp — B8/HĐ-4), chỉ giữ con trỏ đứng yên tới khi apply.
            //   · Trước B8 chỗ này ném POLICY_NEEDS_PREVIEW (409) — nhưng như vậy thì KHÔNG có đường
            //     nào tạo được version để mà preview → apply. B4 đã ghi "luồng đó thuộc B8".
            var hasScored = kind == ScoringExpressionKind.Interview
                ? await _db.CampaignRankings.AnyAsync(r => r.CampaignId == campaignId, ct)
                : await _db.CvSubmissions.AnyAsync(s => s.CampaignId == campaignId && s.OverallMatchScore != null, ct);

            // HĐ-6 — HrMember chỉ sửa chính sách chấm khi campaign còn Draft.
            if (campaign.Status != CampaignStatus.Draft && !isOrgAdmin)
                throw new EntitlementForbiddenException(
                    "HrMember chỉ sửa chính sách chấm khi chiến dịch còn Draft (HĐ-6).");

            // sourceTemplateId — PROVENANCE. Nếu có: phải là mẫu hệ thống (campaign_id NULL) cùng loại.
            // KHÔNG đọc giá trị từ mẫu ở đây (client gửi giá trị đã chỉnh trong body); chỉ lưu id làm dấu.
            if (req.SourceTemplateId is Guid tid)
            {
                var okTemplate = await _db.ScoringPolicies
                    .AnyAsync(p => p.Id == tid && p.CampaignId == null && p.Kind == kind, ct);
                if (!okTemplate)
                    throw new ArgumentException("sourceTemplateId không phải mẫu hệ thống hợp lệ của cùng loại.");
            }

            // B3 — validate lại, KHÔNG tin dữ liệu vào.
            var check = ScoringExpression.Validate(kind, expression);
            if (!check.Valid)
                throw new ScoringExpressionInvalidException(check.Errors);

            var maxVersion = await _db.ScoringPolicies
                .Where(p => p.CampaignId == campaignId && p.Kind == kind)
                .Select(p => (int?)p.Version)
                .MaxAsync(ct) ?? 0;

            var policy = new ScoringPolicy
            {
                Id = Guid.NewGuid(),
                CampaignId = campaignId,
                Kind = kind,
                Version = maxVersion + 1,
                EngineVersion = ScoringEngine.Version,   // engine HIỆN TẠI — KHÔNG chép từ mẫu
                Name = req.Name!.Trim(),
                Description = string.IsNullOrWhiteSpace(req.Description) ? null : req.Description!.Trim(),
                Expression = expression,                 // giá trị trong body — KHÔNG deref mẫu
                PassScorePct = req.PassScorePct,
                SourceTemplateId = req.SourceTemplateId, // dấu vết provenance, KHÔNG FK sống
                CreatedAt = DateTime.UtcNow,
                CreatedBy = actorUserId,
            };
            _db.ScoringPolicies.Add(policy);

            // Chưa ai được chấm ⇒ trỏ con trỏ vào version vừa tạo là an toàn (không có kết quả cũ để
            // relabel). Campaign đã chấm ⇒ để con trỏ đứng yên; HR phải preview + apply (B8).
            if (!hasScored)
            {
                if (kind == ScoringExpressionKind.Interview)
                {
                    campaign.InterviewPolicyVersion = policy.Version;
                    // B9 — ngưỡng đạt nay do CHÍNH SÁCH sở hữu. policy.PassScorePct == null ⇒ chính sách
                    // không quy định ⇒ giữ nguyên giá trị HR đã đặt (ngữ nghĩa null của E5: "HR quyết tay").
                    if (policy.PassScorePct is int pp) campaign.PassScorePct = pp;
                }
                else campaign.CvPolicyVersion = policy.Version;
            }

            await _db.SaveChangesAsync(ct);
            return Map(policy);
        }

        public async Task<ScoringPolicyPreviewResponse> PreviewPolicyAsync(
            Guid orgId, Guid campaignId, ScoringPolicyPreviewRequest req,
            string? cursor, int? limit, CancellationToken ct = default)
        {
            var kind = req.Kind switch
            {
                "Interview" => ScoringExpressionKind.Interview,
                "CvScreening" => ScoringExpressionKind.CvScreening,
                _ => throw new ArgumentException("kind phải là 'Interview' hoặc 'CvScreening'."),
            };
            // B9/B11 — giữ parity với đường tạo: sàng CV không có đạt/trượt, và ngưỡng ngoài [0,100]
            // phải 400 NGAY ở xem trước — không thì FE xem trước với một ngưỡng nó sẽ không lưu được
            // (CHECK DB nổ thành 500 lúc apply, hoặc fingerprint lệch).
            RejectCvPassScore(kind, req.PassScorePct);
            PassScorePctRule.Validate(req.PassScorePct);
            var expression = req.Expression ?? string.Empty;

            var campaign = await _db.Campaigns
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == campaignId && c.OrgId == orgId, ct)
                ?? throw new KeyNotFoundException($"Campaign {campaignId} not found.");

            // Không tin dữ liệu vào — validate như đường tạo (B3/B4).
            var check = ScoringExpression.Validate(kind, expression);
            if (!check.Valid)
                throw new ScoringExpressionInvalidException(check.Errors);

            var fingerprint = ScoringPolicyFingerprint.Compute(
                expression, req.PassScorePct, ScoringEngine.Version);

            // Chạy TOÀN BỘ ứng viên đã chấm (LOCAL, không xuyên service). Đánh giá bó biến scalar rất
            // rẻ ⇒ tính hết rồi mới phân trang phần TRẢ VỀ (CẤM #1: hạng trên tập con là hạng SAI).
            var cvNeedCount = campaign.JobNeeds?.Count ?? 0;
            var scored = kind == ScoringExpressionKind.Interview
                ? await LoadInterviewScoredAsync(campaignId, ct)
                : await LoadCvScoredAsync(campaign, ct);

            if (kind == ScoringExpressionKind.Interview)
                WarnOnPreRnk1Snapshots(campaignId, scored);

            var computed = ComputeAll(kind, expression, scored, cvNeedCount);
            var rows = computed
                .Select(x => new ScoringPolicyPreviewRow(
                    x.CandidateId, FullName: null,
                    x.OldScore, x.NewScore, x.OldRank, x.NewRank, x.RankChanged))
                .ToList();

            var lim = limit is > 0 and <= 2000 ? limit.Value : 500;
            var skip = DecodeCursor(cursor);
            var page = rows.Skip(skip).Take(lim).ToList();
            var next = skip + page.Count < rows.Count
                ? EncodeCursor(skip + page.Count)
                : null;

            return new ScoringPolicyPreviewResponse(fingerprint, rows.Count, page, next);
        }

        public async Task<ApplyScoringPolicyResult> ApplyPolicyAsync(
            Guid orgId, Guid actorUserId, bool isOrgAdmin,
            Guid campaignId, Guid policyId, ApplyScoringPolicyRequest req, CancellationToken ct = default)
        {
            // HĐ-6 — chỉ OrgAdmin đánh giá lại toàn bộ.
            if (!isOrgAdmin)
                throw new EntitlementForbiddenException("Chỉ OrgAdmin được áp chính sách chấm mới (HĐ-6).");

            var campaign = await _db.Campaigns
                .FirstOrDefaultAsync(c => c.Id == campaignId && c.OrgId == orgId, ct)
                ?? throw new KeyNotFoundException($"Campaign {campaignId} not found.");

            // Policy phải là BẢN CỦA CHÍNH campaign này (không phải mẫu hệ thống, không phải campaign khác).
            var policy = await _db.ScoringPolicies
                .FirstOrDefaultAsync(p => p.Id == policyId && p.CampaignId == campaignId, ct)
                ?? throw new KeyNotFoundException($"Scoring policy {policyId} not found.");

            // HĐ-4 — vân tay tính LẠI từ dòng đã lưu; lệch ⇒ 409 (ai đó đổi biểu thức sau khi HR xem trước).
            var actual = ScoringPolicyFingerprint.Compute(
                policy.Expression, policy.PassScorePct, policy.EngineVersion);
            if (!string.Equals(actual, req.Fingerprint, StringComparison.OrdinalIgnoreCase))
                throw new ScoringPolicyChangedException();

            var kind = policy.Kind;

            if (kind == ScoringExpressionKind.Interview)
            {
                var ranks = await _db.CampaignRankings
                    .Where(r => r.CampaignId == campaignId && r.ScoringInputs != null)
                    .ToListAsync(ct);
                if (ranks.Count == 0)
                    throw new InvalidOperationException("Chưa có ứng viên nào được chấm để đánh giá lại.");

                var scored = ranks
                    .Select(r => new ScoredRow(r.CandidateId, r.TotalScore, r.ScoringInputs!, null))
                    .ToList();
                WarnOnPreRnk1Snapshots(campaignId, scored);
                var byId = ComputeAll(kind, policy.Expression, scored)
                    .ToDictionary(x => x.CandidateId);

                var oldSnapshot = new List<object>(ranks.Count);
                int rankChanged = 0;
                foreach (var r in ranks)
                {
                    var x = byId[r.CandidateId];
                    oldSnapshot.Add(new { c = r.CandidateId, s = r.TotalScore });
                    r.TotalScore = x.NewScore ?? r.TotalScore;   // Interview: NewScore luôn có
                    r.PolicyVersion = policy.Version;
                    r.PolicyName = policy.Name;
                    r.ScoreFallback = x.FellBack;
                    r.UpdatedAt = DateTime.UtcNow;
                    if (x.RankChanged) rankChanged++;
                }

                campaign.InterviewPolicyVersion = policy.Version;
                // B9 — đồng bộ ngưỡng đạt: đường Pass/Fail (E5, CampaignService.cs:1681) đọc
                // campaign.PassScorePct, và cột đó nằm trong hợp đồng API công khai (PublicApiController,
                // CSV, PDF) nên phải ĐỒNG BỘ VÀO CỘT, không đổi nguồn đọc. policy.PassScorePct == null ⇒
                // chính sách không quy định ⇒ giữ nguyên ngưỡng HR đã đặt.
                if (policy.PassScorePct is int pp) campaign.PassScorePct = pp;
                AddApplyAudit(actorUserId, orgId, campaign.Id, kind, policy.Version, oldSnapshot);
                await _db.SaveChangesAsync(ct);
                return new ApplyScoringPolicyResult(ranks.Count, rankChanged, policy.Version);
            }
            else
            {
                var cands = await _db.CvSubmissions
                    .Where(s => s.CampaignId == campaignId && s.OverallMatchScore != null)
                    .ToListAsync(ct);
                if (cands.Count == 0)
                    throw new InvalidOperationException("Chưa có ứng viên nào được chấm để đánh giá lại.");

                var needCount = campaign.JobNeeds?.Count ?? 0;
                var scored = cands
                    .Select(c => new ScoredRow(c.Id, c.OverallMatchScore!.Value, null, BuildAssessments(c)))
                    .ToList();
                var byId = ComputeAll(kind, policy.Expression, scored, needCount)
                    .ToDictionary(x => x.CandidateId);

                var oldSnapshot = new List<object>(cands.Count);
                int applied = 0, rankChanged = 0;
                foreach (var c in cands)
                {
                    var x = byId[c.Id];
                    oldSnapshot.Add(new { c = c.Id, s = c.OverallMatchScore });
                    // newScore null (0 assessment + biểu thức lỗi) ⇒ GIỮ điểm cũ, không "bỏ chấm" người ta.
                    if (x.NewScore is decimal ns)
                    {
                        c.OverallMatchScore = (int)ns;
                        c.ScoringPolicyVersion = policy.Version;   // re-pin (như rescreen)
                        c.ScoreFallback = x.FellBack;
                        c.UpdatedAt = DateTime.UtcNow;
                        applied++;
                    }
                    if (x.RankChanged) rankChanged++;
                }

                campaign.CvPolicyVersion = policy.Version;
                AddApplyAudit(actorUserId, orgId, campaign.Id, kind, policy.Version, oldSnapshot);
                await _db.SaveChangesAsync(ct);
                return new ApplyScoringPolicyResult(applied, rankChanged, policy.Version);
            }
        }

        // ── Nội bộ B8 ────────────────────────────────────────────────────────────────────────────

        /// <summary>1 ứng viên đã chấm — bó biến để chạy lại biểu thức. Interview mang
        /// <see cref="Bag"/>; CvScreening mang <see cref="Assessments"/>.</summary>
        private sealed record ScoredRow(
            Guid CandidateId,
            decimal OldScore,
            ScoringInputsSnapshot? Bag,
            IReadOnlyList<NeedAssessment>? Assessments);

        private async Task<List<ScoredRow>> LoadInterviewScoredAsync(Guid campaignId, CancellationToken ct)
        {
            var rows = await _db.CampaignRankings
                .AsNoTracking()
                .Where(r => r.CampaignId == campaignId && r.ScoringInputs != null)
                .Select(r => new { r.CandidateId, r.TotalScore, r.ScoringInputs })
                .ToListAsync(ct);
            return rows
                .Select(r => new ScoredRow(r.CandidateId, r.TotalScore, r.ScoringInputs, null))
                .ToList();
        }

        private async Task<List<ScoredRow>> LoadCvScoredAsync(Campaign campaign, CancellationToken ct)
        {
            var rows = await _db.CvSubmissions
                .AsNoTracking()
                .Where(s => s.CampaignId == campaign.Id && s.OverallMatchScore != null)
                .Select(s => new { s.Id, s.OverallMatchScore, s.Strengths, s.Gaps })
                .ToListAsync(ct);
            return rows
                .Select(s => new ScoredRow(
                    s.Id,
                    s.OverallMatchScore!.Value,
                    null,
                    Concat(s.Strengths, s.Gaps)))
                .ToList();

            static List<NeedAssessment> Concat(List<NeedAssessment>? a, List<NeedAssessment>? b)
            {
                var r = new List<NeedAssessment>();
                if (a is not null) r.AddRange(a);
                if (b is not null) r.AddRange(b);
                return r;
            }
        }

        private static IReadOnlyList<NeedAssessment> BuildAssessments(CvSubmission c)
        {
            var r = new List<NeedAssessment>();
            if (c.Strengths is not null) r.AddRange(c.Strengths);
            if (c.Gaps is not null) r.AddRange(c.Gaps);
            return r;
        }

        /// <summary>RNK1 · HĐ-2 — báo (1 lần, gộp) khi tập ứng viên đang tính lại có dòng mang snapshot
        /// GHI TRƯỚC RNK1 (thiếu <c>seedAnswered</c>/<c>seedTotal</c>). Luật câu bỏ trống (CAMP-21)
        /// KHÔNG áp cho các dòng đó — <see cref="SkipPenaltyRule.Apply"/> trả điểm nguyên — nên điểm
        /// preview/apply của chúng = điểm tính lúc chấm, đúng ý đồ "không đổi thước đo giữa chừng".
        /// Không lùi-an-toàn, không ném: đây là ghi nhận để người đọc bảng hiểu vì sao nhóm cũ không bị phạt.</summary>
        private void WarnOnPreRnk1Snapshots(Guid campaignId, IReadOnlyList<ScoredRow> scored)
        {
            var stale = scored.Count(s => s.Bag is null || s.Bag.SeedTotal is null);
            if (stale > 0)
                _logger.LogWarning(
                    "RNK1/HĐ-2: campaign {CampaignId} — {Stale}/{Total} dòng có snapshot trước RNK1 "
                    + "(thiếu seedAnswered/seedTotal). Luật câu bỏ trống KHÔNG áp cho các dòng đó.",
                    campaignId, stale, scored.Count);
        }

        /// <summary>1 dòng đã tính đủ: điểm/hạng cũ↔mới + cờ lùi an toàn (<c>FellBack</c>).</summary>
        private sealed record ComputedRow(
            Guid CandidateId, decimal? OldScore, decimal? NewScore, bool FellBack,
            int OldRank, int NewRank)
        {
            public bool RankChanged => OldRank != NewRank;
        }

        /// <summary>
        /// Điểm/hạng cũ↔mới + cờ lùi an toàn cho MỌI dòng — hạng gán trên TOÀN BỘ tập (competition
        /// ranking, khớp <c>GetCampaignResultsAsync</c>: rank = số điểm cao hơn + 1, đồng điểm cùng
        /// rank). Sắp theo <c>newRank</c> rồi <c>candidateId</c> để phân trang tất định.
        /// </summary>
        private static List<ComputedRow> ComputeAll(
            ScoringExpressionKind kind, string expression, List<ScoredRow> scored, int cvNeedCount = 0)
        {
            var mid = scored
                .Select(s =>
                {
                    var (newScore, fellBack) = kind == ScoringExpressionKind.Interview
                        ? ScoreInterview(expression, s.Bag)
                        : ScoreCv(expression, s.Assessments ?? Array.Empty<NeedAssessment>(), cvNeedCount);
                    return (s.CandidateId, OldScore: (decimal?)s.OldScore, NewScore: newScore, FellBack: fellBack);
                })
                .ToList();

            var oldRank = AssignRanks(mid.Select(x => (x.CandidateId, x.OldScore)));
            var newRank = AssignRanks(mid.Select(x => (x.CandidateId, x.NewScore)));

            return mid
                .Select(x => new ComputedRow(
                    x.CandidateId, x.OldScore, x.NewScore, x.FellBack,
                    oldRank[x.CandidateId], newRank[x.CandidateId]))
                .OrderBy(r => r.NewRank)
                .ThenBy(r => r.CandidateId)
                .ToList();
        }

        /// <summary>Điểm 1 ứng viên phỏng vấn dưới <paramref name="expression"/>; lỗi lúc chạy ⇒ công
        /// thức weighted mặc định (B6) + cờ true. Bag null (event trước B5) ⇒ (0, true) — không dùng được.
        ///
        /// <para>RNK1 · HĐ-2 / CAMP-21 — điểm (nhánh policy-ok LẪN nhánh lùi mặc định) đi qua
        /// <see cref="SkipPenaltyRule.Apply"/>, CÙNG hàm Shared mà SessionScoringNotifier dùng trên
        /// đường chấm thường ⇒ "điểm preview = điểm apply = điểm một lần chấm mới". Snapshot trước RNK1
        /// (SkipPenalty/SeedTotal null) ⇒ Apply trả nguyên ⇒ hàng lịch sử KHÔNG bị đổi điểm.</para></summary>
        private static (decimal? Score, bool FellBack) ScoreInterview(string expression, ScoringInputsSnapshot? bag)
        {
            if (bag is null) return (0m, true);
            var inputs = bag.ToInterviewInputs();
            var def = DefaultInterviewTotal(bag);
            var ctx = ScoringContext.ForInterview(inputs);
            var outcome = ScoringPolicyRunner.Evaluate(expression, ctx);

            decimal raw;
            bool fellBack;
            if (outcome.Value is decimal v) { raw = Math.Round(v, 2); fellBack = false; }
            else { raw = def; fellBack = true; }

            return (SkipPenaltyRule.Apply(raw, inputs), fellBack);
        }

        /// <summary>Điểm 1 ứng viên sàng CV dưới <paramref name="expression"/>; lỗi ⇒ CAMP-14 mặc định
        /// (B7) + cờ true. need_count = 0 ⇒ (null, true) — B7 ném; ở batch mình BỎ QUA dòng đó.</summary>
        private static (decimal? Score, bool FellBack) ScoreCv(
            string expression, IReadOnlyList<NeedAssessment> assessments, int needCount)
        {
            int? def = assessments.Count == 0
                ? null
                : (int)Math.Round(
                    100m * assessments.Sum(a => NeedLevels.Credit(a.Level)) / assessments.Count,
                    MidpointRounding.AwayFromZero);

            if (needCount <= 0)
                return (def, true);   // không tính được biểu thức — chỉ có mặc định (có thể null)

            var strong = assessments.Count(a => a.Level == NeedLevels.Strong);
            var partial = assessments.Count(a => a.Level == NeedLevels.Partial);
            var weak = assessments.Count(a => a.Level == NeedLevels.Weak);
            var ctx = ScoringContext.ForCvScreening(new CvScreeningScoringInputs(
                StrongCount: strong, PartialCount: partial, WeakCount: weak,
                NeedCount: needCount, MustHaveTotal: needCount, MustHaveMet: strong + partial));

            var outcome = ScoringPolicyRunner.Evaluate(expression, ctx);
            return outcome.Value is decimal v
                ? ((int)Math.Round(v, MidpointRounding.AwayFromZero), false)
                : ((decimal?)def, true);
        }

        private static decimal DefaultInterviewTotal(ScoringInputsSnapshot bag)
        {
            decimal weightedSum = 0m, weightSum = 0m;
            foreach (var c in bag.Criteria)
            {
                weightedSum += c.Pct * c.Weight;
                weightSum += c.Weight;
            }
            return weightSum <= 0m
                ? 0m
                : Math.Clamp(Math.Round(weightedSum / weightSum, 2), 0m, 100m);
        }

        /// <summary>Competition ranking: rank = số điểm CAO HƠN + 1; đồng điểm cùng rank (1,1,3). null
        /// (không tính được) xuống đáy.</summary>
        private static Dictionary<Guid, int> AssignRanks(IEnumerable<(Guid Id, decimal? Score)> rows)
        {
            var ordered = rows
                .OrderByDescending(x => x.Score.HasValue)
                .ThenByDescending(x => x.Score ?? decimal.MinValue)
                .ThenBy(x => x.Id)
                .ToList();

            var result = new Dictionary<Guid, int>(ordered.Count);
            for (int i = 0; i < ordered.Count; i++)
            {
                var same = i > 0
                    && Nullable.Equals(ordered[i - 1].Score, ordered[i].Score);
                result[ordered[i].Id] = same ? result[ordered[i - 1].Id] : i + 1;
            }
            return result;
        }

        private void AddApplyAudit(
            Guid actorUserId, Guid orgId, Guid campaignId,
            ScoringExpressionKind kind, int version, IReadOnlyList<object> oldScores)
        {
            var payload = JsonSerializer.Serialize(new
            {
                policyVersion = version,
                kind = kind.ToString(),
                old = oldScores,
            });
            _db.AuditLogs.Add(new AuditLog
            {
                Id = Guid.NewGuid(),
                OrgId = orgId,
                ActorUserId = actorUserId,
                Action = AuditAction.ApplyScoringPolicy,
                Entity = "Campaign",
                EntityId = campaignId,
                Summary = payload,
                At = DateTime.UtcNow,
            });
        }

        private static string EncodeCursor(int skip) =>
            Convert.ToBase64String(Encoding.UTF8.GetBytes(skip.ToString()));

        private static int DecodeCursor(string? cursor)
        {
            if (string.IsNullOrWhiteSpace(cursor)) return 0;
            try
            {
                var s = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
                return int.TryParse(s, out var n) && n >= 0 ? n : 0;
            }
            catch { return 0; }
        }

        // B9 — sàng CV KHÔNG có khái niệm đạt/trượt (không cột, không consumer, không màn hiển thị).
        // Nhận passScorePct cho kind CvScreening = hứa với employer một quyết định không tồn tại.
        private static void RejectCvPassScore(ScoringExpressionKind kind, int? passScorePct)
        {
            if (kind == ScoringExpressionKind.CvScreening && passScorePct is not null)
                throw new ArgumentException(
                    "Sàng CV không có ngưỡng đạt/trượt — bỏ passScorePct (chỉ chính sách chấm phỏng vấn dùng ngưỡng).");
        }

        private static ScoringPolicyResponse Map(ScoringPolicy p) => new(
            p.Id,
            p.Kind.ToString(),
            p.Version,
            p.EngineVersion,
            p.Name,
            p.Description,
            p.Expression,
            p.PassScorePct,
            p.SourceTemplateId,
            p.CreatedAt,
            p.CreatedBy);
    }
}
