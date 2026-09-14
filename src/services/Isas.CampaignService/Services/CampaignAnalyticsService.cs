using Isas.CampaignService.DTOs;
using Isas.CampaignService.Models;
using Isas.Shared.Analytics;
using Isas.Shared.Scoring;
using Microsoft.EntityFrameworkCore;

namespace Isas.CampaignService.Services
{
    /// <summary>
    /// Phân tích tuyển dụng theo TỔ CHỨC (employer) — bản ORG-SCOPED của <c>AdminController.Analytics</c>
    /// (FR18, platform-wide). Đọc 6 truy vấn cố định, mỗi truy vấn lọc theo org TRONG SQL và chỉ
    /// <c>Select</c> đúng cột cần (không nạp nguyên entity), rồi gộp trong bộ nhớ — quy mô một org là
    /// hàng chục campaign / hàng trăm dòng, và hai cột jsonb (<c>skills</c>, <c>scoring_inputs</c>) chỉ
    /// đếm được sau khi nạp về (GroupBy jsonb trong SQL không dịch được trên cả Npgsql lẫn SQLite).
    ///
    /// <para><b>Luật KHÔNG tự chép:</b> kết luận Pass/Fail, trạng thái giao lời mời và phép ghép lời
    /// mời↔join đều gọi <see cref="CampaignResultRules"/> — cùng nguồn với <c>GET /results</c> và
    /// <c>GET /invitations</c>, để tổng ở dashboard không lệch kết luận trên bảng.</para>
    ///
    /// <para><b>Soft-delete:</b> global query filter DB13 (trên <c>campaigns</c> và mọi bảng con) tự loại
    /// campaign đã xoá ở cả 6 truy vấn — không thêm vị ngữ <c>DeletedAt</c> nào ở đây.</para>
    /// </summary>
    public sealed class CampaignAnalyticsService : ICampaignAnalyticsService
    {
        private readonly CampaignDbContext _db;

        public CampaignAnalyticsService(CampaignDbContext db)
        {
            _db = db;
        }

        // Thứ tự band và nhãn risk là HỢP ĐỒNG với FE: luôn trả đủ, kể cả count 0.
        private static readonly string[] Bands = { "0-19", "20-39", "40-59", "60-79", "80-100" };
        private static readonly string[] Risks = { "Low", "Medium", "High" };
        private const int TopSkillsLimit = 10;

        /// <summary>Một dòng ranking sau khi áp luật kết luận — điểm effective (override ?? AI) + Pass/Fail/null.</summary>
        private readonly record struct Verdict(Guid CampaignId, decimal Effective, string? Result, DateTime UpdatedAt);

        public async Task<CampaignAnalyticsResponse> GetAsync(Guid orgId, AnalyticsPeriodResult period, CancellationToken ct)
        {
            var now = DateTime.UtcNow;

            // (1) campaigns của org + tiêu chí (id/tên/sàn) cho điểm sàn — một truy vấn, collection projection.
            var campaigns = await _db.Campaigns.AsNoTracking()
                .Where(c => c.OrgId == orgId)
                .Select(c => new
                {
                    c.Id,
                    c.Title,
                    c.Status,
                    c.CreatedAt,
                    c.PassScorePct,
                    Criteria = c.Criteria
                        .Select(x => new CampaignResultRules.CutoffCriterion(x.Id, x.Name, x.MinPct))
                        .ToList()
                })
                .ToListAsync(ct);

            var response = new CampaignAnalyticsResponse
            {
                From = period.FromUtc,
                To = period.ToUtc,
                Granularity = period.Granularity.ToString().ToLowerInvariant(),
            };

            response.Campaigns.Total = campaigns.Count;
            response.Campaigns.ByStatus = campaigns
                .GroupBy(c => c.Status)
                .OrderBy(g => g.Key)
                .Select(g => new StatusCount { Status = g.Key.ToString(), Count = g.Count() })
                .ToList();

            // Org chưa có campaign ⇒ mọi khối là số 0 / mảng rỗng / median null — vẫn đủ 5 band + 3 risk.
            response.Screening.FitDistribution = BandCounts(Array.Empty<decimal>());
            response.Screening.RiskBySeverity = Risks.Select(r => new RiskCount { Risk = r, Count = 0 }).ToList();
            response.Interviews.ScoreDistribution = BandCounts(Array.Empty<decimal>());
            if (campaigns.Count == 0)
                return response;

            var campaignIds = campaigns.Select(c => c.Id).ToList();
            var passScoreByCampaign = campaigns.ToDictionary(c => c.Id, c => c.PassScorePct);
            var criteriaByCampaign = campaigns.ToDictionary(c => c.Id, c => c.Criteria);

            // (2) cv_submission — status/điểm sàng/risk/skills (jsonb nạp về rồi đếm).
            var submissions = await _db.CvSubmissions.AsNoTracking()
                .Where(s => campaignIds.Contains(s.CampaignId))
                .Select(s => new { s.CampaignId, s.Status, s.OverallMatchScore, s.VerificationRisk, s.Skills })
                .ToListAsync(ct);

            // (3) campaign_invitations — đúng các cột mà ResolveDeliveryStatus + phép ghép join cần.
            var invitations = await _db.CampaignInvitations.AsNoTracking()
                .Where(i => campaignIds.Contains(i.CampaignId))
                .Select(i => new { i.Id, i.CampaignId, i.CampaignCandidateId, i.Email, i.ExpiresAt, i.EmailSentAt, i.RevokedAt })
                .ToListAsync(ct);

            // (4) campaign_membership — join/start/tiến độ + khoá ghép về lời mời.
            var memberships = await _db.CampaignMemberships.AsNoTracking()
                .Where(m => campaignIds.Contains(m.CampaignId))
                .Select(m => new
                {
                    m.CampaignId, m.InvitationId, m.CvSubmissionId, m.Email, m.JoinedAt,
                    m.SessionId, m.InterviewStatus, m.InterviewStartedAt
                })
                .ToListAsync(ct);

            // (5) campaign_rankings — điểm effective + override + bó biến để tính sàn.
            var rankings = await _db.CampaignRankings.AsNoTracking()
                .Where(r => campaignIds.Contains(r.CampaignId))
                .Select(r => new { r.CampaignId, r.SessionId, r.TotalScore, r.OverrideScore, r.OverrideResult, r.ScoringInputs, r.UpdatedAt })
                .ToListAsync(ct);

            // (6) session_flags — chỉ loại tín hiệu.
            var flags = await _db.SessionFlags.AsNoTracking()
                .Where(f => campaignIds.Contains(f.CampaignId))
                .Select(f => new { f.CampaignId, f.SignalType })
                .ToListAsync(ct);

            // ── screening ────────────────────────────────────────────────────────────
            var screening = response.Screening;
            screening.Submissions = submissions.Count;
            screening.Analyzed = submissions.Count(s => s.Status is CvSubmissionStatus.Analyzed or CvSubmissionStatus.Invited);
            screening.ByStatus = submissions
                .GroupBy(s => s.Status)
                .OrderBy(g => g.Key)
                .Select(g => new StatusCount { Status = g.Key.ToString(), Count = g.Count() })
                .ToList();
            var fitScores = submissions
                .Where(s => s.OverallMatchScore is not null)
                .Select(s => (decimal)s.OverallMatchScore!.Value)
                .ToList();
            screening.MedianFitScore = Median(fitScores);
            screening.FitDistribution = BandCounts(fitScores);
            screening.RiskBySeverity = Risks
                .Select(r => new RiskCount
                {
                    Risk = r,
                    Count = submissions.Count(s => s.VerificationRisk is not null
                        && string.Equals(s.VerificationRisk.Trim(), r, StringComparison.OrdinalIgnoreCase))
                })
                .ToList();
            screening.TopSkills = TopSkills(submissions.Select(s => s.Skills));

            // ── invitations — CÙNG ResolveDeliveryStatus + CÙNG phép ghép join với GET /invitations ────
            var joinIndexByCampaign = memberships
                .GroupBy(m => m.CampaignId)
                .ToDictionary(
                    g => g.Key,
                    g => new CampaignResultRules.InvitationJoinIndex(g.Select(m =>
                        new CampaignResultRules.MembershipJoinRow(m.InvitationId, m.CvSubmissionId, m.Email, m.JoinedAt))));
            var emptyJoinIndex = new CampaignResultRules.InvitationJoinIndex(Array.Empty<CampaignResultRules.MembershipJoinRow>());

            var invitationStatus = new Dictionary<string, int>(StringComparer.Ordinal)
            {
                [InvitationDeliveryStatus.Queued] = 0,
                [InvitationDeliveryStatus.Sent] = 0,
                [InvitationDeliveryStatus.Joined] = 0,
                [InvitationDeliveryStatus.Expired] = 0,
                [InvitationDeliveryStatus.Revoked] = 0,
            };
            foreach (var i in invitations)
            {
                var index = joinIndexByCampaign.TryGetValue(i.CampaignId, out var ix) ? ix : emptyJoinIndex;
                var joined = index.TryFind(i.Id, i.CampaignCandidateId, i.Email, out _);
                var status = CampaignResultRules.ResolveDeliveryStatus(i.RevokedAt, joined, i.ExpiresAt, i.EmailSentAt, now);
                invitationStatus[status]++;
            }
            response.Invitations = new CampaignAnalyticsInvitations
            {
                Total = invitations.Count,
                Queued = invitationStatus[InvitationDeliveryStatus.Queued],
                Sent = invitationStatus[InvitationDeliveryStatus.Sent],
                Joined = invitationStatus[InvitationDeliveryStatus.Joined],
                Expired = invitationStatus[InvitationDeliveryStatus.Expired],
                Revoked = invitationStatus[InvitationDeliveryStatus.Revoked],
            };

            // ── interviews — kết luận Pass/Fail CÙNG luật với GET /results ────────────
            var scoredSessions = new HashSet<Guid>(rankings.Select(r => r.SessionId));
            var verdicts = rankings.Select(r =>
            {
                var effective = r.OverrideScore ?? r.TotalScore;
                var belowCutoff = CampaignResultRules.ComputeBelowCutoff(
                    r.ScoringInputs,
                    criteriaByCampaign.TryGetValue(r.CampaignId, out var crit) ? crit : new List<CampaignResultRules.CutoffCriterion>());
                var result = CampaignResultRules.ResolveResult(
                    r.OverrideResult, belowCutoff.Count,
                    passScoreByCampaign.TryGetValue(r.CampaignId, out var pct) ? pct : null,
                    effective);
                return new Verdict(r.CampaignId, effective, result, r.UpdatedAt);
            }).ToList();

            var interviews = response.Interviews;
            interviews.Joined = memberships.Count;
            interviews.Started = memberships.Count(m => m.SessionId is not null);
            interviews.InProgress = memberships.Count(m => m.InterviewStatus == InterviewProgressStatus.InProgress);
            interviews.Completed = memberships.Count(m => m.InterviewStatus == InterviewProgressStatus.Completed);
            interviews.Scored = rankings.Count;
            interviews.PendingScore = memberships.Count(m =>
                m.SessionId is Guid sid && m.InterviewStatus == InterviewProgressStatus.Completed && !scoredSessions.Contains(sid));
            interviews.Passed = verdicts.Count(v => v.Result == "Pass");
            interviews.Failed = verdicts.Count(v => v.Result == "Fail");
            interviews.Undetermined = verdicts.Count(v => v.Result is null);
            var effectiveScores = verdicts.Select(v => v.Effective).ToList();
            interviews.MedianScore = Median(effectiveScores);
            interviews.ScoreDistribution = BandCounts(effectiveScores);
            interviews.FlagsBySignal = flags
                .GroupBy(f => f.SignalType, StringComparer.Ordinal)
                .Select(g => new SignalCount { SignalType = g.Key, Count = g.Count() })
                .OrderByDescending(x => x.Count)
                .ThenBy(x => x.SignalType, StringComparer.Ordinal)
                .ToList();

            // ── buckets — dòng chảy trong [from, to), mỗi cột neo đúng mốc của nó ────
            var g = period.Granularity;
            bool InPeriod(DateTime t) => t >= period.FromUtc && t < period.ToUtc;
            var entries = campaigns.Where(c => InPeriod(c.CreatedAt))
                    .Select(c => (Key: AnalyticsPeriod.BucketKey(c.CreatedAt, g), Campaigns: 1, Sent: 0, Joins: 0, Started: 0, Scored: 0))
                .Concat(invitations.Where(i => i.EmailSentAt is DateTime sentAt && InPeriod(sentAt))
                    .Select(i => (Key: AnalyticsPeriod.BucketKey(i.EmailSentAt!.Value, g), Campaigns: 0, Sent: 1, Joins: 0, Started: 0, Scored: 0)))
                .Concat(memberships.Where(m => m.JoinedAt is DateTime joinedAt && InPeriod(joinedAt))
                    .Select(m => (Key: AnalyticsPeriod.BucketKey(m.JoinedAt!.Value, g), Campaigns: 0, Sent: 0, Joins: 1, Started: 0, Scored: 0)))
                .Concat(memberships.Where(m => m.InterviewStartedAt is DateTime startedAt && InPeriod(startedAt))
                    .Select(m => (Key: AnalyticsPeriod.BucketKey(m.InterviewStartedAt!.Value, g), Campaigns: 0, Sent: 0, Joins: 0, Started: 1, Scored: 0)))
                .Concat(verdicts.Where(v => InPeriod(v.UpdatedAt))
                    .Select(v => (Key: AnalyticsPeriod.BucketKey(v.UpdatedAt, g), Campaigns: 0, Sent: 0, Joins: 0, Started: 0, Scored: 1)));
            response.Buckets = entries
                .GroupBy(e => e.Key)
                .OrderBy(b => b.Key)
                .Select(b => new CampaignAnalyticsBucket
                {
                    PeriodStart = AnalyticsPeriod.BucketStart(b.Key, g),
                    CampaignsCreated = b.Sum(e => e.Campaigns),
                    InvitationsSent = b.Sum(e => e.Sent),
                    Joins = b.Sum(e => e.Joins),
                    InterviewsStarted = b.Sum(e => e.Started),
                    Scored = b.Sum(e => e.Scored),
                })
                .ToList();

            // ── perCampaign — cùng định nghĩa cột như các khối trên, mới nhất trước ────
            var invitationsByCampaign = invitations.GroupBy(i => i.CampaignId).ToDictionary(x => x.Key, x => x.Count());
            var membershipsByCampaign = memberships.GroupBy(m => m.CampaignId)
                .ToDictionary(x => x.Key, x => (Joined: x.Count(), Started: x.Count(m => m.SessionId is not null)));
            var verdictsByCampaign = verdicts.GroupBy(v => v.CampaignId).ToDictionary(x => x.Key, x => x.ToList());
            response.PerCampaign = campaigns
                .OrderByDescending(c => c.CreatedAt)
                .ThenByDescending(c => c.Id)
                .Select(c =>
                {
                    var m = membershipsByCampaign.TryGetValue(c.Id, out var mm) ? mm : (Joined: 0, Started: 0);
                    var v = verdictsByCampaign.TryGetValue(c.Id, out var vv) ? vv : new List<Verdict>();
                    return new CampaignAnalyticsPerCampaign
                    {
                        CampaignId = c.Id,
                        Title = c.Title,
                        Status = c.Status.ToString(),
                        CreatedAt = c.CreatedAt,
                        Invited = invitationsByCampaign.TryGetValue(c.Id, out var inv) ? inv : 0,
                        Joined = m.Joined,
                        Started = m.Started,
                        Scored = v.Count,
                        Passed = v.Count(x => x.Result == "Pass"),
                        MedianScore = Median(v.Select(x => x.Effective).ToList()),
                    };
                })
                .ToList();

            return response;
        }

        /// <summary>Median: lẻ ⇒ phần tử giữa; chẵn ⇒ trung bình 2 phần tử giữa; làm tròn 2 chữ số; rỗng ⇒ null (KHÔNG phải 0).</summary>
        internal static decimal? Median(IReadOnlyList<decimal> values)
        {
            if (values.Count == 0) return null;
            var sorted = values.OrderBy(v => v).ToList();
            var mid = sorted.Count / 2;
            var median = sorted.Count % 2 == 1
                ? sorted[mid]
                : (sorted[mid - 1] + sorted[mid]) / 2m;
            return Math.Round(median, 2, MidpointRounding.AwayFromZero);
        }

        /// <summary>Band điểm: biên dưới bao gồm (`<` ở mọi ngưỡng), `100` rơi vào band cuối. Luôn trả đủ 5 band đúng thứ tự.</summary>
        internal static List<BandCount> BandCounts(IReadOnlyList<decimal> scores)
        {
            var counts = new int[Bands.Length];
            foreach (var s in scores) counts[BandIndex(s)]++;
            return Bands.Select((b, i) => new BandCount { Band = b, Count = counts[i] }).ToList();
        }

        internal static int BandIndex(decimal score)
            => score < 20 ? 0
             : score < 40 ? 1
             : score < 60 ? 2
             : score < 80 ? 3
             : 4;

        /// <summary>
        /// Top kỹ năng từ <c>cv_submission.skills[]</c>: trim, so KHÔNG phân biệt hoa/thường, hiển thị dạng gặp
        /// đầu tiên; mỗi CV đếm một kỹ năng tối đa MỘT lần (danh sách có trùng không đội count); top 10 giảm
        /// dần theo count, hoà ⇒ theo tên A→Z.
        /// </summary>
        internal static List<SkillCount> TopSkills(IEnumerable<List<string>?> skillLists)
        {
            var counts = new Dictionary<string, (string Display, int Count)>(StringComparer.OrdinalIgnoreCase);
            foreach (var list in skillLists)
            {
                if (list is null) continue;
                var seenInCv = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var raw in list)
                {
                    if (string.IsNullOrWhiteSpace(raw)) continue;
                    var skill = raw.Trim();
                    if (!seenInCv.Add(skill)) continue;
                    counts[skill] = counts.TryGetValue(skill, out var cur)
                        ? (cur.Display, cur.Count + 1)
                        : (skill, 1);
                }
            }
            return counts.Values
                .OrderByDescending(x => x.Count)
                .ThenBy(x => x.Display, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.Display, StringComparer.Ordinal)
                .Take(TopSkillsLimit)
                .Select(x => new SkillCount { Skill = x.Display, Count = x.Count })
                .ToList();
        }
    }
}
