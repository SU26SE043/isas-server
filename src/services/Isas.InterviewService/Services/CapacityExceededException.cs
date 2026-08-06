namespace Isas.InterviewService.Services;

public sealed class CapacityExceededException(int maxConcurrentSessions)
    : Exception($"Hệ thống đang có tối đa {maxConcurrentSessions} người thi. Vui lòng thử lại sau ~1 phút.")
{
    public int MaxConcurrentSessions { get; } = maxConcurrentSessions;
}
