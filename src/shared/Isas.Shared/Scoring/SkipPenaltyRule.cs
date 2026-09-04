namespace Isas.Shared.Scoring;

/// <summary>
/// RNK1 · HĐ-2 / CAMP-21 — LUẬT ENGINE (không cấu hình): câu HR khai mà ứng viên bỏ trống tính 0
/// điểm. Áp bằng cách NHÂN điểm sau khi đánh giá biểu thức chính sách với tỷ lệ câu GỐC đã trả lời:
///
/// <code>total = clamp(expr × seed_answered / seed_total, 0, 100)   khi skip_penalty = true</code>
///
/// <para><b>MỘT hàm dùng chung</b> cho cả đường chấm LIVE (InterviewService · SessionScoringNotifier)
/// lẫn đường XEM TRƯỚC / ÁP (CampaignService · ScoringPolicyService). Hai đoạn code nhân riêng sẽ trôi
/// xa nhau mà không có triệu chứng — "điểm preview hiện ra" phải = "điểm apply ghi" = "điểm một lần
/// chấm mới". Có test khoá byte-equal.</para>
///
/// <para><b>Không phạt</b> (trả <paramref name="expr"/> nguyên) khi:
/// <list type="bullet">
///   <item><see cref="InterviewScoringInputs.SkipPenalty"/> != <c>true</c> — campaign có TRƯỚC RNK1
///     (backfill <c>skip_penalty = false</c>, KHÔNG đổi thước đo giữa chiến dịch đang chạy), hoặc
///     snapshot cũ (null).</item>
///   <item><see cref="InterviewScoringInputs.SeedTotal"/> null hoặc ≤ 0 — snapshot trước RNK1, hoặc
///     buổi B2B lỗi materialize không có câu gốc nào. Nhân cho 0 sẽ phạt ứng viên vì lỗi hệ thống;
///     giữ nguyên điểm mặc định an toàn hơn (biến <c>seed_completeness</c> trong catalog thì vẫn = 0
///     ở ca này — đó là quyết định của người viết biểu thức, không phải của luật engine).</item>
/// </list></para>
/// </summary>
public static class SkipPenaltyRule
{
    public static decimal Apply(decimal expr, InterviewScoringInputs input)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (input.SkipPenalty != true) return expr;
        if (input.SeedTotal is not int seedTotal || seedTotal <= 0) return expr;

        var seedAnswered = input.SeedAnswered.GetValueOrDefault();
        var penalised = expr * seedAnswered / seedTotal;
        // Làm tròn 2 chữ số TẠI ĐÂY (điểm hệ thống luôn 2dp; DefaultInterviewTotal / nhánh policy-ok
        // đã round trước khi vào đây). Round ở một chỗ ⇒ đường chấm thường (qua numeric(5,2) DB) và
        // đường preview/apply (in-memory) cho ra CÙNG con số. `Math.Round(_, 2)` mặc định (ToEven) —
        // khớp mọi chỗ round điểm khác trong SessionScoringNotifier / ScoringPolicyService.
        return Math.Round(Math.Clamp(penalised, 0m, 100m), 2);
    }
}
