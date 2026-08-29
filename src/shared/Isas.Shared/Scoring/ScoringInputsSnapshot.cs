namespace Isas.Shared.Scoring;

/// <summary>
/// SCP1 · B5 — BÓ BIẾN ĐẦU VÀO THÔ của một lần chấm phỏng vấn, đi kèm event <c>SessionScored</c> và
/// ghim vào <c>campaign_rankings.scoring_inputs</c> (jsonb).
///
/// <para><b>Lưu RAW per-criterion — KHÔNG lưu scalar đã tính</b> (<c>weighted_avg_pct</c>,
/// <c>avg_pct</c>, <c>min_pct</c>…). Lý do: lưu số đã tính là khoá mình vào bộ biến của HÔM NAY —
/// thêm một biến mới sau này (append-only, HĐ-1) thì KHÔNG tính lại được cho hàng lịch sử. B8
/// (xem trước / áp) dựng lại <see cref="InterviewScoringInputs"/> từ đây rồi chạy
/// <c>ScoringExpression</c>.</para>
/// </summary>
public sealed record ScoringInputsSnapshot(
    IReadOnlyList<CriterionInputSnapshot> Criteria,
    int Answered,
    int TotalQuestions)
{
    /// <summary>Chuyển sang input của bộ đánh giá B1 (ánh xạ <c>pct</c>/<c>weight</c> per-criterion,
    /// giữ <c>answered</c>/<c>totalQuestions</c>). <c>name</c>/<c>maxScore</c> lưu kèm cho hiển thị
    /// và biến tương lai — bộ đánh giá hiện tại không cần.</summary>
    public InterviewScoringInputs ToInterviewInputs() => new(
        Criteria.Select(c => new CriterionScore(c.Pct, c.Weight)).ToList(),
        Answered,
        TotalQuestions);
}

/// <summary>Một tiêu chí trong bó biến thô: <c>pct</c> đã chuẩn hoá [0,100], kèm <c>weight</c>,
/// <c>maxScore</c>, <c>name</c> để không mất thông tin gốc.</summary>
public sealed record CriterionInputSnapshot(string Name, decimal Pct, decimal Weight, int MaxScore);
