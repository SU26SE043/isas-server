using System.Collections.Immutable;

namespace Isas.Shared.Scoring;

/// <summary>
/// SCP1 · HĐ-1/HĐ-2 — CỬA VÀO của ngôn ngữ biểu thức chấm điểm. Phân tích một lần, đánh giá nhiều lần
/// (một cây, N ứng viên).
///
/// <code>
/// var parsed = ScoringExpression.Parse("weighted_avg_pct");
/// if (!parsed.Ok) return parsed.Errors;                // mã + vị trí, FE tự dịch
/// var r = parsed.Evaluate(ScoringContext.ForInterview(inputs));
/// if (!r.Ok) { /* scoreFallback = true */ }
/// else       { /* dùng r.Value */ }
/// </code>
/// </summary>
public static class ScoringExpression
{
    /// <summary>Phân tích tĩnh (không cần dữ liệu ứng viên). Lỗi ở đây: <c>SYNTAX_ERROR</c>,
    /// <c>UNKNOWN_FUNCTION</c>, <c>WRONG_ARG_COUNT</c>, <c>TOO_LONG</c>, <c>TOO_DEEP</c>,
    /// <c>TOO_MANY_NODES</c>.</summary>
    public static ParseResult Parse(string expression)
    {
        ArgumentNullException.ThrowIfNull(expression);
        var (root, nodeCount, error) = ScoringParser.Parse(expression);
        return error is not null
            ? new ParseResult(expression, null, 0, [error])
            : new ParseResult(expression, root, nodeCount, []);
    }

    /// <summary>
    /// HĐ-2 tiện ích — phân tích RỒI chạy thử trên bộ MẪU của <paramref name="kind"/>. Đúng thứ endpoint
    /// <c>/scoring-policies/validate</c> cần: <c>valid</c> + <c>sampleScore</c>, hoặc danh sách lỗi.
    /// </summary>
    public static ValidationResult Validate(ScoringExpressionKind kind, string expression)
    {
        var parsed = Parse(expression);
        if (!parsed.Ok)
            return new ValidationResult(false, null, parsed.Errors);

        var r = parsed.Evaluate(ScoringContext.Sample(kind));
        return r.Ok
            ? new ValidationResult(true, r.Value, [])
            : new ValidationResult(false, null, r.Errors);
    }
}

/// <summary>Kết quả phân tích. Giữ cây (nội bộ) để <see cref="Evaluate"/> lại nhiều lần không phân
/// tích lại.</summary>
public sealed class ParseResult
{
    private readonly string _expression;
    internal ScoringNode? Root { get; }

    internal ParseResult(string expression, ScoringNode? root, int nodeCount, IReadOnlyList<ScoringError> errors)
    {
        _expression = expression;
        Root = root;
        NodeCount = nodeCount;
        Errors = errors;
        ReferencedVariables = root is null ? [] : CollectVariables(root);
    }

    public bool Ok => Errors.Count == 0;
    public IReadOnlyList<ScoringError> Errors { get; }

    /// <summary>Số node cây (0 khi phân tích lỗi). Rẻ để log/quan sát.</summary>
    public int NodeCount { get; }

    /// <summary>Tên các biến biểu thức THAM CHIẾU (đã distinct + sắp ordinal). Vào vân tay HĐ-4 và cho
    /// phép kiểm "biến lạ" thuần tĩnh trước khi có context.</summary>
    public ImmutableArray<string> ReferencedVariables { get; }

    /// <summary>Đánh giá cây trên một <see cref="ScoringContext"/>. Lỗi ở đây: <c>UNKNOWN_VARIABLE</c>,
    /// <c>DIVIDE_BY_ZERO</c>, <c>RESULT_OUT_OF_RANGE</c>.</summary>
    public EvalResult Evaluate(ScoringContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (Root is null)
            return new EvalResult(false, null, Errors);

        var (value, error) = ScoringEvaluator.Evaluate(Root, context, _expression.Length);
        return error is null
            ? new EvalResult(true, value, [])
            : new EvalResult(false, error.Code == ScoringErrorCodes.ResultOutOfRange ? value : null, [error]);
    }

    private static ImmutableArray<string> CollectVariables(ScoringNode root)
    {
        var names = new SortedSet<string>(StringComparer.Ordinal);
        Walk(root, names);
        return [.. names];

        static void Walk(ScoringNode n, SortedSet<string> acc)
        {
            switch (n)
            {
                case VariableNode v: acc.Add(v.Name); break;
                case NegateNode neg: Walk(neg.Operand, acc); break;
                case BinaryNode b: Walk(b.Left, acc); Walk(b.Right, acc); break;
                case CallNode c: foreach (var a in c.Args) Walk(a, acc); break;
            }
        }
    }
}

/// <summary>Kết quả đánh giá. <see cref="Value"/> có thể khác <c>null</c> kèm <see cref="Ok"/> = false
/// khi lỗi là <c>RESULT_OUT_OF_RANGE</c> (giá trị ngoài dải, để chẩn đoán).</summary>
public sealed record EvalResult(bool Ok, decimal? Value, IReadOnlyList<ScoringError> Errors);

/// <summary>HĐ-2 — hình dạng câu trả lời của <c>/scoring-policies/validate</c>.</summary>
public sealed record ValidationResult(bool Valid, decimal? SampleScore, IReadOnlyList<ScoringError> Errors);
