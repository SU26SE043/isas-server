namespace Isas.Shared.Scoring;

/// <summary>Node cây cú pháp. Mọi node mang khoảng ký tự <c>[Start, End)</c> để lỗi lúc chạy
/// (chia 0, biến lạ) chỉ đúng đoạn.</summary>
internal abstract class ScoringNode
{
    public int Start { get; init; }
    public int End { get; init; }
}

internal enum BinaryOp
{
    Add, Sub, Mul, Div,
    Lt, Le, Gt, Ge, Eq, Neq,
}

internal sealed class NumberNode : ScoringNode
{
    public required decimal Value { get; init; }
}

internal sealed class VariableNode : ScoringNode
{
    public required string Name { get; init; }
}

/// <summary>Chỉ có phủ định một ngôi (<c>-x</c>). Không có <c>+x</c>, không có <c>!x</c>.</summary>
internal sealed class NegateNode : ScoringNode
{
    public required ScoringNode Operand { get; init; }
}

internal sealed class BinaryNode : ScoringNode
{
    public required BinaryOp Op { get; init; }
    public required ScoringNode Left { get; init; }
    public required ScoringNode Right { get; init; }
}

/// <summary>Lời gọi hàm. <c>if</c> cũng là <see cref="CallNode"/> nhưng bộ đánh giá xử LAZY riêng
/// (chỉ đánh giá nhánh được chọn).</summary>
internal sealed class CallNode : ScoringNode
{
    public required string Name { get; init; }
    public required IReadOnlyList<ScoringNode> Args { get; init; }

    /// <summary>Khoảng ký tự của riêng TÊN hàm — cho lỗi <c>UNKNOWN_FUNCTION</c> chỉ vào tên, không
    /// vào cả lời gọi.</summary>
    public int NameStart { get; init; }
    public int NameEnd { get; init; }
}
