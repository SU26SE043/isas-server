namespace Isas.Shared.Scoring;

/// <summary>
/// SCP1 — kết quả CHẠY MỘT biểu thức chính sách trên một <see cref="ScoringContext"/>.
/// <c>Ok</c> ⇒ <see cref="Value"/> là điểm (đã trong [0,100]); ngược lại <see cref="FailReason"/>
/// mang MÃ vì sao (mã lỗi HĐ-2, hoặc "OVERFLOW"/"ENGINE_THREW"/"NO_EXPRESSION") và
/// <see cref="Exception"/> giữ nguyên exception nếu bộ đánh giá ném (để caller log).
/// </summary>
public readonly record struct PolicyEvalOutcome(decimal? Value, string? FailReason, System.Exception? Exception)
{
    public bool Ok => Value is not null;
}

/// <summary>
/// SCP1 — MỘT chỗ duy nhất chạy biểu thức chính sách chấm điểm: parse → evaluate → phân loại lỗi.
///
/// <para>Đường chấm LIVE (B6 phỏng vấn · B7 sàng CV) và đường XEM TRƯỚC / ÁP (B8) BẮT BUỘC đi qua
/// đây để "điểm preview hiện ra = điểm apply ghi = điểm một lần chấm mới sẽ cho". Trước bản này mỗi
/// nơi tự viết lại khối try/catch giống hệt — đúng kiểu "hai hợp đồng trôi xa nhau âm thầm" repo đã
/// dính nhiều lần.</para>
///
/// <para>KHÔNG log, KHÔNG quyết định lùi-an-toàn, KHÔNG ném vì bất biến (total_questions/need_count = 0):
/// những thứ đó phụ thuộc ngữ cảnh gọi (B6 khác B7) nên để nguyên ở call-site.</para>
/// </summary>
public static class ScoringPolicyRunner
{
    public static PolicyEvalOutcome Evaluate(string? expression, ScoringContext ctx)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return new PolicyEvalOutcome(null, "NO_EXPRESSION", null);

        try
        {
            var parsed = ScoringExpression.Parse(expression);
            if (!parsed.Ok)
                return new PolicyEvalOutcome(
                    null, parsed.Errors.Count > 0 ? parsed.Errors[0].Code : "PARSE_ERROR", null);

            var eval = parsed.Evaluate(ctx);
            return eval.Ok
                ? new PolicyEvalOutcome(eval.Value, null, null)   // ∈ [0,100]; ngoài dải ⇒ eval.Ok = false
                : new PolicyEvalOutcome(
                    null, eval.Errors.Count > 0 ? eval.Errors[0].Code : "EVAL_ERROR", null);
        }
        catch (System.OverflowException ex)
        {
            return new PolicyEvalOutcome(null, "OVERFLOW", ex);
        }
        catch (System.Exception ex)
        {
            return new PolicyEvalOutcome(null, "ENGINE_THREW", ex);
        }
    }
}
