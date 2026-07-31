namespace Isas.InterviewService.Services.Interfaces;

// RAG grounding — tải HTML của 1 URL khi ingest nguồn `Url` (admin-only). Tách interface để unit test
// MOCK (khỏi gọi mạng thật). Lỗi → Context7Exception-tương-đương? → dùng InvalidOperationException để
// controller map 400/502 tuỳ caller; xem impl.
public interface IUrlContentFetcher
{
    Task<string> FetchAsync(string url, CancellationToken ct = default);
}
