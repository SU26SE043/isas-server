using System.Globalization;

namespace Isas.Shared.Scoring;

internal enum TokenType
{
    Number,
    Identifier,
    Plus, Minus, Star, Slash,
    Lt, Le, Gt, Ge, EqEq, NotEq,
    LParen, RParen, Comma,
    End,
}

/// <summary>Token + khoảng ký tự nửa mở <c>[Start, End)</c> trong biểu thức gốc.</summary>
internal readonly record struct Token(TokenType Type, string Text, int Start, int End);

/// <summary>
/// SCP1 — Bộ tách từ. KHÔNG ném: trả về danh sách token + (nếu có) MỘT lỗi <c>SYNTAX_ERROR</c> đầu tiên
/// kèm vị trí. Số dùng dấu chấm thập phân, không mũ (<c>1e3</c> không hợp lệ — giữ tất định, dễ đọc).
/// Định danh cho phép cả chữ hoa để lỗi "biến lạ" chỉ đúng đoạn định danh thay vì gãy cú pháp.
/// </summary>
internal static class ScoringLexer
{
    public static (List<Token> Tokens, ScoringError? Error) Tokenize(string expr)
    {
        var tokens = new List<Token>();
        var i = 0;
        var n = expr.Length;

        while (i < n)
        {
            var c = expr[i];

            if (c is ' ' or '\t' or '\r' or '\n') { i++; continue; }

            // ── số: chữ số [. chữ số] ──────────────────────────────────────────────────────────
            if (char.IsAsciiDigit(c))
            {
                var start = i;
                while (i < n && char.IsAsciiDigit(expr[i])) i++;
                if (i < n && expr[i] == '.')
                {
                    // bắt buộc có chữ số SAU dấu chấm ("1." không hợp lệ)
                    if (i + 1 >= n || !char.IsAsciiDigit(expr[i + 1]))
                        return (tokens, new ScoringError(ScoringErrorCodes.SyntaxError, start, i + 1));
                    i++;
                    while (i < n && char.IsAsciiDigit(expr[i])) i++;
                }
                var text = expr[start..i];
                if (!decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out _))
                    return (tokens, new ScoringError(ScoringErrorCodes.SyntaxError, start, i));
                tokens.Add(new Token(TokenType.Number, text, start, i));
                continue;
            }

            // ── định danh: [A-Za-z_][A-Za-z0-9_]* ─────────────────────────────────────────────
            if (char.IsAsciiLetter(c) || c == '_')
            {
                var start = i;
                while (i < n && (char.IsAsciiLetterOrDigit(expr[i]) || expr[i] == '_')) i++;
                tokens.Add(new Token(TokenType.Identifier, expr[start..i], start, i));
                continue;
            }

            // ── toán tử / dấu ────────────────────────────────────────────────────────────────
            switch (c)
            {
                case '+': tokens.Add(new Token(TokenType.Plus, "+", i, i + 1)); i++; continue;
                case '-': tokens.Add(new Token(TokenType.Minus, "-", i, i + 1)); i++; continue;
                case '*': tokens.Add(new Token(TokenType.Star, "*", i, i + 1)); i++; continue;
                case '/': tokens.Add(new Token(TokenType.Slash, "/", i, i + 1)); i++; continue;
                case '(': tokens.Add(new Token(TokenType.LParen, "(", i, i + 1)); i++; continue;
                case ')': tokens.Add(new Token(TokenType.RParen, ")", i, i + 1)); i++; continue;
                case ',': tokens.Add(new Token(TokenType.Comma, ",", i, i + 1)); i++; continue;

                case '<':
                    if (i + 1 < n && expr[i + 1] == '=') { tokens.Add(new Token(TokenType.Le, "<=", i, i + 2)); i += 2; }
                    else { tokens.Add(new Token(TokenType.Lt, "<", i, i + 1)); i++; }
                    continue;
                case '>':
                    if (i + 1 < n && expr[i + 1] == '=') { tokens.Add(new Token(TokenType.Ge, ">=", i, i + 2)); i += 2; }
                    else { tokens.Add(new Token(TokenType.Gt, ">", i, i + 1)); i++; }
                    continue;
                case '=':
                    if (i + 1 < n && expr[i + 1] == '=') { tokens.Add(new Token(TokenType.EqEq, "==", i, i + 2)); i += 2; continue; }
                    return (tokens, new ScoringError(ScoringErrorCodes.SyntaxError, i, i + 1));
                case '!':
                    if (i + 1 < n && expr[i + 1] == '=') { tokens.Add(new Token(TokenType.NotEq, "!=", i, i + 2)); i += 2; continue; }
                    return (tokens, new ScoringError(ScoringErrorCodes.SyntaxError, i, i + 1));

                default:
                    // ký tự lạ (&, |, %, ^, [, ], ...) — bao gồm cả mưu toan mở cú pháp truy vấn.
                    return (tokens, new ScoringError(ScoringErrorCodes.SyntaxError, i, i + 1));
            }
        }

        tokens.Add(new Token(TokenType.End, string.Empty, n, n));
        return (tokens, null);
    }
}
