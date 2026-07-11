namespace Isas.CampaignService.Services
{
    /// <summary>
    /// C14 — 1 tiêu chí gửi kèm job sàng CV. Worker KHÔNG tự đọc DB → Campaign đẩy tiêu chí sẵn
    /// (TÁI DÙNG <c>campaign_criteria</c>). <c>CriterionId</c> để worker trả lại đúng id trong callback.
    /// </summary>
    public record CvScreeningCriterion(Guid CriterionId, string Name, string? Description, int MaxScore);

    /// <summary>
    /// C14 — Job đẩy vào <c>cv_screening_queue</c> cho 1 CV <c>Filtered</c> (ai.md §Pipeline sàng CV).
    /// KHÔNG Whisper/audio: <c>CvText</c> nằm sẵn. <c>CallbackBase</c> trỏ CampaignService (worker mặc
    /// định trỏ Interview nên B2B phải override). Worker sàng CV thật KHÔNG thuộc phạm vi C14.
    /// </summary>
    public record CvScreeningJob(
        Guid CandidateId,
        string CvText,
        string? JobCategory,
        string? JdText,
        List<CvScreeningCriterion> Criteria,
        string CallbackBase);

    public interface ICvScreeningPublisher
    {
        Task PublishAsync(CvScreeningJob job, CancellationToken ct = default);
    }
}
