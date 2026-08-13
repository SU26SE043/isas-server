using System.Text.Json;
using Isas.CampaignService.DTOs;
using Isas.CampaignService.Models;
using Microsoft.EntityFrameworkCore;

namespace Isas.CampaignService.Services
{
    public interface IRubricPreviewService
    {
        Task<RubricPreviewRunResponse> RunAsync(
            Guid orgId, Guid actorUserId, Guid campaignId, RubricPreviewRequest request, CancellationToken ct);

        Task<List<RubricPreviewRunResponse>> GetHistoryAsync(Guid orgId, Guid campaignId, CancellationToken ct);
    }

    /// <summary>
    /// CAMP-19 — CHẤM THỬ: AI viết 3 bài mẫu cho một câu hỏi HR chọn rồi chấm chính chúng bằng thước đo
    /// ĐANG LƯU, để Employer thấy "6 điểm nghĩa là gì" trước khi phát link cho ứng viên thật.
    /// </summary>
    public class RubricPreviewService : IRubricPreviewService
    {
        private readonly CampaignDbContext _db;
        private readonly IRubricPreviewClient _ai;
        private readonly ICreditReservationClient? _credits;
        private readonly ILogger<RubricPreviewService> _logger;

        /// <summary>Số lượt THÀNH CÔNG miễn phí cho MỖI phiên bản thước đo.</summary>
        public const int FreeRunsPerRubricVersion = 3;

        /// <summary>Mục tiêu số từ chung cho cả 3 bài — khác biệt phải nằm ở CHẤT, không ở độ dài.</summary>
        private const int TargetWordCount = 160;

        /// <summary>
        /// Quá mốc này thì một row <c>Running</c> coi như mồ côi (tiến trình chết giữa lời gọi đồng bộ).
        /// Không self-heal thì UNIQUE có điều kiện sẽ khoá chết campaign đó ở 409 vĩnh viễn.
        /// </summary>
        private static readonly TimeSpan StaleRunningAfter = TimeSpan.FromMinutes(5);

        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        public RubricPreviewService(
            CampaignDbContext db, IRubricPreviewClient ai,
            ILogger<RubricPreviewService> logger, ICreditReservationClient? credits = null)
        {
            _db = db; _ai = ai; _logger = logger; _credits = credits;
        }

        public async Task<RubricPreviewRunResponse> RunAsync(
            Guid orgId, Guid actorUserId, Guid campaignId, RubricPreviewRequest request, CancellationToken ct)
        {
            // ⚠ TRẬT TỰ GUARD LÀ HỢP ĐỒNG (PAY-5): mọi guard chạy TRƯỚC ReserveAsync. Đảo một bước là
            // org bị trừ credit cho một request đằng nào cũng bị từ chối, và để lại chỗ giữ mồ côi.

            // ── 1. org sở hữu? ────────────────────────────────────────────
            var campaign = await _db.Campaigns
                .Include(c => c.Questions)
                .Include(c => c.Criteria).ThenInclude(cr => cr.Levels)
                .FirstOrDefaultAsync(c => c.Id == campaignId && c.OrgId == orgId, ct)
                ?? throw new KeyNotFoundException($"Campaign {campaignId} not found.");

            // ── 2. trạng thái ─────────────────────────────────────────────
            // Active vẫn chấm thử được — cho sửa thước mà không cho kiểm chứng là bắt HR sửa mù.
            if (campaign.Status is CampaignStatus.Closed or CampaignStatus.Archived)
                throw new InvalidOperationException(
                    $"Chiến dịch {campaign.Status} không chạy chấm thử được.");

            // ── 3. có tiêu chí? ───────────────────────────────────────────
            var criteria = campaign.Criteria.OrderBy(c => c.OrderNo).ToList();
            if (criteria.Count == 0)
                throw new ArgumentException("Chiến dịch chưa có tiêu chí chấm.");

            // ── 4. mốc hợp lệ? ────────────────────────────────────────────
            // Chấm thử là để kiểm chứng THANG ĐIỂM; không có mốc thì Interview dùng dải mặc định và
            // lượt chấm thử chẳng kiểm chứng được gì ngoài chính dải mặc định đó.
            var thieuMoc = criteria.Where(c => (c.Levels?.Count ?? 0) < 2).Select(c => c.Name).ToList();
            if (thieuMoc.Count > 0)
                throw new ArgumentException(
                    $"Chưa khai mốc điểm cho tiêu chí: {string.Join(", ", thieuMoc)}. "
                    + "Chấm thử cần mốc để kiểm chứng, nếu không nó chỉ đang kiểm chứng dải mặc định.");

            // ── 5. chọn được câu hỏi? ─────────────────────────────────────
            var question = SelectQuestion(campaign, request.QuestionId);

            // ── 6. còn lượt nào đang chạy? (self-heal row mồ côi) ─────────
            await ResolveStaleRunningAsync(campaignId, ct);
            if (await _db.RubricPreviewRuns.AnyAsync(
                    r => r.CampaignId == campaignId && r.Status == RubricPreviewStatus.Running, ct))
                throw new InvalidOperationException(
                    "Đang có một lượt chấm thử chạy cho chiến dịch này. Đợi nó xong rồi thử lại.");

            // ── 7. INSERT row Running TRƯỚC khi gọi AI ────────────────────
            // Có chủ đích: row này vừa là khoá chống double-click (UNIQUE có điều kiện) vừa là chỗ kết
            // quả rơi vào kể cả khi trình duyệt HR chết — reload là thấy trong lịch sử.
            var run = new RubricPreviewRun
            {
                Id = Guid.NewGuid(),
                CampaignId = campaignId,
                CreatedByUserId = actorUserId,
                QuestionId = question.Id,
                QuestionText = question.QuestionText,
                Status = RubricPreviewStatus.Running,
                Billed = false,
                RubricSnapshot = JsonSerializer.Serialize(BuildRubricView(criteria), Json),
                RubricFingerprint = RubricFingerprint.Compute(criteria),
                RubricVersion = campaign.RubricVersion,
                CreatedAt = DateTime.UtcNow
            };
            _db.RubricPreviewRuns.Add(run);
            try
            {
                await _db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                // Hai request vào cùng lúc: UNIQUE có điều kiện là trọng tài, không phải câu đọc ở bước 6.
                _db.Entry(run).State = EntityState.Detached;
                throw new InvalidOperationException(
                    "Đang có một lượt chấm thử chạy cho chiến dịch này. Đợi nó xong rồi thử lại.");
            }

            // ── 8. quota → reserve (LẦN ĐẦU chạm Payment) ─────────────────
            // Chỉ đếm Succeeded: phạt HR vì AI của ta hỏng là sai. Theo (campaign, rubric_version) vì
            // thước đo mới là bài toán mới — campaign chạy 6 tháng sửa thước 4 lần mà dùng chung quota
            // sẽ hết lượt ngay lần hai rồi quay về sửa mù.
            var succeeded = await CountSucceededAsync(campaignId, campaign.RubricVersion, ct);
            var billed = succeeded >= FreeRunsPerRubricVersion;
            if (billed)
            {
                if (_credits is null)
                    throw new InvalidOperationException("Credit client chưa được cấu hình.");
                try
                {
                    await _credits.ReserveAsync("Org", orgId, run.Id, ct);
                }
                catch
                {
                    await MarkFailedAsync(run, "Không giữ được credit cho lượt chấm thử.", ct);
                    throw;
                }
                run.Billed = true;
            }

            // ── 9-10. gọi AI rồi chốt trạng thái ──────────────────────────
            try
            {
                var result = await _ai.RunAsync(
                    string.IsNullOrWhiteSpace(campaign.Domain) ? "BE" : campaign.Domain!,
                    campaign.Language, campaign.Seniority,
                    question.QuestionText, question.SampleAnswer, request.CustomAnswer,
                    TargetWordCount, BuildPreviewCriteria(criteria), ct);

                var samples = BuildSamples(criteria, result.Samples);
                run.Samples = JsonSerializer.Serialize(samples, Json);
                run.PromptVersion = result.PromptVersion;
                run.LengthParityWarning = result.LengthParityWarning;
                run.Status = RubricPreviewStatus.Succeeded;
                run.CompletedAt = DateTime.UtcNow;
                await _db.SaveChangesAsync(ct);

                if (billed) await TryCreditOpAsync(() => _credits!.ConsumeAsync(run.Id, ct), "consume", run.Id);

                return ToResponse(run, await FreeRemainingAsync(campaignId, run.RubricVersion, ct));
            }
            catch (Exception ex)
            {
                await MarkFailedAsync(run, ex.Message, ct);
                if (billed) await TryCreditOpAsync(() => _credits!.ReleaseAsync(run.Id, ct), "release", run.Id);
                throw;
            }
        }

        public async Task<List<RubricPreviewRunResponse>> GetHistoryAsync(
            Guid orgId, Guid campaignId, CancellationToken ct)
        {
            if (!await _db.Campaigns.AnyAsync(c => c.Id == campaignId && c.OrgId == orgId, ct))
                throw new KeyNotFoundException($"Campaign {campaignId} not found.");

            var runs = await _db.RubricPreviewRuns
                .AsNoTracking()
                .Where(r => r.CampaignId == campaignId)
                .OrderByDescending(r => r.CreatedAt)
                .Take(20)
                .ToListAsync(ct);

            var free = runs.Count == 0
                ? FreeRunsPerRubricVersion
                : await FreeRemainingAsync(campaignId, runs[0].RubricVersion, ct);

            return runs.Select(r => ToResponse(r, free)).ToList();
        }

        // ── helpers ───────────────────────────────────────────────────────

        private static CampaignQuestion SelectQuestion(Campaign campaign, Guid? questionId)
        {
            var pool = campaign.Questions.OrderBy(q => q.CreatedAt).ThenBy(q => q.Id).ToList();
            if (pool.Count == 0)
                throw new ArgumentException("Chiến dịch chưa có câu hỏi để chấm thử.");

            if (questionId is null) return pool[0];

            return pool.FirstOrDefault(q => q.Id == questionId)
                ?? throw new ArgumentException("Câu hỏi không thuộc chiến dịch này.");
        }

        private async Task ResolveStaleRunningAsync(Guid campaignId, CancellationToken ct)
        {
            var cutoff = DateTime.UtcNow - StaleRunningAfter;
            var stale = await _db.RubricPreviewRuns
                .Where(r => r.CampaignId == campaignId
                            && r.Status == RubricPreviewStatus.Running
                            && r.CreatedAt < cutoff)
                .ToListAsync(ct);
            if (stale.Count == 0) return;

            foreach (var r in stale)
            {
                r.Status = RubricPreviewStatus.Failed;
                r.ErrorReason = "Lượt chấm thử không kết thúc (tiến trình dừng giữa chừng).";
                r.CompletedAt = DateTime.UtcNow;
            }
            await _db.SaveChangesAsync(ct);
            _logger.LogWarning("Dọn {Count} lượt chấm thử mồ côi của campaign {CampaignId}", stale.Count, campaignId);
        }

        private Task<int> CountSucceededAsync(Guid campaignId, int rubricVersion, CancellationToken ct)
            => _db.RubricPreviewRuns.CountAsync(
                r => r.CampaignId == campaignId
                     && r.RubricVersion == rubricVersion
                     && r.Status == RubricPreviewStatus.Succeeded, ct);

        private async Task<int> FreeRemainingAsync(Guid campaignId, int rubricVersion, CancellationToken ct)
            => Math.Max(0, FreeRunsPerRubricVersion - await CountSucceededAsync(campaignId, rubricVersion, ct));

        private async Task MarkFailedAsync(RubricPreviewRun run, string reason, CancellationToken ct)
        {
            run.Status = RubricPreviewStatus.Failed;
            // Cắt để một stack trace dài không nuốt cả cột.
            run.ErrorReason = reason.Length > 500 ? reason[..500] : reason;
            run.CompletedAt = DateTime.UtcNow;
            try
            {
                await _db.SaveChangesAsync(ct);
            }
            catch (Exception ex)
            {
                // Không được nuốt lỗi gốc bằng một lỗi ghi DB — row mồ côi đã có self-heal 5 phút lo.
                _logger.LogError(ex, "Không ghi được trạng thái Failed cho lượt chấm thử {RunId}", run.Id);
            }
        }

        // Consume/release là best-effort: lỗi ở đây KHÔNG được lật ngược kết quả HR đã trả tiền để có.
        // Chỗ giữ treo lại thuộc phạm vi reconciler bên Payment.
        private async Task TryCreditOpAsync(Func<Task> op, string name, Guid runId)
        {
            try { await op(); }
            catch (Exception ex) { _logger.LogError(ex, "Credit {Op} lỗi cho lượt chấm thử {RunId}", name, runId); }
        }

        private static List<RubricPreviewCriterion> BuildRubricView(List<CampaignCriterion> criteria)
            => criteria.Select(c => new RubricPreviewCriterion
            {
                CriterionId = c.Id,
                Name = c.Name,
                Weight = c.Weight,
                MaxScore = c.MaxScore,
                Levels = SortedLevels(c)
                    .Select(l => new CriterionLevelResponse { Score = l.Score, Descriptor = l.Descriptor })
                    .ToList()
            }).ToList();

        private static List<CampaignCriterionLevel> SortedLevels(CampaignCriterion c)
            => (c.Levels ?? new List<CampaignCriterionLevel>()).OrderBy(l => l.Score).ToList();

        /// <summary>
        /// MỨC KỲ VỌNG do CODE chọn, không phải model tự đặt — đó là cả điểm mấu chốt: có mức biết
        /// trước thì mới so được "kỳ vọng vs thật" và đo được độ chệch tự-khen-văn-mình.
        /// </summary>
        internal static (int Weak, int Good, int Excellent) ExpectedLevels(IReadOnlyList<CampaignCriterionLevel> sorted)
        {
            var n = sorted.Count;
            return (sorted[n / 4].Score, sorted[Math.Min(n - 1, (int)(n * 0.6))].Score, sorted[n - 1].Score);
        }

        private static List<PreviewCriterionInput> BuildPreviewCriteria(List<CampaignCriterion> criteria)
            => criteria.Select(c =>
            {
                var sorted = SortedLevels(c);
                var (weak, good, excellent) = ExpectedLevels(sorted);
                return new PreviewCriterionInput(
                    c.Id, c.Name, c.Description, c.MaxScore, c.Weight,
                    sorted.Select(l => new SessionCriterionLevelInput(l.Score, l.Descriptor)).ToList(),
                    weak, good, excellent);
            }).ToList();

        private static List<RubricPreviewSample> BuildSamples(
            List<CampaignCriterion> criteria, IReadOnlyList<PreviewSample> samples)
        {
            var byId = criteria.ToDictionary(c => c.Id);

            return samples.Select(s =>
            {
                var scores = new List<RubricPreviewSampleScore>();
                decimal expectedPct = 0, actualPct = 0;

                foreach (var c in criteria)
                {
                    var sorted = SortedLevels(c);
                    var (weak, good, excellent) = ExpectedLevels(sorted);
                    var expected = s.Band switch
                    {
                        "Weak" => weak,
                        "Good" => good,
                        "Excellent" => excellent,
                        _ => good   // bài HR tự dán: không có kỳ vọng riêng, neo ở mức giữa
                    };

                    var actual = s.Scores.FirstOrDefault(x => x.CriterionId == c.Id)?.Score ?? 0m;
                    var matched = s.Scores.FirstOrDefault(x => x.CriterionId == c.Id)?.LevelMatched;

                    scores.Add(new RubricPreviewSampleScore
                    {
                        CriterionId = c.Id,
                        CriterionName = c.Name,
                        MaxScore = c.MaxScore,
                        ExpectedLevel = expected,
                        ActualScore = actual,
                        LevelMatched = matched,
                        Reasoning = s.Scores.FirstOrDefault(x => x.CriterionId == c.Id)?.Reasoning
                    });

                    if (c.MaxScore > 0)
                    {
                        expectedPct += expected / (decimal)c.MaxScore * c.Weight * 100m;
                        actualPct += actual / c.MaxScore * c.Weight * 100m;
                    }
                }

                return new RubricPreviewSample
                {
                    Band = s.Band,
                    AnswerText = s.AnswerText,
                    WordCount = s.WordCount,
                    ExpectedWeightedPct = Math.Round(expectedPct, 2),
                    ActualWeightedPct = Math.Round(actualPct, 2),
                    Scores = scores
                };
            }).ToList();
        }

        private static RubricPreviewRunResponse ToResponse(RubricPreviewRun run, int freeRemaining)
            => new()
            {
                Id = run.Id,
                Status = run.Status.ToString(),
                QuestionId = run.QuestionId,
                QuestionText = run.QuestionText,
                RubricFingerprint = run.RubricFingerprint,
                RubricVersion = run.RubricVersion,
                PromptVersion = run.PromptVersion,
                // v1: bài mẫu là văn bản ⇒ không có số đo cách nói (F11). Băng cảnh báo trên FE đọc cờ này.
                DeliveryMetricsAvailable = false,
                LengthParityWarning = run.LengthParityWarning,
                Billed = run.Billed,
                FreeRunsRemaining = freeRemaining,
                Rubric = Deserialize<List<RubricPreviewCriterion>>(run.RubricSnapshot) ?? new(),
                Samples = Deserialize<List<RubricPreviewSample>>(run.Samples) ?? new(),
                ErrorReason = run.ErrorReason,
                CreatedAt = run.CreatedAt,
                CompletedAt = run.CompletedAt
            };

        private static T? Deserialize<T>(string? json) where T : class
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try { return JsonSerializer.Deserialize<T>(json, Json); }
            catch (JsonException) { return null; }
        }
    }
}
