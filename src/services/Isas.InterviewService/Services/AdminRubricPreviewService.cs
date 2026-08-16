using System.Text.Json;
using Isas.InterviewService.ApplicationDbContext;
using Isas.InterviewService.Data;
using Isas.InterviewService.DTOs;
using Isas.InterviewService.Entities;
using Isas.InterviewService.Enums;
using Isas.Shared.Rubric;
using Microsoft.EntityFrameworkCore;

namespace Isas.InterviewService.Services;

/// <summary>Hết lượt chấm thử miễn phí của một phiên bản thước đo. Controller map thành 429.</summary>
public class PreviewQuotaExceededException(string message) : Exception(message);

public interface IAdminRubricPreviewService
{
    Task<AdminRubricPreviewRunResponse> RunAsync(
        Guid actorUserId, JobCategory jobCategory, string? language,
        AdminRubricPreviewRequest request, CancellationToken ct = default);

    Task<IReadOnlyList<AdminRubricPreviewRunResponse>> HistoryAsync(
        JobCategory jobCategory, string? language, CancellationToken ct = default);

    /// <summary>AI gợi ý mốc cho cả bộ. KHÔNG ghi DB — admin xem/sửa rồi lưu qua đúng một cửa PUT.</summary>
    Task<AdminSuggestLevelsResponse> SuggestLevelsAsync(
        JobCategory jobCategory, string? language, string? seniority, CancellationToken ct = default);
}

/// <summary>
/// CHẤM THỬ bộ chuẩn B2C: AI viết 3 bài mẫu cho một câu hỏi rồi chấm chính chúng bằng thước đo ĐANG
/// LƯU, để người soạn thấy "3 điểm khác 4 điểm ở chỗ nào" trước khi thước đó áp cho mọi người luyện.
///
/// <para><b>Đặt ở Interview, không nới bảng của Campaign</b> — AUTH-7 (endpoint admin nằm trong service
/// sở hữu dữ liệu) và vì ở đây dùng được <see cref="ScoringCriteriaBuilder"/> của chính Interview,
/// tức đúng hàm sinh ra mảng mà đường chấm THẬT gửi đi.</para>
///
/// <para><b>Miễn phí + trần cứng</b>, không đụng PaymentService: admin không có ví, mà dựng một "ví hệ
/// thống" giả sẽ đẻ ra <c>owner_type</c> mới xuyên PaymentService cho một tính năng nội bộ. Hết trần
/// → 429 kèm gợi ý "sửa mốc rồi lưu thì có lượt mới" — đúng hành vi ta muốn.</para>
/// </summary>
public class AdminRubricPreviewService(
    InterviewDbContext db,
    IRubricPreviewClient ai,
    IAiServiceLevelSuggester? levelSuggester,
    ILogger<AdminRubricPreviewService> logger) : IAdminRubricPreviewService
{
    /// <summary>Số lượt THÀNH CÔNG miễn phí cho MỖI (nghề, ngôn ngữ, phiên bản thước đo).</summary>
    public const int FreeRunsPerRubricVersion = 5;

    /// <summary>Mục tiêu số từ chung cho cả 3 bài — khác biệt phải nằm ở CHẤT, không ở độ dài.</summary>
    private const int TargetWordCount = 160;

    /// <summary>
    /// Quá mốc này thì một row <c>Running</c> coi như mồ côi (tiến trình chết giữa lời gọi đồng bộ).
    /// Không self-heal thì UNIQUE có điều kiện sẽ khoá chết phạm vi đó ở 409 vĩnh viễn.
    /// </summary>
    private static readonly TimeSpan StaleRunningAfter = TimeSpan.FromMinutes(5);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<AdminRubricPreviewRunResponse> RunAsync(
        Guid actorUserId, JobCategory jobCategory, string? language,
        AdminRubricPreviewRequest request, CancellationToken ct = default)
    {
        var lang = ValidateLanguage(language);

        // ── 1. có bộ tiêu chí đang hiệu lực? ──────────────────────────────────
        var criteria = await LoadActiveSetAsync(jobCategory, lang, ct);
        if (criteria.Count == 0)
            throw new KeyNotFoundException($"Chưa có bộ chuẩn cho {jobCategory} ({lang}).");
        var rubricVersion = criteria[0].Version;

        // ── 2. mốc hợp lệ? ────────────────────────────────────────────────────
        // Chấm thử là để kiểm chứng THANG ĐIỂM. Không có mốc thì đường chấm dùng dải mặc định và lượt
        // chấm thử chẳng kiểm chứng được gì ngoài chính dải mặc định đó — tức đốt một lượt AI để xác
        // nhận hiện trạng mà ta đang tìm cách thoát ra.
        var thieuMoc = criteria.Where(c => c.Levels.Count < 2).Select(c => c.Name).ToList();
        if (thieuMoc.Count > 0)
            throw new InvalidOperationException(
                $"Chưa khai mốc điểm cho tiêu chí: {string.Join(", ", thieuMoc)}. "
                + "Chấm thử cần mốc để kiểm chứng, nếu không nó chỉ đang kiểm chứng dải mặc định.");

        // ── 3. chọn câu hỏi ───────────────────────────────────────────────────
        var question = SelectQuestion(jobCategory, lang, request.Question, request.SampleQuestionId);

        // ── 4. còn lượt nào đang chạy? (self-heal row mồ côi trước) ────────────
        await ResolveStaleRunningAsync(jobCategory, lang, ct);
        if (await db.AdminRubricPreviewRuns.AnyAsync(
                r => r.JobCategory == jobCategory && r.Language == lang
                     && r.Status == AdminRubricPreviewStatus.Running, ct))
            throw new InvalidOperationException(
                "Đang có một lượt chấm thử chạy cho bộ này. Đợi nó xong rồi thử lại.");

        // ── 5. quota ──────────────────────────────────────────────────────────
        // Chỉ đếm Succeeded: phạt người soạn vì AI của ta hỏng là sai.
        var succeeded = await CountSucceededAsync(jobCategory, lang, rubricVersion, ct);
        if (succeeded >= FreeRunsPerRubricVersion)
            throw new PreviewQuotaExceededException(
                $"Đã dùng hết {FreeRunsPerRubricVersion} lượt chấm thử cho bản {rubricVersion}. "
                + "Sửa mốc rồi lưu thì có lượt mới.");

        // ── 6. INSERT row Running TRƯỚC khi gọi AI ────────────────────────────
        // Có chủ đích: row này vừa là khoá chống double-click (UNIQUE có điều kiện) vừa là chỗ kết quả
        // rơi vào kể cả khi trình duyệt admin chết — reload là thấy trong lịch sử.
        var run = new AdminRubricPreviewRun
        {
            Id = Guid.NewGuid(),
            JobCategory = jobCategory,
            Language = lang,
            RubricVersion = rubricVersion,
            CreatedByUserId = actorUserId,
            QuestionText = question,
            Status = AdminRubricPreviewStatus.Running,
            RubricSnapshot = JsonSerializer.Serialize(BuildRubricView(criteria), Json),
            RubricFingerprint = RubricFingerprint.Compute(ToSnapshots(criteria)),
            CreatedAt = DateTime.UtcNow
        };
        db.AdminRubricPreviewRuns.Add(run);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Hai request vào cùng lúc: UNIQUE có điều kiện là trọng tài, không phải câu đọc ở bước 4.
            db.Entry(run).State = EntityState.Detached;
            throw new InvalidOperationException(
                "Đang có một lượt chấm thử chạy cho bộ này. Đợi nó xong rồi thử lại.");
        }

        // ── 7. gọi AI rồi chốt trạng thái ─────────────────────────────────────
        try
        {
            var result = await ai.RunAsync(
                jobCategory.ToString(), lang, request.Seniority,
                question, sampleAnswer: null, request.CustomAnswer,
                TargetWordCount, BuildPreviewCriteria(criteria), ct);

            var samples = BuildSamples(criteria, result.Samples);
            run.Samples = JsonSerializer.Serialize(samples, Json);
            run.PromptVersion = result.PromptVersion;
            run.LengthParityWarning = result.LengthParityWarning;
            run.Status = AdminRubricPreviewStatus.Succeeded;
            run.CompletedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);

            return ToResponse(run, await FreeRemainingAsync(jobCategory, lang, rubricVersion, ct));
        }
        catch (Exception ex)
        {
            await MarkFailedAsync(run, ex.Message, ct);
            throw;
        }
    }

    public async Task<IReadOnlyList<AdminRubricPreviewRunResponse>> HistoryAsync(
        JobCategory jobCategory, string? language, CancellationToken ct = default)
    {
        var lang = ValidateLanguage(language);
        var runs = await db.AdminRubricPreviewRuns.AsNoTracking()
            .Where(r => r.JobCategory == jobCategory && r.Language == lang)
            .OrderByDescending(r => r.CreatedAt)
            .Take(20)
            .ToListAsync(ct);

        var free = runs.Count == 0
            ? FreeRunsPerRubricVersion
            : await FreeRemainingAsync(jobCategory, lang, runs[0].RubricVersion, ct);

        return runs.Select(r => ToResponse(r, free)).ToList();
    }

    public async Task<AdminSuggestLevelsResponse> SuggestLevelsAsync(
        JobCategory jobCategory, string? language, string? seniority, CancellationToken ct = default)
    {
        var lang = ValidateLanguage(language);
        if (levelSuggester is null)
            throw new InvalidOperationException("Chưa cấu hình dịch vụ gợi ý mốc.");

        var criteria = await LoadActiveSetAsync(jobCategory, lang, ct);
        if (criteria.Count == 0)
            throw new KeyNotFoundException($"Chưa có bộ chuẩn cho {jobCategory} ({lang}).");

        var suggested = await levelSuggester.SuggestAsync(
            jobCategory.ToString(), lang, seniority, jdText: null,
            criteria.Select(c => new LevelSuggestionInput(c.Id, c.Name, c.Description, c.MaxScore)).ToList(),
            ct);

        var byId = suggested.ToDictionary(s => s.CriterionId);
        return new AdminSuggestLevelsResponse(jobCategory, lang, criteria[0].Version,
            criteria.Select(c => new AdminSuggestedCriterionLevels(
                c.Id, c.Name, c.MaxScore,
                byId.TryGetValue(c.Id, out var s)
                    ? s.Levels.OrderBy(l => l.Score)
                        .Select(l => new AdminRubricLevelItem(l.Score, l.Descriptor)).ToList()
                    : []))
                .ToList());
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────

    private Task<List<RubricCriterion>> LoadActiveSetAsync(
        JobCategory jobCategory, string language, CancellationToken ct)
        => db.RubricCriteria.AsNoTracking().Include(c => c.Levels)
            .Where(c => c.CampaignId == null && c.CandidateId == null
                        && c.JobCategory == jobCategory && c.Language == language && c.IsActive)
            .OrderBy(c => c.Name)
            .ToListAsync(ct);

    private static string SelectQuestion(
        JobCategory jobCategory, string language, string? custom, string? sampleQuestionId)
    {
        if (!string.IsNullOrWhiteSpace(custom)) return custom.Trim();

        var bank = AdminPreviewQuestionBank.For(jobCategory, language);

        if (!string.IsNullOrWhiteSpace(sampleQuestionId))
        {
            // Từ chối TƯỜNG MINH thay vì rơi về câu mặc định: rơi âm thầm thì admin tưởng mình đang
            // kiểm chứng câu A trong khi hệ thống chấm câu B, và cả lượt chấm thử nói về một thứ khác
            // — mà báo cáo trông vẫn hợp lý.
            return AdminPreviewQuestionBank.Find(jobCategory, language, sampleQuestionId.Trim())?.Text
                ?? throw new InvalidOperationException(
                    $"sampleQuestionId '{sampleQuestionId}' không thuộc bộ câu mẫu của {jobCategory} ({language}). "
                    + $"Hợp lệ: {string.Join(", ", bank.Select(q => q.Id))}.");
        }

        if (bank.Count == 0)
            throw new InvalidOperationException(
                $"Chưa có câu hỏi mẫu cho {jobCategory} ({language}) — nhập câu hỏi để chấm thử.");
        return bank[0].Text;
    }

    private async Task ResolveStaleRunningAsync(JobCategory jobCategory, string language, CancellationToken ct)
    {
        var cutoff = DateTime.UtcNow - StaleRunningAfter;
        var stale = await db.AdminRubricPreviewRuns
            .Where(r => r.JobCategory == jobCategory && r.Language == language
                        && r.Status == AdminRubricPreviewStatus.Running && r.CreatedAt < cutoff)
            .ToListAsync(ct);
        if (stale.Count == 0) return;

        foreach (var r in stale)
        {
            r.Status = AdminRubricPreviewStatus.Failed;
            r.ErrorReason = "Lượt chấm thử không kết thúc (tiến trình dừng giữa chừng).";
            r.CompletedAt = DateTime.UtcNow;
        }
        await db.SaveChangesAsync(ct);
        logger.LogWarning("Dọn {Count} lượt chấm thử mồ côi của {Cat} ({Lang})", stale.Count, jobCategory, language);
    }

    private Task<int> CountSucceededAsync(
        JobCategory jobCategory, string language, int rubricVersion, CancellationToken ct)
        => db.AdminRubricPreviewRuns.CountAsync(
            r => r.JobCategory == jobCategory && r.Language == language
                 && r.RubricVersion == rubricVersion
                 && r.Status == AdminRubricPreviewStatus.Succeeded, ct);

    private async Task<int> FreeRemainingAsync(
        JobCategory jobCategory, string language, int rubricVersion, CancellationToken ct)
        => Math.Max(0, FreeRunsPerRubricVersion
            - await CountSucceededAsync(jobCategory, language, rubricVersion, ct));

    private async Task MarkFailedAsync(AdminRubricPreviewRun run, string reason, CancellationToken ct)
    {
        run.Status = AdminRubricPreviewStatus.Failed;
        run.ErrorReason = reason.Length > 500 ? reason[..500] : reason;   // stack dài không nuốt cả cột
        run.CompletedAt = DateTime.UtcNow;
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            // Không được nuốt lỗi gốc bằng một lỗi ghi DB — row mồ côi đã có self-heal 5 phút lo.
            logger.LogError(ex, "Không ghi được trạng thái Failed cho lượt chấm thử {RunId}", run.Id);
        }
    }

    private static IReadOnlyList<RubricCriterionSnapshot> ToSnapshots(List<RubricCriterion> criteria)
        => criteria.OrderBy(c => c.Name, StringComparer.Ordinal)
            .Select((c, i) => new RubricCriterionSnapshot(i, c.Name, c.Description, c.Weight, c.MaxScore,
                SortedLevels(c).Select(l => new RubricLevelSnapshot(l.Score, l.Descriptor)).ToList()))
            .ToList();

    private static List<AdminPreviewRubricCriterion> BuildRubricView(List<RubricCriterion> criteria)
        => criteria.Select(c => new AdminPreviewRubricCriterion(
            c.Id, c.Name, c.Weight, c.MaxScore,
            SortedLevels(c).Select(l => new AdminRubricLevelItem(l.Score, l.Descriptor)).ToList()))
            .ToList();

    private static List<RubricLevel> SortedLevels(RubricCriterion c)
        => (c.Levels ?? []).OrderBy(l => l.Score).ToList();

    /// <summary>
    /// Mức kỳ vọng do CODE chọn, qua <see cref="ExpectedLevels"/> DÙNG CHUNG với đường chấm thử của
    /// employer. Mỗi bên tự chọn thì hai báo cáo "kỳ vọng vs thật" đo hai thứ khác nhau mà trông giống
    /// hệt — và không có gì trên màn hình nói ra điều đó.
    /// </summary>
    private static (int Weak, int Good, int Excellent) Expected(RubricCriterion c)
        => ExpectedLevels.For(SortedLevels(c).Select(l => new RubricLevelSnapshot(l.Score, l.Descriptor)).ToList());

    /// <summary>
    /// 🔴 Bộ tiêu chí gửi đi CHẤM THỬ dựng từ CHÍNH <see cref="ScoringCriteriaBuilder"/> — cùng hàm mà
    /// đường CHẤM THẬT dùng. Cả tính năng đứng trên lời hứa "thứ admin kiểm chứng chính là thứ người
    /// luyện bị chấm"; tự sort/tự map ở đây là mở đúng cái khe để hai đường trôi xa nhau mà KHÔNG có
    /// triệu chứng nào (cả hai vẫn ra điểm, chỉ là điểm của hai thước đo khác nhau).
    /// </summary>
    public static List<PreviewCriterionInput> BuildPreviewCriteria(List<RubricCriterion> criteria)
    {
        var shared = ScoringCriteriaBuilder.Build(criteria);
        var byId = criteria.ToDictionary(c => c.Id);

        return shared.Select(s =>
        {
            var c = byId[s.CriterionId];
            var (weak, good, excellent) = Expected(c);
            return new PreviewCriterionInput(
                c.Id, s.Name, s.Description, s.MaxScore, s.Weight,
                s.Levels,   // ĐÚNG mảng mà đường chấm thật gửi đi, không phải bản dựng lại
                weak, good, excellent);
        }).ToList();
    }

    private static List<AdminPreviewSample> BuildSamples(
        List<RubricCriterion> criteria, IReadOnlyList<PreviewSample> samples)
        => samples.Select(s =>
        {
            var scores = new List<AdminPreviewSampleScore>();
            decimal expectedSum = 0, actualSum = 0;

            foreach (var c in criteria)
            {
                var (weak, good, excellent) = Expected(c);
                var expected = s.Band switch
                {
                    "Weak" => weak,
                    "Good" => good,
                    "Excellent" => excellent,
                    _ => good   // bài admin tự dán: không có kỳ vọng riêng, neo ở mức giữa
                };

                var hit = s.Scores.FirstOrDefault(x => x.CriterionId == c.Id);
                scores.Add(new AdminPreviewSampleScore(
                    c.Id, c.Name, c.MaxScore, expected, hit?.Score ?? 0m, hit?.LevelMatched, hit?.Reasoning));

                if (c.MaxScore > 0)
                {
                    expectedSum += expected / (decimal)c.MaxScore * 100m;
                    actualSum += (hit?.Score ?? 0m) / c.MaxScore * 100m;
                }
            }

            // TRUNG BÌNH CỘNG, KHÔNG weighted — B2C tính điểm tổng bằng equal weight (INT-10). Dùng
            // công thức weighted của B2B ở đây thì báo cáo chấm thử đo một thang khác với thang người
            // luyện thật nhận, mà cả hai đều ra số trông hợp lý.
            var n = criteria.Count > 0 ? criteria.Count : 1;
            return new AdminPreviewSample(
                s.Band, s.AnswerText, s.WordCount,
                Math.Round(expectedSum / n, 2), Math.Round(actualSum / n, 2), scores);
        }).ToList();

    private static AdminRubricPreviewRunResponse ToResponse(AdminRubricPreviewRun run, int freeRemaining)
        => new(
            run.Id, run.Status.ToString(), run.JobCategory, run.Language, run.RubricVersion,
            run.QuestionText, run.RubricFingerprint, run.PromptVersion,
            // Bài mẫu là văn bản ⇒ không có số đo cách nói (F11). Băng cảnh báo trên FE đọc cờ này.
            DeliveryMetricsAvailable: false,
            run.LengthParityWarning,
            freeRemaining,
            Deserialize<List<AdminPreviewRubricCriterion>>(run.RubricSnapshot) ?? [],
            Deserialize<List<AdminPreviewSample>>(run.Samples) ?? [],
            run.ErrorReason, run.CreatedAt, run.CompletedAt);

    private static T? Deserialize<T>(string? json) where T : class
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<T>(json, Json); }
        catch (JsonException) { return null; }
    }

    private static string ValidateLanguage(string? requested)
    {
        if (string.IsNullOrWhiteSpace(requested)) return "vi";
        var language = requested.Trim().ToLowerInvariant();
        if (language is not ("vi" or "en"))
            throw new InvalidOperationException("language chỉ nhận vi hoặc en.");
        return language;
    }
}
