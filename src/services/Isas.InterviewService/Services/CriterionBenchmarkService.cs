using Isas.InterviewService.ApplicationDbContext;
using Isas.InterviewService.DTOs;
using Isas.InterviewService.Entities;
using Isas.InterviewService.Enums;
using Isas.InterviewService.Models;
using Isas.InterviewService.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Isas.InterviewService.Services;

/// <summary>
/// F14 (FR08) — dựng mốc đối chiếu (lớp thứ hai của radar kết quả buổi luyện B2C).
///
/// ⚠ "CHUẨN NGÀNH" LÀ THỨ HỆ THỐNG NÀY KHÔNG CÓ. Không có bộ dữ liệu benchmark nào được mua
/// hay tích hợp. Vì vậy service này CỐ Ý chỉ dùng hai nguồn có thật, và nhãn nói đúng nguồn:
///
///   1. <b>PeerAverage</b> — trung bình % của NGƯỜI DÙNG KHÁC trên chính hệ thống: cùng
///      <c>job_category</c>, buổi B2C đã <c>Scored</c>, gom theo TÊN tiêu chí.
///   2. <b>PassThreshold</b> — ngưỡng đạt nội bộ (<see cref="ScoringOptions.ImprovementThresholdPct"/>),
///      tức đúng ngưỡng đang quyết định tiêu chí nào bị gắn "cần cải thiện" ngay trên màn hình đó.
///      KHÔNG phải hằng số mới bịa ra: dùng lại nó khiến đường kẻ trên radar giải thích luôn vì sao
///      một tiêu chí bị đánh dấu yếu.
///
/// Ba quyết định dễ bị "sửa cho gọn" về sau, ghi lại lý do:
///
/// • <b>Loại chính mình khỏi mẫu.</b> So mình với tập có chứa mình là vòng tròn; ở ca hệ thống mới
///   có 1 người dùng thì tập đó CHÍNH LÀ họ ⇒ mốc trùng khít điểm của họ — vô nghĩa nhưng nhìn rất
///   thuyết phục. Loại bản thân làm ca đó tự rơi về n=0 → ngưỡng nội bộ.
///
/// • <b>Gom theo TÊN tiêu chí, không theo id.</b> BC16 cho candidate rubric RIÊNG, mỗi người một
///   hàng <c>rubric_criteria</c> khác id cho cùng một tiêu chí. Gom theo id thì mọi người dùng
///   rubric riêng đều ra n=0 vĩnh viễn — tính năng chết im lặng đúng với nhóm dùng nhiều nhất.
///
/// • <b>Một nguồn cho CẢ radar, không trộn.</b> Mỗi trục một nguồn khác nhau thì đường đứt nét kia
///   không còn nghĩa gì thống nhất và không thể chú thích trung thực bằng một nhãn.
/// </summary>
public class CriterionBenchmarkService : ICriterionBenchmarkService
{
    private readonly InterviewDbContext _db;
    private readonly BenchmarkOptions _options;
    private readonly decimal _passThresholdPct;

    public CriterionBenchmarkService(
        InterviewDbContext db,
        IOptions<BenchmarkOptions> options,
        IOptions<ScoringOptions> scoring)
    {
        _db = db;
        _options = options.Value;
        _passThresholdPct = scoring.Value.ImprovementThresholdPct;
    }

    public async Task<BenchmarkResponse?> BuildAsync(
        PracticeSession session,
        IReadOnlyList<SessionCriterionScore> criterionScores,
        CancellationToken ct = default)
    {
        if (!_options.Enabled || criterionScores.Count == 0) return null;

        var names = criterionScores.Select(c => c.CriterionName).Distinct().ToList();

        // Mẫu cộng đồng: buổi B2C đã Scored, CÙNG vị trí, của người KHÁC.
        var peers = await _db.SessionCriterionScores.AsNoTracking()
            .Where(x => names.Contains(x.CriterionName)
                        && x.Session.CampaignId == null
                        && x.Session.Status == SessionStatus.Scored
                        && x.Session.JobCategory == session.JobCategory
                        && x.Session.CandidateId != session.CandidateId)
            .Select(x => new { x.CriterionName, x.Percentage, x.SessionId })
            .ToListAsync(ct);

        // Gom trong C# (không AVG SQL): SQLite của test map Average(decimal) qua ef_avg dễ lệch
        // Postgres — cùng lý do BC9 đã materialize rồi mới tính (SessionResultService).
        var byName = peers
            .GroupBy(p => p.CriterionName)
            .ToDictionary(
                g => g.Key,
                g => (Avg: g.Average(p => p.Percentage), N: g.Select(p => p.SessionId).Distinct().Count()));

        // Đủ mẫu cho MỌI tiêu chí thì mới dùng trung bình cộng đồng — thiếu một trục là cả biểu đồ
        // rơi về ngưỡng nội bộ (xem lý do "một nguồn cho cả radar" ở docstring).
        var minSample = _options.MinSampleSize;
        var enoughForAll = names.Count > 0
            && names.All(n => byName.TryGetValue(n, out var s) && s.N >= minSample);

        if (enoughForAll)
        {
            var sampleSize = names.Min(n => byName[n].N);
            var items = criterionScores
                .Select(cs => new CriterionBenchmarkResponse(
                    cs.CriterionId,
                    cs.CriterionName,
                    Math.Round(Math.Clamp(byName[cs.CriterionName].Avg, 0m, 100m), 2)))
                .ToList();

            return new BenchmarkResponse(
                Source: "PeerAverage",
                Label: $"Trung bình người luyện cùng vị trí (n={sampleSize})",
                SampleSize: sampleSize,
                Criteria: items);
        }

        var target = Math.Round(Math.Clamp(_passThresholdPct, 0m, 100m), 2);
        return new BenchmarkResponse(
            Source: "PassThreshold",
            // Nhãn nói ĐÚNG đây là ngưỡng nội bộ của sản phẩm, không phải chuẩn ngành nào cả.
            Label: $"Ngưỡng đạt nội bộ ({target:0.#}%)",
            SampleSize: 0,
            Criteria: criterionScores
                .Select(cs => new CriterionBenchmarkResponse(cs.CriterionId, cs.CriterionName, target))
                .ToList());
    }
}
