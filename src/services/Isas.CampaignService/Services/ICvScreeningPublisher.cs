namespace Isas.CampaignService.Services
{
    /// <summary>
    /// 1 nhu cầu công việc gửi kèm job sàng CV. Worker KHÔNG tự đọc DB (GEN-4) → Campaign đẩy sẵn
    /// bộ nhu cầu đã chốt của campaign. <c>NeedId</c> để worker trả lại đúng id trong callback.
    /// </summary>
    public record CvScreeningNeed(string NeedId, string Category, string Text);

    /// <summary>
    /// Job đẩy vào <c>cv_screening_queue</c> cho 1 CV <c>Filtered</c> (ai.md §Pipeline sàng CV).
    /// KHÔNG Whisper/audio: <c>CvText</c> nằm sẵn. <c>CallbackBase</c> trỏ CampaignService (worker mặc
    /// định trỏ Interview nên B2B phải override).
    ///
    /// ⚠ KHÔNG còn mang <c>JdText</c>: JD đã được chưng cất MỘT LẦN thành <c>JobNeeds</c> lúc publish.
    /// Gửi lại JD theo từng hồ sơ vừa tốn token vừa mở đường cho hai ứng viên cùng campaign bị đo
    /// bằng hai bộ yêu cầu khác nhau — đúng thứ bất công CAMP-10 chặn ở đường phỏng vấn.
    /// </summary>
    public record CvScreeningJob(
        Guid CandidateId,
        string CvText,
        string? JobCategory,
        List<CvScreeningNeed> JobNeeds,
        string Language,
        string CallbackBase);

    public interface ICvScreeningPublisher
    {
        Task PublishAsync(CvScreeningJob job, CancellationToken ct = default);
    }
}
