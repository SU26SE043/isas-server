using Isas.PaymentService.DTOs;

namespace Isas.PaymentService.Services
{
    /// <summary>F22 (FR18) — ghi nhận + tổng hợp tiêu thụ token/chi phí AI.</summary>
    public interface IAiUsageService
    {
        /// <summary>Ghi 1 lượt gọi LLM (đơn giá được SNAPSHOT tại đây). Trả về Id dòng vừa ghi.</summary>
        Task<Guid> RecordAsync(RecordAiUsageRequest req, CancellationToken ct = default);

        Task<AiUsageReportResponse> GetReportAsync(
            DateTime from, DateTime to, AiUsageGranularity granularity, CancellationToken ct = default);
    }
}
