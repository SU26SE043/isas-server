using Isas.InterviewService.ApplicationDbContext;
using Isas.InterviewService.DTOs;
using Isas.InterviewService.Entities;
using Isas.InterviewService.Enums;
using Isas.InterviewService.Models;
using Isas.InterviewService.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
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
///
/// ── HÌNH DẠNG TRUY VẤN (phòng xa, KHÔNG phải sửa sự cố) ─────────────────────────────────────
///
/// Bản đầu nạp TOÀN BỘ <c>session_criterion_scores</c> của mọi người cùng nghề vào RAM rồi mới
/// gom. Hôm nay bảng mới vài trăm dòng ⇒ nó KHÔNG gây ra độ trễ nào đang đo được; nhưng chi phí
/// tăng tuyến tính theo toàn bộ lịch sử và điểm gọi lại là đường nóng nhất (lần poll trả báo cáo
/// ngay khi buổi `Scored`, rồi lặp mỗi lần mở trang Kết quả). Ở 100k buổi × ~7 tiêu chí là ~700k
/// dòng mỗi lượt xem. Ba lớp chặn, KHÔNG lớp nào đụng vào phép tính:
///
///   1. <b>Cửa sổ thời gian</b> (<see cref="BenchmarkOptions.PeerWindowDays"/>) — trần thành
///      "lưu lượng N ngày" thay vì "toàn bộ lịch sử", và làm "trung bình người luyện cùng vị trí"
///      có nghĩa hơn (so với người đang luyện gần đây).
///   2. <b>Cache dùng chung</b> (<see cref="BenchmarkOptions.CacheTtlSeconds"/>) — xem dưới.
///   3. <b>Index</b> <c>ix_practice_sessions_peer_benchmark</c> (partial:
///      <c>campaign_id IS NULL AND status = 'Scored'</c>) — phục vụ đúng vị từ này; trước đó
///      không index nào che nó.
///
/// ⚠ <b>VẪN GOM TRONG C#, KHÔNG DÙNG AVG() CỦA SQL</b> — CỐ Ý, đừng "tối ưu" nốt: SQLite (dùng
/// trong test) map <c>Average(decimal)</c> qua <c>ef_avg</c> lệch Postgres, nên test xanh mà prod
/// ra số khác — đúng loại lỗi im lặng mà <see cref="SessionResultService"/> đã materialize để né.
///
/// ── CACHE: vì sao khoá KHÔNG có candidate, mà vẫn loại được chính mình ──────────────────────
///
/// Mốc này giống nhau cho mọi người cùng <c>(nghề, ngôn ngữ, bộ tên tiêu chí)</c> — trừ đúng một
/// chi tiết: mỗi người phải bị loại khỏi mẫu CỦA CHÍNH HỌ. Nếu nhét <c>candidate_id</c> vào khoá
/// thì cache thành per-user: mỗi người dùng mới vẫn trả nguyên giá một lượt quét ⇒ quả bom vẫn còn.
///
/// Nên cache TỔNG của cả cộng đồng (kể cả mình) dạng <c>(tổng %, số dòng, số buổi)</c> mỗi tên
/// tiêu chí, rồi TRỪ phần đóng góp của chính người xem — truy vấn thứ hai rẻ (lọc thẳng
/// <c>candidate_id</c>, đã có <c>ix_practice_sessions_candidate_history</c>). Trung bình cộng chịu
/// phép trừ chính xác: <c>avg = (Σ_all − Σ_mine) / (rows_all − rows_mine)</c>, và hai tập rời nhau
/// nên số buổi cũng trừ thẳng được.
///
/// ⚠ Ảnh chụp trong cache là một THỜI ĐIỂM, còn phần "của mình" đọc ở hiện tại ⇒ có thể trừ HƠI
/// NHIỀU (buổi của chính mình vừa `Scored` sau lúc chụp thì có trong "mình" mà chưa có trong tổng).
/// Sai lệch này CHỈ đi theo hướng an toàn — dữ liệu bảng này chỉ được THÊM, nên tập "của mình bây
/// giờ" luôn phủ tập "của mình lúc chụp" ⇒ <b>chính mình không bao giờ lọt vào mẫu</b>, bất biến
/// quan trọng vẫn nguyên. Chiều ngược lại (trừ thiếu → tự so với chính mình) là điều không thể xảy ra.
/// Trừ ra số âm/0 ⇒ coi như không đủ mẫu ⇒ rơi về ngưỡng nội bộ, chứ không bịa ra một trung bình.
/// </summary>
public class CriterionBenchmarkService : ICriterionBenchmarkService
{
    private readonly InterviewDbContext _db;
    private readonly IMemoryCache _cache;
    private readonly BenchmarkOptions _options;
    private readonly decimal _passThresholdPct;

    /// <param name="cache">
    /// ⚠ Cần <c>builder.Services.AddMemoryCache();</c> trong <c>Program.cs</c> của InterviewService
    /// (AuthService/CampaignService đã dùng đúng mẫu này — KHÔNG thêm package mới,
    /// <c>Microsoft.Extensions.Caching.Memory</c> nằm sẵn trong framework ASP.NET Core).
    /// </param>
    public CriterionBenchmarkService(
        InterviewDbContext db,
        IMemoryCache cache,
        IOptions<BenchmarkOptions> options,
        IOptions<ScoringOptions> scoring)
    {
        _db = db;
        _cache = cache;
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

        // Tổng của CẢ cộng đồng (kể cả mình) trong cửa sổ — dùng chung, cache theo (nghề, ngôn ngữ,
        // bộ tên tiêu chí). Ảnh chụp mang theo `Cutoff` đã dùng để vế "của mình" dưới đây trừ trên
        // ĐÚNG cùng một cửa sổ (mốc cutoff trôi theo thời gian thật, không được lấy lại tại chỗ).
        var pool = await GetPoolAsync(session, names, ct);

        // Phần đóng góp của CHÍNH người xem, cùng vị từ + cùng cutoff → trừ ra khỏi tổng.
        var mine = await AggregateAsync(
            PoolQuery(session, names, pool.Cutoff).Where(x => x.Session.CandidateId == session.CandidateId),
            ct);

        var byName = new Dictionary<string, (decimal Avg, int N)>(pool.ByName.Count);
        foreach (var (name, all) in pool.ByName)
        {
            var own = mine.TryGetValue(name, out var m) ? m : CriterionAggregate.Empty;
            var rows = all.Rows - own.Rows;
            var sessions = all.Sessions - own.Sessions;

            // rows <= 0 ⇒ mẫu chỉ toàn là mình (hoặc ảnh chụp đã cũ hơn buổi của mình — xem ghi chú
            // "trừ hơi nhiều" ở docstring): KHÔNG có trung bình nào để nói ⇒ bỏ tên này khỏi mẫu.
            if (rows <= 0 || sessions <= 0) continue;
            byName[name] = ((all.SumPct - own.SumPct) / rows, sessions);
        }

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

    // ── Mẫu cộng đồng ───────────────────────────────────────────────────────────────────────

    /// <summary>Tổng đã gom của MỘT tên tiêu chí. Giữ tổng + số dòng (không giữ sẵn trung bình) vì
    /// chỉ ở dạng này phép "trừ phần của chính mình" mới còn đúng.</summary>
    private readonly record struct CriterionAggregate(decimal SumPct, int Rows, int Sessions)
    {
        public static readonly CriterionAggregate Empty = new(0m, 0, 0);
    }

    /// <summary>Ảnh chụp cộng đồng + ĐÚNG mốc cutoff đã dùng để chụp (xem `GetPoolAsync`).</summary>
    private sealed record PeerPool(DateTime? Cutoff, IReadOnlyDictionary<string, CriterionAggregate> ByName);

    /// <summary>
    /// Vị từ mẫu: buổi B2C (<c>campaign_id IS NULL</c>) đã <c>Scored</c>, cùng vị trí + cùng ngôn ngữ,
    /// trong cửa sổ. CHƯA loại chính mình — việc đó làm bằng phép trừ ở <c>BuildAsync</c> để ảnh chụp
    /// dùng chung được cho mọi người xem.
    ///
    /// Vì sao lọc thêm <c>Language</c>: seed rubric B2C có CẢ vi lẫn en cho cùng một nghề (xem
    /// <see cref="RubricCriteriaLoader"/>), nên tên tiêu chí hai ngôn ngữ vốn đã khác nhau và bộ lọc
    /// này gần như không đổi tập dòng. Khai nó ra để việc tách ngôn ngữ là điều CODE NÓI, không phải
    /// điều tình cờ đúng nhờ cách đặt tên tiêu chí — rubric riêng BC16 hoàn toàn có thể đặt trùng tên
    /// ở hai ngôn ngữ, và lúc đó mốc sẽ lẳng lặng trộn hai bộ tiêu chí khác nhau.
    ///
    /// `status`/`campaign_id` so với HẰNG ⇒ EF render thành literal ⇒ planner chứng minh được vị từ
    /// của partial index `ix_practice_sessions_peer_benchmark` (cùng lập luận đã verify cho
    /// `ix_practice_sessions_deadline`).
    /// </summary>
    private IQueryable<SessionCriterionScore> PoolQuery(
        PracticeSession session, List<string> names, DateTime? cutoff)
    {
        var q = _db.SessionCriterionScores.AsNoTracking()
            .Where(x => names.Contains(x.CriterionName)
                        && x.Session.CampaignId == null
                        && x.Session.Status == SessionStatus.Scored
                        && x.Session.JobCategory == session.JobCategory
                        && x.Session.Language == session.Language);

        // PeerWindowDays <= 0 = tắt cửa sổ (hành vi trước đây: toàn bộ lịch sử).
        return cutoff is null ? q : q.Where(x => x.Session.CreatedAt >= cutoff.Value);
    }

    /// <summary>
    /// Materialize rồi gom TRONG C#. ⚠ KHÔNG đổi sang <c>AVG()</c>/<c>SUM()</c> của SQL: SQLite (test)
    /// map aggregate trên <c>decimal</c> qua <c>ef_avg</c> lệch Postgres ⇒ test xanh mà prod ra số khác.
    /// Gom theo TÊN (không theo id) — BC16 cho mỗi ứng viên một hàng rubric khác id cho cùng tiêu chí.
    /// </summary>
    private static async Task<Dictionary<string, CriterionAggregate>> AggregateAsync(
        IQueryable<SessionCriterionScore> query, CancellationToken ct)
    {
        var rows = await query
            .Select(x => new { x.CriterionName, x.Percentage, x.SessionId })
            .ToListAsync(ct);

        return rows
            .GroupBy(r => r.CriterionName)
            .ToDictionary(
                g => g.Key,
                g => new CriterionAggregate(
                    g.Sum(r => r.Percentage),
                    g.Count(),
                    g.Select(r => r.SessionId).Distinct().Count()));
    }

    /// <summary>
    /// Ảnh chụp cộng đồng, cache theo <c>(nghề, ngôn ngữ, cửa sổ, BỘ tên tiêu chí)</c>.
    ///
    /// Bộ tên phải nằm trong khoá vì nó là vị từ của truy vấn (rubric riêng BC16 ⇒ mỗi người một bộ);
    /// sắp xếp + nối bằng US (U+001F) để cùng một TẬP tên luôn ra cùng một khoá bất kể thứ tự, và để
    /// tên chứa dấu phẩy/gạch không ghép nhầm thành khoá khác.
    ///
    /// ⚠ Khoá mang <c>PeerWindowDays</c> (số ngày), TUYỆT ĐỐI không mang mốc cutoff tuyệt đối: cutoff
    /// đổi theo từng tick ⇒ mọi lượt xem một khoá riêng ⇒ cache không bao giờ trúng.
    /// </summary>
    private async Task<PeerPool> GetPoolAsync(
        PracticeSession session, List<string> names, CancellationToken ct)
    {
        var days = _options.PeerWindowDays;
        var ttl = _options.CacheTtlSeconds;

        async Task<PeerPool> LoadAsync()
        {
            // Cutoff chốt MỘT LẦN ở đây rồi đi theo ảnh chụp: vế "của chính mình" phải trừ trên đúng
            // cửa sổ đã chụp, không phải cửa sổ tại thời điểm nó chạy.
            var cutoff = days > 0 ? DateTime.UtcNow.AddDays(-days) : (DateTime?)null;
            return new PeerPool(cutoff, await AggregateAsync(PoolQuery(session, names, cutoff), ct));
        }

        if (ttl <= 0) return await LoadAsync();   // TTL 0 = tắt cache (test/điều tra sự cố).

        var key = string.Join('\u001F',
            new[] { "f14-peer", session.JobCategory.ToString(), session.Language, days.ToString() }
                .Concat(names.OrderBy(n => n, StringComparer.Ordinal)));

        // GetOrCreateAsync có thể để 2 request cùng nạp khi cache lạnh (không khoá) — chấp nhận:
        // đây là truy vấn ĐỌC thuần, nạp trùng chỉ tốn công, không sai dữ liệu; thêm khoá vào đường
        // đọc kết quả thì đổi một vấn đề hiệu năng lấy một chỗ có thể nghẽn.
        return (await _cache.GetOrCreateAsync(key, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(ttl);
            return await LoadAsync();
        }))!;
    }
}
