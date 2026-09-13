using System.Text.Json;
using Isas.CampaignService.DTOs;
using Isas.CampaignService.Models;
using Isas.Shared.Rubric;
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

        /// <summary>
        /// SC2 · T6 (D-4) — số lượt THÀNH CÔNG miễn phí cho MỖI (campaign, phiên bản thước đo, CÂU HỎI).
        /// Trước: 3 lượt/(campaign, version) dùng chung mọi câu — nay chấm thử chấm THEO PHẠM VI CÂU nên
        /// mỗi câu là một bài toán riêng: HR kiểm được từng câu một lượt miễn phí, câu B không ăn quota
        /// của câu A. Đổi hằng này = đổi D-4, không phải chuyện kỹ thuật.
        /// </summary>
        public const int FreeRunsPerQuestion = 1;

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

            // ── 4. chọn được câu hỏi? ─────────────────────────────────────
            // (SC2 · T6: đứng TRƯỚC guard mốc vì phạm vi tiêu chí phụ thuộc vào CÂU — cả hai vẫn là
            // 400 và vẫn TRƯỚC ReserveAsync, trật tự guard-trước-tiền không đổi.)
            var question = SelectQuestion(campaign, request.QuestionId);

            // ── 4b. PHẠM VI CHẤM của câu (SC2 · T6, I6: chấm thử = chấm thật) ─────
            // Ứng viên trả lời câu Q bị chấm trên: tiêu chí Always ∪ tiêu chí Q nhắm tới (INT-18). Chấm
            // thử phải dùng ĐÚNG tập đó, nếu không HR kiểm chứng một thước mà ứng viên bị đo bằng thước
            // khác. null = chưa gắn nhãn ⇒ TOÀN BỘ (I2: null ≠ [] — [] ⇒ chỉ Always).
            var scopedIds = ScopeFor(criteria, question.TargetCriterionIds);
            var scoped = criteria.Where(c => scopedIds.Contains(c.Id)).ToList();
            if (scoped.Count == 0)
                throw new ArgumentException(
                    "Câu hỏi này không nhắm tiêu chí nội dung nào và chiến dịch không có tiêu chí "
                    + "chấm-mọi-câu (Always) ⇒ không có tiêu chí nào để chấm thử. Gắn nhãn cho câu hoặc "
                    + "đặt ít nhất một tiêu chí scoringScope = Always.");

            // ── 5. mốc hợp lệ (TRONG phạm vi)? ────────────────────────────
            // Chấm thử là để kiểm chứng THANG ĐIỂM; không có mốc thì Interview dùng dải mặc định và
            // lượt chấm thử chẳng kiểm chứng được gì ngoài chính dải mặc định đó. Chỉ đòi mốc trên tiêu
            // chí SẼ ĐƯỢC CHẤM cho câu này — tiêu chí ngoài phạm vi thiếu mốc không chặn lượt này.
            var thieuMoc = scoped.Where(c => (c.Levels?.Count ?? 0) < 2).Select(c => c.Name).ToList();
            if (thieuMoc.Count > 0)
                throw new ArgumentException(
                    $"Chưa khai mốc điểm cho tiêu chí: {string.Join(", ", thieuMoc)}. "
                    + "Chấm thử cần mốc để kiểm chứng, nếu không nó chỉ đang kiểm chứng dải mặc định.");

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
                // SC2 · T6 — snapshot ghi ĐỦ bộ (HR vẫn thấy thước đo) + đánh dấu InScope; không migration.
                RubricSnapshot = JsonSerializer.Serialize(BuildRubricView(criteria, scopedIds), Json),
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
            // Chỉ đếm Succeeded: phạt HR vì AI của ta hỏng là sai. Theo (campaign, rubric_version,
            // question) — SC2 · T6 / D-4: thước đo mới là bài toán mới, và từ khi chấm theo phạm vi câu
            // thì mỗi câu cũng là một bài toán mới (tập tiêu chí khác). Lượt của câu A không ăn quota
            // của câu B.
            var succeeded = await CountSucceededAsync(campaignId, campaign.RubricVersion, question.Id, ct);
            var billed = succeeded >= FreeRunsPerQuestion;
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
                    // I6 — CÙNG ScoringCriteriaBuilder.Build, chỉ khác TẬP tiêu chí gửi (= phạm vi câu).
                    TargetWordCount, BuildPreviewCriteria(scoped), ct);

                var samples = BuildSamples(scoped, result.Samples);
                run.Samples = JsonSerializer.Serialize(samples, Json);
                run.PromptVersion = result.PromptVersion;
                run.LengthParityWarning = result.LengthParityWarning;
                run.Status = RubricPreviewStatus.Succeeded;
                run.CompletedAt = DateTime.UtcNow;
                await _db.SaveChangesAsync(ct);

                if (billed) await TryCreditOpAsync(() => _credits!.ConsumeAsync(run.Id, ct), "consume", run.Id);

                return ToResponse(run, await FreeRemainingAsync(campaignId, run.RubricVersion, run.QuestionId, ct));
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

            // SC2 · T6 — freeRunsRemaining tính theo ĐÚNG (RubricVersion, QuestionId) của TỪNG run, không
            // dùng chung số của runs[0]: FE nhóm lịch sử theo câu và hiện "còn N lượt miễn phí" theo câu.
            // Đếm Succeeded của cả campaign trong MỘT truy vấn rồi GroupBy trong bộ nhớ.
            var succeededByKey = (await _db.RubricPreviewRuns
                    .AsNoTracking()
                    .Where(r => r.CampaignId == campaignId && r.Status == RubricPreviewStatus.Succeeded)
                    .Select(r => new { r.RubricVersion, r.QuestionId })
                    .ToListAsync(ct))
                .GroupBy(x => (x.RubricVersion, x.QuestionId))
                .ToDictionary(g => g.Key, g => g.Count());

            return runs.Select(r => ToResponse(r,
                Math.Max(0, FreeRunsPerQuestion - succeededByKey.GetValueOrDefault((r.RubricVersion, r.QuestionId)))))
                .ToList();
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

        /// <summary>
        /// SC2 · T6 — phạm vi chấm của một câu: <c>Always</c> ∪ {id ∈ nhãn câu}. <c>null</c> (chưa gắn nhãn)
        /// ⇒ TOÀN BỘ tiêu chí; <c>[]</c> (đã xét, không nhắm) ⇒ chỉ <c>Always</c>. Khớp luật INT-18 mà
        /// Interview áp khi chấm ứng viên thật — I6.
        /// </summary>
        internal static HashSet<Guid> ScopeFor(IReadOnlyList<CampaignCriterion> criteria, IReadOnlyList<Guid>? targetCriterionIds)
        {
            if (targetCriterionIds is null)
                return criteria.Select(c => c.Id).ToHashSet();
            var targets = targetCriterionIds.ToHashSet();
            return criteria
                .Where(c => c.ScoringScope == CriterionScoringScope.Always || targets.Contains(c.Id))
                .Select(c => c.Id)
                .ToHashSet();
        }

        // SC2 · T6 — lọc thêm QuestionId: quota là của (campaign, version, CÂU). Row cũ trước T6 có
        // QuestionId luôn resolved (từ CAMP-19) nên không có row nào rơi ra ngoài phép đếm.
        private Task<int> CountSucceededAsync(Guid campaignId, int rubricVersion, Guid? questionId, CancellationToken ct)
            => _db.RubricPreviewRuns.CountAsync(
                r => r.CampaignId == campaignId
                     && r.RubricVersion == rubricVersion
                     && r.QuestionId == questionId
                     && r.Status == RubricPreviewStatus.Succeeded, ct);

        private async Task<int> FreeRemainingAsync(Guid campaignId, int rubricVersion, Guid? questionId, CancellationToken ct)
            => Math.Max(0, FreeRunsPerQuestion - await CountSucceededAsync(campaignId, rubricVersion, questionId, ct));

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

        /// <summary>
        /// Snapshot ĐỦ bộ tiêu chí (HR vẫn nhìn thấy cả thước đo) kèm <c>InScope</c> theo phạm vi câu
        /// (SC2 · T6). Row cũ trước T6 không có hai trường mới ⇒ deserialize ra <c>ScoringScope = null</c>,
        /// <c>InScope = null</c> ⇒ <see cref="ToResponse"/> coi là in-scope (I5: lượt cũ = chấm toàn bộ).
        /// </summary>
        private static List<RubricPreviewCriterion> BuildRubricView(List<CampaignCriterion> criteria, IReadOnlySet<Guid> scopedIds)
            => criteria.Select(c => new RubricPreviewCriterion
            {
                CriterionId = c.Id,
                Name = c.Name,
                Weight = c.Weight,
                MaxScore = c.MaxScore,
                ScoringScope = c.ScoringScope.ToString(),
                InScope = scopedIds.Contains(c.Id),
                Levels = SortedLevels(c)
                    .Select(l => new CriterionLevelResponse { Score = l.Score, Descriptor = l.Descriptor })
                    .ToList()
            }).ToList();

        private static List<CampaignCriterionLevel> SortedLevels(CampaignCriterion c)
            => (c.Levels ?? new List<CampaignCriterionLevel>()).OrderBy(l => l.Score).ToList();

        /// <summary>
        /// MỨC KỲ VỌNG do CODE chọn, không phải model tự đặt — đó là cả điểm mấu chốt: có mức biết
        /// trước thì mới so được "kỳ vọng vs thật" và đo được độ chệch tự-khen-văn-mình.
        ///
        /// <para>Phép chọn nằm ở <see cref="Isas.Shared.Rubric.ExpectedLevels"/> vì chấm thử chạy ở
        /// HAI chỗ (employer kiểm thước campaign · admin kiểm bộ chuẩn B2C). Mỗi bên tự chọn mức kỳ
        /// vọng thì hai báo cáo "kỳ vọng vs thật" đo hai thứ khác nhau mà trông giống hệt.</para>
        /// </summary>
        internal static (int Weak, int Good, int Excellent) ExpectedLevels(IReadOnlyList<CampaignCriterionLevel> sorted)
            => Isas.Shared.Rubric.ExpectedLevels.For(
                sorted.Select(l => new RubricLevelSnapshot(l.Score, l.Descriptor)).ToList());

        /// <summary>
        /// 🔴 Bộ tiêu chí gửi đi CHẤM THỬ phải dựng từ CHÍNH <see cref="ScoringCriteriaBuilder"/> —
        /// cùng hàm mà đường CHẤM THẬT dùng. Cả tính năng đứng trên lời hứa "thứ HR kiểm chứng chính
        /// là thứ ứng viên bị chấm"; tự sort/tự map ở đây là mở đúng cái khe để hai đường trôi xa nhau
        /// mà KHÔNG có triệu chứng nào (cả hai vẫn ra điểm, chỉ là điểm của hai thước đo khác nhau).
        /// Phần riêng của chấm thử chỉ là mức kỳ vọng — bọc THÊM lên trên, không dựng lại.
        /// </summary>
        internal static List<PreviewCriterionInput> BuildPreviewCriteria(List<CampaignCriterion> criteria)
        {
            var shared = ScoringCriteriaBuilder.Build(criteria);
            var byName = criteria.ToDictionary(c => c.Name, StringComparer.Ordinal);

            return shared.Select(s =>
            {
                var c = byName[s.Name];
                var (weak, good, excellent) = ExpectedLevels(SortedLevels(c));
                return new PreviewCriterionInput(
                    c.Id, s.Name, s.Description, s.MaxScore, s.Weight,
                    s.Levels,   // ĐÚNG mảng mà đường chấm thật gửi đi, không phải bản dựng lại
                    weak, good, excellent);
            }).ToList();
        }

        /// <summary>
        /// Điểm tổng % của một bài mẫu — MIRROR công thức weighted của đường chấm thật
        /// (<c>Isas.InterviewService/Services/SessionScoringNotifier.cs:325-354</c>): chuẩn % từng tiêu chí
        /// (kẹp 0..100) rồi <c>Σ(pct×w) / Σw</c>, mẫu số chỉ gồm tiêu chí THỰC SỰ CÓ ĐIỂM (tiêu chí AI bỏ
        /// không tính vào mẫu số — y như tiêu chí không ai hỏi rơi khỏi mẫu số ở INT-18).
        /// <para>⚠ Correction T6: trước T6 tập gửi = toàn bộ và C12 ép Σw = 1 nên "Σ(pct×w×100)" tương đương;
        /// sau T6 <paramref name="criteria"/> là PHẠM VI CÂU (Σw &lt; 1) ⇒ thiếu phép chia thì câu nhắm W1
        /// (Σw = 0.7) chấm 5/5 mọi tiêu chí ra 70 trong khi ứng viên thật được 100 — FE so ngưỡng tuyệt đối
        /// (DISCRIMINATION_RANGE_PCT / BIAS_DELTA_PCT / passScorePct) nên verdict oan cho MỌI lượt scoped.</para>
        /// </summary>
        private static List<RubricPreviewSample> BuildSamples(
            List<CampaignCriterion> criteria, IReadOnlyList<PreviewSample> samples)
        {
            var byId = criteria.ToDictionary(c => c.Id);

            return samples.Select(s =>
            {
                var scores = new List<RubricPreviewSampleScore>();
                decimal expectedSum = 0, expectedWeightSum = 0, actualSum = 0, actualWeightSum = 0;

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

                    var aiScore = s.Scores.FirstOrDefault(x => x.CriterionId == c.Id);
                    var actual = aiScore?.Score ?? 0m;
                    var matched = aiScore?.LevelMatched;

                    scores.Add(new RubricPreviewSampleScore
                    {
                        CriterionId = c.Id,
                        CriterionName = c.Name,
                        MaxScore = c.MaxScore,
                        ExpectedLevel = expected,
                        ActualScore = actual,
                        LevelMatched = matched,
                        Reasoning = aiScore?.Reasoning
                    });

                    if (c.MaxScore <= 0) continue;   // phòng chia 0 (ràng buộc maxScore ≥ 1)

                    // Kỳ vọng do CODE chọn ⇒ luôn có ⇒ mọi tiêu chí trong phạm vi vào mẫu số.
                    expectedSum += Math.Clamp(expected / (decimal)c.MaxScore * 100m, 0m, 100m) * c.Weight;
                    expectedWeightSum += c.Weight;

                    // Thật: chỉ tiêu chí AI CÓ trả điểm (mirror notifier `TryGetValue … continue`).
                    if (aiScore is null) continue;
                    actualSum += Math.Clamp(actual / c.MaxScore * 100m, 0m, 100m) * c.Weight;
                    actualWeightSum += c.Weight;
                }

                return new RubricPreviewSample
                {
                    Band = s.Band,
                    AnswerText = s.AnswerText,
                    WordCount = s.WordCount,
                    ExpectedWeightedPct = expectedWeightSum <= 0m ? 0m : Math.Round(expectedSum / expectedWeightSum, 2),
                    ActualWeightedPct = actualWeightSum <= 0m ? 0m : Math.Round(actualSum / actualWeightSum, 2),
                    Scores = scores
                };
            }).ToList();
        }

        private static RubricPreviewRunResponse ToResponse(RubricPreviewRun run, int freeRemaining)
        {
            var rubric = Deserialize<List<RubricPreviewCriterion>>(run.RubricSnapshot) ?? new();
            return new()
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
                Rubric = rubric,
                // SC2 · T6 — tập ĐÃ CHẤM. InScope null (row trước T6) ⇒ in-scope: lượt cũ chấm toàn bộ (I5).
                ScopedCriterionIds = rubric.Where(c => c.InScope != false).Select(c => c.CriterionId).ToList(),
                Samples = Deserialize<List<RubricPreviewSample>>(run.Samples) ?? new(),
                ErrorReason = run.ErrorReason,
                CreatedAt = run.CreatedAt,
                CompletedAt = run.CompletedAt
            };
        }

        private static T? Deserialize<T>(string? json) where T : class
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try { return JsonSerializer.Deserialize<T>(json, Json); }
            catch (JsonException) { return null; }
        }
    }
}
