namespace Isas.Shared.Scoring;

/// <summary>Điểm % của MỘT tiêu chí phỏng vấn + trọng số của nó (SCP1 · nguồn thô cho biến Interview).
/// <paramref name="Pct"/> ∈ [0,100] (đã kẹp ở đường chấm E8); <paramref name="Weight"/> &gt; 0.</summary>
public sealed record CriterionScore(decimal Pct, decimal Weight);

/// <summary>
/// SCP1 — DỮ LIỆU THÔ của một buổi phỏng vấn đã chấm, đủ để suy ra mọi biến PHỎNG VẤN của HĐ-1.
/// InterviewService dựng cái này từ <c>answer_scores</c> + rubric rồi gửi kèm event
/// <c>SessionScored</c>; CampaignService lưu lại để <c>preview</c>/<c>apply</c> tính lại được.
/// </summary>
public sealed record InterviewScoringInputs(
    IReadOnlyList<CriterionScore> Criteria,
    int Answered,
    int TotalQuestions);

/// <summary>
/// SCP1 — DỮ LIỆU THÔ của một hồ sơ đã sàng CV, đủ để suy ra mọi biến SÀNG CV của HĐ-1.
/// Nguồn nằm sẵn trong CampaignService (<c>cv_submission</c> + <c>job_needs</c>), không cần
/// InterviewService.
/// </summary>
public sealed record CvScreeningScoringInputs(
    int StrongCount,
    int PartialCount,
    int WeakCount,
    int NeedCount,
    int MustHaveTotal,
    int MustHaveMet);
