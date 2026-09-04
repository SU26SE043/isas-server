using System.Collections.Immutable;

namespace Isas.Shared.Scoring;

/// <summary>
/// SCP1 — MÔI TRƯỜNG chạy một biểu thức: giá trị (decimal) của mọi biến + chuỗi dùng cho
/// <c>count_below</c>.
///
/// <para><b>Đây là nơi định nghĩa NGHĨA ĐEN của từng biến HĐ-1</b> (cách suy từ dữ liệu thô). Mọi
/// thay đổi ở hàm <see cref="ForInterview"/>/<see cref="ForCvScreening"/> đều đổi điểm của mọi campaign
/// đang dùng biến đó ⇒ có GOLDEN VECTOR TEST khoá từng biến (input cố định → giá trị cố định). Sửa nghĩa
/// mà không làm đỏ test nào là dấu hiệu golden vector chưa phủ hết — thêm, đừng nới.</para>
///
/// <para>Tất cả tính bằng <see cref="decimal"/>, KHÔNG làm tròn ở đây: người viết biểu thức tự dùng
/// <c>round()</c> khi cần. Chia cho 0 (rubric rỗng, chưa có câu hỏi) → biến nhận <c>0</c>, không ném —
/// biểu thức tự quyết bằng <c>if()</c> nếu muốn phân biệt.</para>
/// </summary>
public sealed class ScoringContext
{
    public ScoringExpressionKind Kind { get; }

    /// <summary>Giá trị mọi biến đã công bố cho <see cref="Kind"/>. Khoá = tên trong
    /// <see cref="ScoringVariableCatalog"/>. Biểu thức tham chiếu tên KHÔNG có ở đây →
    /// <c>UNKNOWN_VARIABLE</c>.</summary>
    public IReadOnlyDictionary<string, decimal> Variables { get; }

    /// <summary>Chuỗi số cho <c>count_below(x)</c> = "đếm phần tử &lt; x". Với PHỎNG VẤN đây là danh
    /// sách <c>pct</c> của từng tiêu chí; với SÀNG CV là điểm quy đổi từng nhu cầu
    /// (strong→100, partial→50, weak→0). <c>count_below</c> là HÀM trả scalar — chuỗi này KHÔNG
    /// lộ ra ngôn ngữ (không có cú pháp truy cập mảng).</summary>
    public IReadOnlyList<decimal> CountBelowSeries { get; }

    private ScoringContext(
        ScoringExpressionKind kind,
        IReadOnlyDictionary<string, decimal> variables,
        IReadOnlyList<decimal> countBelowSeries)
    {
        Kind = kind;
        Variables = variables;
        CountBelowSeries = countBelowSeries;
    }

    /// <summary>
    /// Biến PHỎNG VẤN từ dữ liệu thô một buổi đã chấm.
    ///
    /// <list type="bullet">
    /// <item><c>weighted_avg_pct</c> = Σ(pct × weight) / Σweight. Σweight ≤ 0 (không tiêu chí) → 0.</item>
    /// <item><c>avg_pct</c> = trung bình CỘNG pct các tiêu chí (equal-weight). Không tiêu chí → 0.</item>
    /// <item><c>min_pct</c> / <c>max_pct</c> = nhỏ nhất / lớn nhất trong các pct. Không tiêu chí → 0.</item>
    /// <item><c>answered</c> = số câu đã trả lời; <c>total_questions</c> = tổng số câu của buổi.</item>
    /// <item><c>completeness</c> = answered / total_questions (PHÂN SỐ 0..1, không nhân 100).
    ///       total_questions = 0 → 0.</item>
    /// <item>RNK1 · HĐ-1 — <c>seed_answered</c>/<c>seed_total</c> = câu GỐC (kind = Seed) có ghi âm /
    ///       tổng câu gốc; <c>seed_completeness</c> = seed_answered / seed_total (PHÂN SỐ 0..1;
    ///       seed_total = 0 → 0). CHỈ đặt khi CẢ HAI <c>SeedAnswered</c> và <c>SeedTotal</c> non-null
    ///       (snapshot có từ RNK1 trở đi). Thiếu ⇒ ba khoá KHÔNG tồn tại ⇒ biểu thức tham chiếu →
    ///       <c>UNKNOWN_VARIABLE</c> → caller lùi an toàn (giống mọi biến chưa có context).</item>
    /// </list>
    /// <c>count_below</c> chạy trên danh sách pct từng tiêu chí.
    /// </summary>
    public static ScoringContext ForInterview(InterviewScoringInputs input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var criteria = input.Criteria ?? [];

        decimal weightSum = 0m, weightedPctSum = 0m, pctSum = 0m;
        decimal? min = null, max = null;
        var pcts = new List<decimal>(criteria.Count);

        foreach (var c in criteria)
        {
            pcts.Add(c.Pct);
            pctSum += c.Pct;
            weightSum += c.Weight;
            weightedPctSum += c.Pct * c.Weight;
            if (min is null || c.Pct < min) min = c.Pct;
            if (max is null || c.Pct > max) max = c.Pct;
        }

        var weightedAvgPct = weightSum > 0m ? weightedPctSum / weightSum : 0m;
        var avgPct = criteria.Count > 0 ? pctSum / criteria.Count : 0m;
        var completeness = input.TotalQuestions > 0
            ? (decimal)input.Answered / input.TotalQuestions
            : 0m;

        var vars = new Dictionary<string, decimal>(StringComparer.Ordinal)
        {
            ["weighted_avg_pct"] = weightedAvgPct,
            ["avg_pct"] = avgPct,
            ["min_pct"] = min ?? 0m,
            ["max_pct"] = max ?? 0m,
            ["answered"] = input.Answered,
            ["total_questions"] = input.TotalQuestions,
            ["completeness"] = completeness,
        };

        // RNK1 · HĐ-1 — biến seed_* CHỈ tồn tại khi snapshot mang dữ liệu câu gốc (từ RNK1 trở đi).
        // Đặt-thiếu-thì-vắng: biểu thức của policy tham chiếu seed_completeness trên snapshot cũ sẽ
        // ra UNKNOWN_VARIABLE ⇒ lùi an toàn + cờ, KHÔNG bịa seed_completeness = 1.
        if (input.SeedAnswered is int seedAnswered && input.SeedTotal is int seedTotal)
        {
            vars["seed_answered"] = seedAnswered;
            vars["seed_total"] = seedTotal;
            vars["seed_completeness"] = seedTotal > 0 ? (decimal)seedAnswered / seedTotal : 0m;
        }

        return new ScoringContext(ScoringExpressionKind.Interview, vars, pcts);
    }

    /// <summary>
    /// Biến SÀNG CV — phần lớn là đếm thẳng từ kết quả sàng. <c>count_below</c> chạy trên chuỗi điểm
    /// quy đổi từng nhu cầu: <c>weak_count</c> phần tử giá trị 0, <c>partial_count</c> phần tử giá trị
    /// 50, <c>strong_count</c> phần tử giá trị 100 (thứ tự không quan trọng với "đếm &lt; x").
    /// </summary>
    public static ScoringContext ForCvScreening(CvScreeningScoringInputs input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var vars = new Dictionary<string, decimal>(StringComparer.Ordinal)
        {
            ["strong_count"] = input.StrongCount,
            ["partial_count"] = input.PartialCount,
            ["weak_count"] = input.WeakCount,
            ["need_count"] = input.NeedCount,
            ["must_have_total"] = input.MustHaveTotal,
            ["must_have_met"] = input.MustHaveMet,
        };

        var series = new List<decimal>(
            Math.Max(0, input.WeakCount) + Math.Max(0, input.PartialCount) + Math.Max(0, input.StrongCount));
        for (var i = 0; i < input.WeakCount; i++) series.Add(0m);
        for (var i = 0; i < input.PartialCount; i++) series.Add(50m);
        for (var i = 0; i < input.StrongCount; i++) series.Add(100m);

        return new ScoringContext(ScoringExpressionKind.CvScreening, vars, series);
    }

    /// <summary>
    /// Bộ MẪU cố định để <c>validate</c> (HĐ-2) chạy thử biểu thức và trả <c>sampleScore</c>. Giá trị
    /// chọn sao cho không tạo chia-0 tự nhiên (mọi mẫu số &gt; 0) và nằm gọn trong [0,100] với các
    /// công thức seed.
    /// </summary>
    public static ScoringContext Sample(ScoringExpressionKind kind) => kind switch
    {
        ScoringExpressionKind.Interview => ForInterview(new InterviewScoringInputs(
            Criteria:
            [
                new CriterionScore(80m, 0.5m),
                new CriterionScore(60m, 0.3m),
                new CriterionScore(40m, 0.2m),
            ],
            Answered: 8,
            TotalQuestions: 10,
            // RNK1 · HĐ-1 — bộ mẫu mang cả câu gốc: seed_completeness = 4/5 = 0.8 (mẫu số > 0,
            // không tạo chia-0 tự nhiên). SkipPenalty true để validate biểu thức "phạt bỏ câu" chạy qua.
            SeedAnswered: 4,
            SeedTotal: 5,
            SkipPenalty: true)),
        ScoringExpressionKind.CvScreening => ForCvScreening(new CvScreeningScoringInputs(
            StrongCount: 3, PartialCount: 2, WeakCount: 1,
            NeedCount: 6, MustHaveTotal: 4, MustHaveMet: 3)),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>Tên biến hợp lệ cho <see cref="Kind"/> — tiện cho kiểm tra tĩnh trước khi có context
    /// thật.</summary>
    public ImmutableArray<string> PublishedVariables => ScoringVariableCatalog.For(Kind);
}
