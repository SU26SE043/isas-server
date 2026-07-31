namespace Isas.InterviewService.Services;

// RAG grounding — lỗi gọi Context7 (transport/status/JSON). Controller map → 502.
public class Context7Exception : Exception
{
    public Context7Exception(string message) : base(message) { }
    public Context7Exception(string message, Exception inner) : base(message, inner) { }
}

// Context7 free-tier giới hạn thấp → 429. Giữ Retry-After để controller trả về (mẫu GitHubRateLimitException).
public class Context7RateLimitException(string message, string? retryAfter) : Context7Exception(message)
{
    public string? RetryAfter { get; } = retryAfter;
}
