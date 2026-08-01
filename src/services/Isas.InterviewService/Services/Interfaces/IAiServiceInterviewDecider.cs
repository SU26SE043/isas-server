using Isas.InterviewService.DTOs;

namespace Isas.InterviewService.Services.Interfaces;

// Phỏng vấn THÍCH ỨNG — typed HttpClient gọi AIService `/decide-next` (máy-máy, X-Internal-Token).
// Gửi audio key (AIService transcribe đồng bộ) + lịch sử + tiêu chí → nhận hành động kế tiếp + câu hỏi.
// INT-17b: đầu vào gói trong `AdaptiveDecisionRequest` thay vì rải 9 tham số (xem lý do CS0854 ở record đó).
public interface IAiServiceInterviewDecider
{
    Task<DecideNextResult> DecideNextAsync(AdaptiveDecisionRequest request, CancellationToken ct = default);
}
