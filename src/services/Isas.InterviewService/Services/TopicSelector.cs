using Isas.InterviewService.Entities;
using Isas.InterviewService.Enums;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Isas.InterviewService.Services;

/// <summary>
/// TOP1-B3 — chọn tối đa <c>slots</c> chủ đề từ một hồ (pool) <see cref="PracticeTopic"/> đã được
/// lọc sẵn theo (nghề, cấp độ, ngôn ngữ), ưu tiên PHỦ các tiêu chí trong <c>targetable</c>.
///
/// THUẦN HÀM: KHÔNG truy vấn DB, KHÔNG biết gì về luồng tạo buổi — nhận <paramref name="pool"/> đã
/// nạp sẵn, trả về danh sách con của chính pool đó. Việc nạp pool (query DB theo
/// (jobCategory, seniority, language)) và wire vào <c>PracticeService</c> là việc của bước KHÁC (B5).
///
/// FAIL-OPEN TUYỆT ĐỐI — mọi ca xấu (pool rỗng, pool ít hơn slots, targetable không khớp chủ đề
/// nào) đều trả kết quả tốt nhất có thể + log, KHÔNG BAO GIỜ ném exception: chủ đề chỉ là gia vị
/// cho câu hỏi, một ô danh mục chưa seed không được phép làm hỏng một buổi đã trừ credit (PAY-13).
///
/// Nhánh "slots &lt; số tiêu chí ⇒ phủ đúng slots tiêu chí KHÁC NHAU" khớp NGUYÊN VĂN nhánh 2 của
/// khối PHÂN BỔ BẮT BUỘC trong <c>prompts.py::build_prompt</c> (grep "PHÂN BỔ BẮT BUỘC"): "Chỉ
/// có {count} câu hỏi cho {n_criteria} tiêu chí, nên hãy chọn {count} tiêu chí KHÁC NHAU".
/// <c>count</c> ↔ <c>slots</c>, <c>n_criteria</c> ↔ số tiêu chí <c>targetable</c> phân biệt.
/// </summary>
public class TopicSelector
{
    private readonly Random _rng;
    private readonly ILogger<TopicSelector> _logger;

    public TopicSelector(Random? rng = null, ILogger<TopicSelector>? logger = null)
    {
        _rng = rng ?? Random.Shared;
        _logger = logger ?? NullLogger<TopicSelector>.Instance;
    }

    /// <param name="jobCategory">Chỉ dùng để LOG (pool đã được caller lọc sẵn theo ô này).</param>
    /// <param name="seniority">Chỉ dùng để LOG.</param>
    /// <param name="language">Chỉ dùng để LOG.</param>
    /// <param name="slots">Số chủ đề tối đa cần chọn.</param>
    /// <param name="targetable">Tên các tiêu chí NỘI DUNG cần ưu tiên phủ (thứ tự ổn định — ảnh
    /// hưởng dãy số ngẫu nhiên rút ra, giữ nguyên thứ tự caller truyền để tái lập được với cùng
    /// <see cref="Random"/> seed).</param>
    /// <param name="pool">Hồ chủ đề đã lọc sẵn theo (nghề, cấp độ, ngôn ngữ) — thứ tự ổn định, cùng
    /// lý do với <paramref name="targetable"/>.</param>
    /// <returns>≤ <paramref name="slots"/> chủ đề, không trùng <see cref="PracticeTopic.TopicKey"/>.</returns>
    public IReadOnlyList<PracticeTopic> Select(
        JobCategory jobCategory,
        string seniority,
        string language,
        int slots,
        IReadOnlyList<string> targetable,
        IReadOnlyList<PracticeTopic> pool)
    {
        if (pool.Count == 0)
        {
            _logger.LogInformation(
                "TOP1: pool chủ đề rỗng cho ({JobCategory}/{Seniority}/{Language}) — trả rỗng, " +
                "không chặn buổi.", jobCategory, seniority, language);
            return [];
        }

        if (slots <= 0)
            return [];

        var effectiveSlots = slots;
        if (pool.Count < slots)
        {
            _logger.LogWarning(
                "TOP1: pool chủ đề ({PoolCount}) nhỏ hơn số khe cần ({Slots}) cho " +
                "({JobCategory}/{Seniority}/{Language}) — trả hết pool, KHÔNG lặp.",
                pool.Count, slots, jobCategory, seniority, language);
            effectiveSlots = pool.Count;
        }

        var distinctTargetable = DedupeTrimmed(targetable);

        var selected = new List<PracticeTopic>(effectiveSlots);
        var usedKeys = new HashSet<string>(StringComparer.Ordinal);

        // PHÂN BỔ BẮT BUỘC (khớp prompts.py::build_prompt, grep "PHÂN BỔ BẮT BUỘC"):
        //  - effectiveSlots >= n_criteria  ⇒ phủ HẾT distinctTargetable (nhánh 1: "MỖI tiêu chí ...
        //    phải được ÍT NHẤT MỘT câu hỏi").
        //  - effectiveSlots <  n_criteria  ⇒ phủ ĐÚNG effectiveSlots tiêu chí KHÁC NHAU, bốc ngẫu
        //    nhiên trong tập targetable (nhánh 2: "chọn {count} tiêu chí KHÁC NHAU").
        var criteriaToCover = distinctTargetable.Count == 0
            ? []
            : effectiveSlots >= distinctTargetable.Count
                ? distinctTargetable
                : PickRandomSubset(distinctTargetable, effectiveSlots, _rng);

        foreach (var criterion in criteriaToCover)
        {
            if (selected.Count >= effectiveSlots)
                break;

            var candidates = pool
                .Where(t => !usedKeys.Contains(t.TopicKey)
                    && string.Equals(t.CriterionName?.Trim(), criterion, StringComparison.Ordinal))
                .ToList();

            if (candidates.Count == 0)
            {
                // Fail-open: ô danh mục thiếu chủ đề cho đúng tiêu chí này — bỏ qua, KHÔNG ném.
                _logger.LogDebug(
                    "TOP1: không có chủ đề nào trong pool khớp tiêu chí '{Criterion}' " +
                    "({JobCategory}/{Seniority}/{Language}).",
                    criterion, jobCategory, seniority, language);
                continue;
            }

            var pick = candidates[_rng.Next(candidates.Count)];
            selected.Add(pick);
            usedKeys.Add(pick.TopicKey);
        }

        // Khe dư ⇒ bốc ngẫu nhiên phần còn lại của pool (không lặp — rút dần không hoàn lại).
        if (selected.Count < effectiveSlots)
        {
            var remainder = pool.Where(t => !usedKeys.Contains(t.TopicKey)).ToList();
            var need = effectiveSlots - selected.Count;

            while (need > 0 && remainder.Count > 0)
            {
                var idx = _rng.Next(remainder.Count);
                var item = remainder[idx];
                remainder.RemoveAt(idx);

                selected.Add(item);
                usedKeys.Add(item.TopicKey);
                need--;
            }
        }

        return selected;
    }

    /// <summary>Trim + bỏ rỗng + khử trùng lặp (Ordinal), GIỮ thứ tự xuất hiện đầu tiên — thứ tự
    /// ổn định là điều kiện để <see cref="Random"/> cùng seed cho ra cùng kết quả.</summary>
    private static List<string> DedupeTrimmed(IReadOnlyList<string> raw)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>(raw.Count);
        foreach (var value in raw)
        {
            var trimmed = value?.Trim();
            if (string.IsNullOrEmpty(trimmed))
                continue;
            if (seen.Add(trimmed))
                result.Add(trimmed);
        }
        return result;
    }

    /// <summary>Bốc ngẫu nhiên đúng <paramref name="take"/> phần tử KHÁC NHAU (rút dần không hoàn
    /// lại) từ <paramref name="items"/>, giữ nguyên đối tượng <paramref name="rng"/> để dãy số
    /// ngẫu nhiên nối tiếp đúng với phần chọn chủ đề theo sau (Random(seed) tái lập được).</summary>
    private static List<string> PickRandomSubset(IReadOnlyList<string> items, int take, Random rng)
    {
        var copy = new List<string>(items);
        var result = new List<string>(Math.Min(take, copy.Count));
        for (var i = 0; i < take && copy.Count > 0; i++)
        {
            var idx = rng.Next(copy.Count);
            result.Add(copy[idx]);
            copy.RemoveAt(idx);
        }
        return result;
    }
}
