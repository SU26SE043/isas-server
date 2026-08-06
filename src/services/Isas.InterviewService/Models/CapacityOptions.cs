namespace Isas.InterviewService.Models;

/// <summary>Trần session đang chạy toàn nền tảng. 0 = tắt, giữ hành vi cũ.</summary>
public sealed class CapacityOptions
{
    public const string SectionName = "Capacity";
    public int MaxConcurrentSessions { get; set; }
}
