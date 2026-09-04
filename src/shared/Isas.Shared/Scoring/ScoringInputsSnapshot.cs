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
/// <remarks>
/// RNK1 · HĐ-1 — APPEND-ONLY. Ba trường <c>SeedAnswered</c>/<c>SeedTotal</c>/<c>SkipPenalty</c> thêm ở
/// CUỐI, đều nullable: row jsonb ghi TRƯỚC RNK1 thiếu khoá ⇒ deserialize ra null (System.Text.Json
/// bỏ qua khoá thiếu). null nghĩa là "KHÔNG BIẾT" — <see cref="SkipPenaltyRule"/> coi như không phạt,
/// <see cref="ScoringContext.ForInterview"/> KHÔNG đặt biến <c>seed_*</c>.
/// </remarks>
public sealed record ScoringInputsSnapshot(
    IReadOnlyList<CriterionInputSnapshot> Criteria,
    int Answered,
    int TotalQuestions,
    int? SeedAnswered = null,
    int? SeedTotal = null,
    bool? SkipPenalty = null)
{
    /// <summary>Chuyển sang input của bộ đánh giá B1 (ánh xạ <c>pct</c>/<c>weight</c> per-criterion,
    /// giữ <c>answered</c>/<c>totalQuestions</c> + <c>seed*</c>/<c>skipPenalty</c>). <c>name</c>/
    /// <c>maxScore</c>/<c>criterionId</c> lưu kèm cho hiển thị và biến tương lai — bộ đánh giá hiện
    /// tại không cần.
    ///
    /// <para>⚠ Chặng dây HAY RỤNG nhất: quên truyền <c>SeedAnswered</c>/<c>SeedTotal</c>/<c>SkipPenalty</c>
    /// ở đây thì luật câu bỏ trống (HĐ-2) im lặng vô hiệu ở đường preview/apply (B8) — cùng snapshot
    /// vẫn cho ra điểm KHÁC đường chấm thường. Có test round-trip khoá đủ 6 trường.</para></summary>
    public InterviewScoringInputs ToInterviewInputs() => new(
        Criteria.Select(c => new CriterionScore(c.Pct, c.Weight)).ToList(),
        Answered,
        TotalQuestions,
        SeedAnswered,
        SeedTotal,
        SkipPenalty);
}

/// <summary>Một tiêu chí trong bó biến thô: <c>pct</c> đã chuẩn hoá [0,100], kèm <c>weight</c>,
/// <c>maxScore</c>, <c>name</c> để không mất thông tin gốc. RNK1 · HĐ-1/HĐ-5 — <c>CriterionId</c>
/// (= <c>campaign_criteria.id</c>, qua <c>rubric_criteria.source_criterion_id</c>) thêm ở CUỐI,
/// nullable: điểm sàn theo tiêu chí (HĐ-5) khớp theo id này; null (snapshot cũ / RNK1-B1 chưa điền
/// — B4 điền) ⇒ khớp theo TÊN.</summary>
public sealed record CriterionInputSnapshot(
    string Name, decimal Pct, decimal Weight, int MaxScore, Guid? CriterionId = null);
