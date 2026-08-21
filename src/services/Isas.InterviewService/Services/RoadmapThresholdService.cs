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
/// BC15 — ngưỡng ĐẠT theo cấp độ, đọc DB trước rồi mới rơi về mặc định code.
///
/// <para><b>KHÔNG CACHE.</b> Bảng tối đa vài hàng và chỉ được hỏi một lần cho mỗi lần build report —
/// một lần build đã tốn nhiều truy vấn khác cộng một lời gọi AI dài vài giây, nên cache ở đây không
/// mua được gì đo đếm được, mà lại mở ra cửa sổ "admin sửa xong nhưng chưa có hiệu lực" — đúng thứ
/// làm người sửa mất niềm tin vào màn quản trị.</para>
/// </summary>
public class RoadmapThresholdService(
    InterviewDbContext db,
    IOptions<RoadmapOptions> options) : IRoadmapThresholdService
{
    private readonly RoadmapOptions _options = options.Value;

    /// <summary>
    /// Tập cấp độ CODE biết. Suy thẳng từ <see cref="RoadmapLevel"/> nên thêm cấp mới (Intern,
    /// Lead…) là thêm một giá trị enum — endpoint nhận ngay, KHÔNG migration, không sửa file này.
    /// </summary>
    private static readonly string[] KnownLevels = Enum.GetNames<RoadmapLevel>();

    /// <summary>
    /// Đưa tên cấp độ người dùng gõ về dạng chính tắc, hoặc <c>null</c> nếu code không biết cấp đó.
    ///
    /// <para>⚠ CỐ Ý so khớp theo TÊN chứ không dùng <c>Enum.TryParse</c>: <c>TryParse</c> còn nhận
    /// chuỗi SỐ ("0" → Fresher) và danh sách ngăn bởi dấu phẩy, tức là admin gõ nhầm vẫn "thành
    /// công" rồi sửa trúng một cấp độ họ không định sửa.</para>
    ///
    /// <para>⚠ Cấp độ lạ bị TỪ CHỐI (mẫu F21 <c>PromptTemplateKeys</c>): một hàng mang khoá không
    /// đường nào đọc sẽ khiến người sửa tưởng mình vừa đổi được hành vi, trong khi report vẫn chạy
    /// ngưỡng cũ — hỏng im lặng, không triệu chứng.</para>
    /// </summary>
    private static string? Canonicalize(string? level) =>
        string.IsNullOrWhiteSpace(level)
            ? null
            : KnownLevels.FirstOrDefault(k => string.Equals(k, level.Trim(), StringComparison.OrdinalIgnoreCase));

    public async Task<IReadOnlyList<RoadmapLevelThresholdResponse>> ListAsync(CancellationToken ct = default)
    {
        var rows = await db.RoadmapLevelThresholds.AsNoTracking().ToListAsync(ct);
        return Project(rows);
    }

    public async Task<int> ThresholdForAsync(string level, CancellationToken ct = default)
    {
        // Cấp lạ → khỏi truy vấn, về thẳng mặc định. Không ném: xem chú thích trên interface.
        var canonical = Canonicalize(level);
        if (canonical is null) return _options.ThresholdFor(level ?? string.Empty);

        // So khớp trong BỘ NHỚ, không đẩy phép so không-phân-biệt-hoa-thường xuống SQL: bảng chỉ vài
        // hàng, mà ngữ nghĩa collation của Postgres và SQLite không giống nhau — đẩy xuống SQL là
        // mở ra một lớp bug chỉ nổ trên production còn CI vẫn xanh.
        var rows = await db.RoadmapLevelThresholds.AsNoTracking().ToListAsync(ct);
        var row = rows.FirstOrDefault(r => string.Equals(r.Level, canonical, StringComparison.OrdinalIgnoreCase));

        // DB trước, mặc định sau — ĐỪNG ĐẢO. Đảo lại thì bảng vẫn có dữ liệu, endpoint vẫn trả
        // "đã chỉnh", mà report vẫn chấm bằng ngưỡng cũ.
        return row?.ThresholdPct ?? _options.ThresholdFor(canonical);
    }

    public async Task<IReadOnlyList<RoadmapLevelThresholdResponse>> UpsertAsync(
        IReadOnlyDictionary<string, int> thresholds, Guid actor, CancellationToken ct = default)
    {
        if (thresholds is null || thresholds.Count == 0)
            throw new InvalidOperationException(
                "Cần ít nhất một cấp độ trong 'thresholds'.");

        // ── Pha 1: validate TẤT CẢ, chưa ghi gì ────────────────────────────────────────────
        // Sai một entry mà đã ghi những entry trước đó thì admin nhận 400 trong khi nửa thay đổi
        // đã nằm trong DB — trạng thái không ai kiểm tra lại vì "nó báo lỗi mà".
        var validated = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (rawLevel, pct) in thresholds)
        {
            var canonical = Canonicalize(rawLevel)
                ?? throw new InvalidOperationException(
                    $"Cấp độ không hợp lệ: '{rawLevel}'. Hợp lệ: {string.Join(", ", KnownLevels)}.");

            if (pct < 0 || pct > 100)
                throw new InvalidOperationException(
                    $"Ngưỡng của cấp '{canonical}' phải nằm trong [0, 100] (đang gửi {pct}).");

            // "Fresher" và "fresher" trong cùng một body chuẩn hoá về cùng một cấp. Im lặng chọn
            // một trong hai nghĩa là admin gửi hai con số và nhận về con số họ không chọn.
            if (validated.TryGetValue(canonical, out var dup))
                throw new InvalidOperationException(
                    $"Cấp '{canonical}' xuất hiện nhiều lần trong body (đã có {dup}, lại nhận {pct}).");

            validated[canonical] = pct;
        }

        // ── Pha 2: ghi ─────────────────────────────────────────────────────────────────────
        var now = DateTime.UtcNow;
        var existing = await db.RoadmapLevelThresholds.ToListAsync(ct);

        foreach (var (level, pct) in validated)
        {
            var row = existing.FirstOrDefault(r =>
                string.Equals(r.Level, level, StringComparison.OrdinalIgnoreCase));

            if (row is null)
            {
                db.RoadmapLevelThresholds.Add(new RoadmapLevelThreshold
                {
                    // Lưu dạng CHÍNH TẮC, không lưu nguyên chuỗi admin gõ: đường đọc hỏi bằng
                    // RoadmapLevel.ToString(), lệch hoa/thường ở đây là hỏng im lặng.
                    Level = level,
                    ThresholdPct = pct,
                    UpdatedBy = actor,
                    UpdatedAt = now
                });
            }
            else
            {
                row.Level = level;   // chuẩn hoá lại hàng cũ (nếu có) — hội tụ về đúng một dạng
                row.ThresholdPct = pct;
                row.UpdatedBy = actor;
                row.UpdatedAt = now;
            }
        }

        // Một SaveChanges = một transaction ngầm ⇒ nguyên tử sẵn, không cần tự mở transaction
        // (nên cũng không cần DbRetry/execution strategy — xem DB25b).
        await db.SaveChangesAsync(ct);

        return Project(await db.RoadmapLevelThresholds.AsNoTracking().ToListAsync(ct));
    }

    public async Task<bool> ResetAsync(string level, CancellationToken ct = default)
    {
        var canonical = Canonicalize(level)
            ?? throw new InvalidOperationException(
                $"Cấp độ không hợp lệ: '{level}'. Hợp lệ: {string.Join(", ", KnownLevels)}.");

        var rows = await db.RoadmapLevelThresholds.ToListAsync(ct);
        var row = rows.FirstOrDefault(r =>
            string.Equals(r.Level, canonical, StringComparison.OrdinalIgnoreCase));
        if (row is null) return false;

        // Hard-delete được vì KHÔNG có con dấu nào ở nơi khác trỏ về hàng này (khác
        // answer_scores.prompt_version → prompt_templates): report đã chốt mang sẵn ngưỡng của nó
        // trong snapshot final_report, nên xoá hàng ở đây không làm báo cáo cũ mất nghĩa.
        db.RoadmapLevelThresholds.Remove(row);
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// Ghép hàng DB với tập cấp độ code biết.
    ///
    /// <para>Trả về MỌI cấp độ code biết, kể cả cấp chưa ai chỉnh — chỉ liệt kê cấp có hàng thì màn
    /// quản trị trông như hệ thống chỉ có vài cấp độ, admin không thể biết mình được chỉnh những gì.
    /// Cộng thêm hàng "mồ côi" (cấp đã bị gỡ khỏi enum) để nó không nằm vô hình trong bảng.</para>
    /// </summary>
    private IReadOnlyList<RoadmapLevelThresholdResponse> Project(List<RoadmapLevelThreshold> rows)
    {
        var known = KnownLevels
            .Select(level =>
            {
                var row = rows.FirstOrDefault(r =>
                    string.Equals(r.Level, level, StringComparison.OrdinalIgnoreCase));
                var def = _options.ThresholdFor(level);
                return new RoadmapLevelThresholdResponse(
                    Level: level,
                    EffectivePct: row?.ThresholdPct ?? def,
                    DefaultPct: def,
                    IsOverridden: row is not null,
                    UpdatedBy: row?.UpdatedBy,
                    UpdatedAt: row?.UpdatedAt);
            });

        var orphans = rows
            .Where(r => Canonicalize(r.Level) is null)
            .Select(r => new RoadmapLevelThresholdResponse(
                Level: r.Level,
                EffectivePct: r.ThresholdPct,
                DefaultPct: _options.ThresholdFor(r.Level),
                IsOverridden: true,
                UpdatedBy: r.UpdatedBy,
                UpdatedAt: r.UpdatedAt,
                IsKnownLevel: false))
            .OrderBy(r => r.Level, StringComparer.Ordinal);

        return [.. known, .. orphans];
    }
}
