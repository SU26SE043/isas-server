using System.Globalization;

namespace Isas.Shared.Scoring;

/// <summary>
/// SCP1 · HĐ-1 — Bộ phân tích ĐỆ QUY XUỐNG. Không phụ thuộc thư viện, không eval/Roslyn. Trả về cây
/// <see cref="ScoringNode"/> hoặc MỘT lỗi tĩnh đầu tiên (mã + vị trí).
///
/// <para>Ưu tiên toán tử (thấp → cao): so sánh <c>== != &lt; &lt;= &gt; &gt;=</c> → cộng/trừ →
/// nhân/chia → phủ định một ngôi <c>-</c> → sơ cấp (số · biến · <c>(…)</c> · lời gọi hàm). Vì thế
/// <c>2 + 3 * 4 == 14</c> phân tích thành <c>(2 + (3*4)) == 14</c>.</para>
///
/// <para>Bốn trần cứng (<see cref="ScoringLimits"/>) kiểm NGAY trong lúc phân tích, TRƯỚC khi đệ quy
/// sâu thêm — input lồng 10000 tầng bị chặn ở tầng 33, không kịp tràn stack.</para>
/// </summary>
internal static class ScoringParser
{
    private sealed class ParseAbort(ScoringError error) : Exception
    {
        public ScoringError Error { get; } = error;
    }

    public static (ScoringNode? Root, int NodeCount, ScoringError? Error) Parse(string expr)
    {
        if (expr.Length > ScoringLimits.MaxExpressionLength)
            return (null, 0, new ScoringError(ScoringErrorCodes.TooLong, 0, expr.Length));

        var (tokens, lexError) = ScoringLexer.Tokenize(expr);
        if (lexError is not null)
            return (null, 0, lexError);

        var state = new State(tokens, expr.Length);
        try
        {
            var root = state.ParseExpr();
            state.Expect(TokenType.End, "biểu thức còn thừa token");
            return (root, state.NodeCount, null);
        }
        catch (ParseAbort abort)
        {
            return (null, state.NodeCount, abort.Error);
        }
    }

    private sealed class State(List<Token> tokens, int exprLength)
    {
        private int _pos;
        private int _depth;

        public int NodeCount { get; private set; }

        private Token Cur => tokens[_pos];
        private Token Next() => tokens[_pos++];

        private T Track<T>(T node) where T : ScoringNode
        {
            if (++NodeCount > ScoringLimits.MaxNodeCount)
                throw new ParseAbort(new ScoringError(
                    ScoringErrorCodes.TooManyNodes, node.Start, exprLength));
            return node;
        }

        public void Expect(TokenType type, string _)
        {
            if (Cur.Type != type)
                throw Syntax(Cur);
            _pos++;
        }

        private static ParseAbort Syntax(Token at)
            => new(new ScoringError(ScoringErrorCodes.SyntaxError, at.Start, Math.Max(at.End, at.Start + 1)));

        // ── expr := comparison ──────────────────────────────────────────────────────────────────
        public ScoringNode ParseExpr()
        {
            // Guard TRƯỚC khi làm gì: mọi lồng (ngoặc, tham số hàm, phủ định) đều vào đây.
            if (++_depth > ScoringLimits.MaxDepth)
                throw new ParseAbort(new ScoringError(
                    ScoringErrorCodes.TooDeep, Cur.Start, exprLength));
            try
            {
                return ParseComparison();
            }
            finally
            {
                _depth--;
            }
        }

        private ScoringNode ParseComparison()
        {
            var left = ParseAdditive();
            while (true)
            {
                var op = Cur.Type switch
                {
                    TokenType.EqEq => BinaryOp.Eq,
                    TokenType.NotEq => BinaryOp.Neq,
                    TokenType.Lt => BinaryOp.Lt,
                    TokenType.Le => BinaryOp.Le,
                    TokenType.Gt => BinaryOp.Gt,
                    TokenType.Ge => BinaryOp.Ge,
                    _ => (BinaryOp?)null,
                };
                if (op is null) return left;
                Next();
                var right = ParseAdditive();
                left = Track(new BinaryNode
                {
                    Op = op.Value, Left = left, Right = right,
                    Start = left.Start, End = right.End,
                });
            }
        }

        private ScoringNode ParseAdditive()
        {
            var left = ParseMultiplicative();
            while (Cur.Type is TokenType.Plus or TokenType.Minus)
            {
                var op = Next().Type == TokenType.Plus ? BinaryOp.Add : BinaryOp.Sub;
                var right = ParseMultiplicative();
                left = Track(new BinaryNode
                {
                    Op = op, Left = left, Right = right,
                    Start = left.Start, End = right.End,
                });
            }
            return left;
        }

        private ScoringNode ParseMultiplicative()
        {
            var left = ParseUnary();
            while (Cur.Type is TokenType.Star or TokenType.Slash)
            {
                var op = Next().Type == TokenType.Star ? BinaryOp.Mul : BinaryOp.Div;
                var right = ParseUnary();
                left = Track(new BinaryNode
                {
                    Op = op, Left = left, Right = right,
                    Start = left.Start, End = right.End,
                });
            }
            return left;
        }

        private ScoringNode ParseUnary()
        {
            if (Cur.Type == TokenType.Minus)
            {
                var minus = Next();
                var operand = ParseUnary();
                return Track(new NegateNode
                {
                    Operand = operand, Start = minus.Start, End = operand.End,
                });
            }
            return ParsePrimary();
        }

        private ScoringNode ParsePrimary()
        {
            var tok = Cur;
            switch (tok.Type)
            {
                case TokenType.Number:
                    Next();
                    return Track(new NumberNode
                    {
                        Value = decimal.Parse(tok.Text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture),
                        Start = tok.Start, End = tok.End,
                    });

                case TokenType.LParen:
                    Next();
                    var inner = ParseExpr();
                    Expect(TokenType.RParen, "thiếu ')'");
                    return inner;

                case TokenType.Identifier:
                    Next();
                    if (Cur.Type == TokenType.LParen)
                        return ParseCall(tok);
                    return Track(new VariableNode { Name = tok.Text, Start = tok.Start, End = tok.End });

                default:
                    throw Syntax(tok);
            }
        }

        private ScoringNode ParseCall(Token name)
        {
            Expect(TokenType.LParen, "thiếu '('");
            var args = new List<ScoringNode>();
            if (Cur.Type != TokenType.RParen)
            {
                args.Add(ParseExpr());
                while (Cur.Type == TokenType.Comma)
                {
                    Next();
                    args.Add(ParseExpr());
                    if (args.Count > ScoringLimits.MaxCallArguments)
                        throw new ParseAbort(new ScoringError(
                            ScoringErrorCodes.WrongArgCount, name.Start, exprLength));
                }
            }
            var close = Cur;
            Expect(TokenType.RParen, "thiếu ')'");

            if (!ScoringVariableCatalog.Functions.Contains(name.Text))
                throw new ParseAbort(new ScoringError(
                    ScoringErrorCodes.UnknownFunction, name.Start, name.End));

            if (!ArityOk(name.Text, args.Count))
                throw new ParseAbort(new ScoringError(
                    ScoringErrorCodes.WrongArgCount, name.Start, close.End));

            return Track(new CallNode
            {
                Name = name.Text, Args = args,
                Start = name.Start, End = close.End,
                NameStart = name.Start, NameEnd = name.End,
            });
        }

        private static bool ArityOk(string fn, int count) => fn switch
        {
            "min" or "max" or "avg" or "sum" => count >= 1 && count <= ScoringLimits.MaxCallArguments,
            "round" => count == 1,
            "clamp" => count == 3,
            "if" => count == 3,
            "count_below" => count == 1,
            _ => false,
        };
    }
}
