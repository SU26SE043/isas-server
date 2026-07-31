namespace Isas.InterviewService.Services.Interfaces;

// RAG grounding — ống dẫn ingest từ Context7 (context7.com/api/v2, Bearer ctx7sk-…). Search thư viện +
// lấy context (snippet markdown đã phân đoạn, mỗi snippet 1 source_url). Unit test MOCK cái này.
public interface IContext7Client
{
    // GET /libs/search — tìm thư viện ứng viên (id, title, reputation, số snippet).
    Task<IReadOnlyList<Context7Library>> SearchAsync(
        string libraryName, string? query, CancellationToken ct = default);

    // GET /context — snippet của 1 thư viện theo topic. Mỗi snippet = 1 chunk (đã phân đoạn sẵn).
    Task<IReadOnlyList<Context7Snippet>> GetContextAsync(
        string libraryId, string topic, CancellationToken ct = default);
}

public record Context7Library(string Id, string Title, string? Reputation, int Snippets);

// title + content (mô tả + code ghép sẵn) + sourceUrl per-snippet (citation chuẩn).
public record Context7Snippet(string Title, string Content, string? SourceUrl);
