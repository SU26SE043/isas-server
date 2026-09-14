namespace Isas.Shared.Scoring;

/// <summary>
/// SCP1 · HĐ-1 — Bộ đánh giá cây cú pháp. Toàn bộ tính bằng <see cref="decimal"/> (tất định, không
/// <c>double</c>). So sánh trả <c>1</c>/<c>0</c>. <c>if(cond, a, b)</c> LAZY: đánh giá <c>cond</c>
/// trước, rồi CHỈ đánh giá nhánh được chọn — chia-0 ở nhánh kia không bao giờ nổ.
/// </summary>
internal static class ScoringEvaluator
{
    private sealed class EvalAbort(ScoringError error) : Exception
    {
        public ScoringError Error { get; } = error;
    }

    /// <returns><c>Value</c> có nghĩa kể cả khi <c>Error</c> là <c>RESULT_OUT_OF_RANGE</c> (trả giá
    /// trị ngoài dải để chẩn đoán); <c>null</c> khi lỗi khác.</returns>
    public static (decimal? Value, ScoringError? Error) Evaluate(
        ScoringNode root, ScoringContext context, int exprLength)
    {
        try
        {
            var value = Eval(root, context);
            if (value < 0m || value > 100m)
                return (value, new ScoringError(ScoringErrorCodes.ResultOutOfRange, 0, exprLength));
            return (value, null);
        }
        catch (EvalAbort abort)
        {
            return (null, abort.Error);
        }
    }

    private static decimal Eval(ScoringNode node, ScoringContext ctx) => node switch
    {
        NumberNode n => n.Value,
        VariableNode v => ResolveVariable(v, ctx),
        NegateNode neg => -Eval(neg.Operand, ctx),
        BinaryNode b => EvalBinary(b, ctx),
        CallNode c => EvalCall(c, ctx),
        _ => throw new InvalidOperationException($"node không xử lý được: {node.GetType().Name}"),
    };

    private static decimal ResolveVariable(VariableNode v, ScoringContext ctx)
    {
        if (ctx.Variables.TryGetValue(v.Name, out var value))
            return value;
        throw new EvalAbort(new ScoringError(ScoringErrorCodes.UnknownVariable, v.Start, v.End));
    }

    private static decimal EvalBinary(BinaryNode b, ScoringContext ctx)
    {
        var l = Eval(b.Left, ctx);

        if (b.Op == BinaryOp.Div)
        {
            var r = Eval(b.Right, ctx);
            if (r == 0m)
                throw new EvalAbort(new ScoringError(ScoringErrorCodes.DivideByZero, b.Start, b.End));
            return l / r;
        }

        var right = Eval(b.Right, ctx);
        return b.Op switch
        {
            BinaryOp.Add => l + right,
            BinaryOp.Sub => l - right,
            BinaryOp.Mul => l * right,
            BinaryOp.Lt => l < right ? 1m : 0m,
            BinaryOp.Le => l <= right ? 1m : 0m,
            BinaryOp.Gt => l > right ? 1m : 0m,
            BinaryOp.Ge => l >= right ? 1m : 0m,
            BinaryOp.Eq => l == right ? 1m : 0m,
            BinaryOp.Neq => l != right ? 1m : 0m,
            _ => throw new InvalidOperationException($"toán tử không xử lý được: {b.Op}"),
        };
    }

    private static decimal EvalCall(CallNode c, ScoringContext ctx)
    {
        // if() — LAZY. Đánh giá điều kiện, rồi chỉ một nhánh.
        if (c.Name == "if")
        {
            var cond = Eval(c.Args[0], ctx);
            return cond != 0m ? Eval(c.Args[1], ctx) : Eval(c.Args[2], ctx);
        }

        // Còn lại: đánh giá HẾT tham số (eager).
        var a = new decimal[c.Args.Count];
        for (var i = 0; i < c.Args.Count; i++)
            a[i] = Eval(c.Args[i], ctx);

        return c.Name switch
        {
            "min" => Fold(a, Math.Min),
            "max" => Fold(a, Math.Max),
            "sum" => Fold(a, static (x, y) => x + y),
            "avg" => Fold(a, static (x, y) => x + y) / a.Length,
            "round" => Math.Round(a[0], 0, MidpointRounding.AwayFromZero),
            "clamp" => Math.Max(a[1], Math.Min(a[0], a[2])),
            "count_below" => CountBelow(ctx, a[0]),
            _ => throw new InvalidOperationException($"hàm không xử lý được: {c.Name}"),
        };
    }

    private static decimal Fold(decimal[] values, Func<decimal, decimal, decimal> op)
    {
        var acc = values[0];
        for (var i = 1; i < values.Length; i++)
            acc = op(acc, values[i]);
        return acc;
    }

    private static decimal CountBelow(ScoringContext ctx, decimal threshold)
    {
        var count = 0;
        foreach (var v in ctx.CountBelowSeries)
            if (v < threshold) count++;
        return count;
    }
}
