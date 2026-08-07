using Isas.InterviewService.ApplicationDbContext;
using Isas.InterviewService.Data;
using Isas.InterviewService.DTOs;
using Isas.InterviewService.Entities;
using Microsoft.EntityFrameworkCore;

namespace Isas.InterviewService.Services;

/// <summary>
/// F21 (FR17) — quản lý mảnh prompt do admin tuỳ biến. Append-only, soft-versioned (mẫu BC16).
/// </summary>
public class PromptTemplateService(InterviewDbContext db, ILogger<PromptTemplateService> logger)
{
    /// <summary>Trần độ dài một mảnh. Không phải con số thiêng — nó tồn tại vì mảnh prompt đi
    /// THẲNG vào mỗi lượt gọi Gemini, nên một lần dán nhầm cả quyển tài liệu vào đây là mọi lượt
    /// chấm sau đó đắt hơn và chậm hơn, âm thầm. Cùng tinh thần với CAMP-5 (trần 20.000 cho JD).</summary>
    public const int MaxBodyChars = 8_000;

    /// <summary>
    /// Chuỗi mà thân prompt do admin nhập KHÔNG được chứa.
    ///
    /// <para>Đây là hàng rào chống <b>tự dựng frame giả</b>: prompt chấm bọc câu trả lời ứng viên
    /// giữa hai delimiter và dặn LLM coi mọi thứ bên trong là DỮ LIỆU. Nếu mảnh do admin nhập
    /// được phép chứa chính các delimiter đó, admin (hoặc kẻ chiếm tài khoản admin) có thể đóng
    /// khung sớm rồi viết chỉ thị nằm NGOÀI vùng dữ liệu — tức là ta giữ khung bất biến ở một
    /// cửa rồi để hở đúng cái khung ấy ở cửa bên cạnh.</para>
    /// </summary>
    private static readonly string[] ForbiddenFragments =
    [
        "---CÂU TRẢ LỜI",
        "---HẾT",
        "---CV",
        "---JD",
    ];

    public async Task<IReadOnlyList<PromptTemplateResponse>> ListAsync(CancellationToken ct)
    {
        var active = await db.PromptTemplates
            .Where(p => p.IsActive)
            .AsNoTracking()
            .ToDictionaryAsync(p => p.Key, ct);

        // Trả về MỌI khoá khai trong code, kể cả khoá chưa ai sửa. Chỉ trả những khoá có row
        // sẽ khiến màn quản trị trông như hệ thống chỉ có vài prompt — người dùng không thể biết
        // mình được sửa những gì. Khoá chưa tuỳ biến ⇒ body null = "đang dùng bản mặc định
        // trong code" (bản mặc định nằm ở prompts.py, cố ý KHÔNG chép sang .NET).
        return [.. PromptTemplateKeys.All
            .OrderBy(k => k, StringComparer.Ordinal)
            .Select(k => active.TryGetValue(k, out var t)
                ? new PromptTemplateResponse(k, t.Version, t.Body, t.UpdatedBy, t.ChangeNote, t.CreatedAt)
                : new PromptTemplateResponse(k, 0, null, null, null, null))];
    }

    public async Task<IReadOnlyList<PromptTemplateResponse>> HistoryAsync(string key, CancellationToken ct) =>
        await db.PromptTemplates
            .Where(p => p.Key == key)
            .OrderByDescending(p => p.Version)
            .AsNoTracking()
            .Select(p => new PromptTemplateResponse(
                p.Key, p.Version, p.Body, p.UpdatedBy, p.ChangeNote, p.CreatedAt))
            .ToListAsync(ct);

    /// <summary>Bản đồ khoá→văn bản đang hiệu lực, cho AIService nạp (endpoint internal).</summary>
    public async Task<Dictionary<string, string>> GetActiveMapAsync(CancellationToken ct) =>
        await db.PromptTemplates
            .Where(p => p.IsActive)
            .AsNoTracking()
            .ToDictionaryAsync(p => p.Key, p => p.Body, ct);

    /// <summary>
    /// Sửa một mảnh = tạo VERSION MỚI (deactivate bản cũ). Không UPDATE tại chỗ — xem
    /// <see cref="PromptTemplate"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">Khoá lạ · body rỗng/quá dài · body chứa
    /// delimiter khung. Controller map sang 400.</exception>
    public async Task<PromptTemplateResponse> UpsertAsync(
        string key, string body, Guid actor, string? changeNote, CancellationToken ct)
    {
        if (!PromptTemplateKeys.All.Contains(key))
            throw new InvalidOperationException(
                $"Khoá prompt không hợp lệ: '{key}'. Khoá phải nằm trong danh sách code khai.");

        body = (body ?? string.Empty).Trim();

        if (body.Length == 0)
            throw new InvalidOperationException(
                "Nội dung prompt không được rỗng. Muốn quay về bản mặc định thì dùng endpoint reset.");

        if (body.Length > MaxBodyChars)
            throw new InvalidOperationException(
                $"Nội dung prompt vượt {MaxBodyChars} ký tự (đang gửi {body.Length}).");

        foreach (var frag in ForbiddenFragments)
        {
            if (body.Contains(frag, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"Nội dung prompt không được chứa '{frag}' — đó là delimiter khung dữ liệu, " +
                    "dùng lại sẽ phá hàng rào chống prompt-injection (AI-4).");
        }

        // 1 transaction: deactivate bản cũ + insert bản mới. Tách ra thì có khe để hai bản cùng
        // active (hoặc không bản nào active) — AIService sẽ đọc được bản tuỳ ý trong khe đó.
        //
        // DB25b — bọc IExecutionStrategy vì Npgsql bật EnableRetryOnFailure (xem <see cref="DbRetry"/>).
        // Cả phần đọc-rồi-sửa `current` LẪN phần Add bản mới phải nằm TRONG delegate: retry dọn tracker
        // rồi chạy lại từ đầu, nên bản mới phải được dựng lại theo `current` vừa đọc lại — để ngoài thì
        // lần thử sau tính `version` theo dữ liệu cũ.
        var created = await DbRetry.RunAsync(db, async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);

            var current = await db.PromptTemplates
                .Where(p => p.Key == key && p.IsActive)
                .ToListAsync(ct);

            foreach (var c in current) c.IsActive = false;

            var next = current.Count == 0
                ? 1
                : current.Max(c => c.Version) + 1;

            var row = new PromptTemplate
            {
                Key = key,
                Version = next,
                Body = body,
                IsActive = true,
                UpdatedBy = actor,
                ChangeNote = changeNote,
            };

            db.PromptTemplates.Add(row);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return row;
        });

        logger.LogInformation(
            "F21: prompt '{Key}' lên version {Version} bởi {Actor}", key, created.Version, actor);

        return new PromptTemplateResponse(
            created.Key, created.Version, created.Body,
            created.UpdatedBy, created.ChangeNote, created.CreatedAt);
    }

    /// <summary>
    /// Quay về bản mặc định trong code = deactivate mọi bản (KHÔNG xoá lịch sử).
    /// Hard-delete sẽ làm <c>answer_scores.prompt_version</c> của điểm cũ trỏ vào hư không.
    /// </summary>
    public async Task<bool> ResetAsync(string key, CancellationToken ct)
    {
        if (!PromptTemplateKeys.All.Contains(key))
            throw new InvalidOperationException($"Khoá prompt không hợp lệ: '{key}'.");

        var affected = await db.PromptTemplates
            .Where(p => p.Key == key && p.IsActive)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.IsActive, false), ct);

        return affected > 0;
    }

    /// <summary>
    /// Con dấu phiên bản đóng lên điểm: tổng version của mọi mảnh đang active.
    ///
    /// <para>KHÔNG phải để đọc ra "prompt lúc đó viết gì" — muốn biết nội dung thì tra bảng
    /// lịch sử. Nó để trả lời câu rẻ hơn mà quan trọng hơn: <b>hai điểm này có được chấm bằng
    /// cùng một thước đo không?</b> Số khác nhau ⇒ khác thước ⇒ so sánh trực tiếp là sai
    /// (CAMP-10 xếp hạng ứng viên, BC15 tính cải thiện theo thời gian).</para>
    ///
    /// <para>⚠ Con dấu này mới chỉ được LƯU, chưa chỗ nào cảnh báo khi ranking trộn hai giá trị
    /// khác nhau — xem backlog BK23. Lưu trước vì lưu là thứ KHÔNG hồi tố được: thiếu cột thì
    /// điểm lịch sử vĩnh viễn mất dấu đã chấm bằng prompt nào. Cảnh báo thì thêm lúc nào cũng được.</para>
    /// </summary>
    public async Task<int> GetPromptVersionStampAsync(CancellationToken ct) =>
        await db.PromptTemplates.Where(p => p.IsActive).SumAsync(p => p.Version, ct);
}
