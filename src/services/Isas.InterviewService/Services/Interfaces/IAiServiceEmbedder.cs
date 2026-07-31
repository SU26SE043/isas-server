namespace Isas.InterviewService.Services.Interfaces;

// RAG grounding — gọi AIService `POST /api/v1/embed` (Contract 1). AI KHÔNG ghi DB (GEN-4) — chỉ sinh
// vector. taskType: "RETRIEVAL_DOCUMENT" (ingest) / "RETRIEVAL_QUERY" (truy vấn) — gemini-embedding-001
// đa ngôn ngữ nên query tiếng Việt tìm thẳng chunk tiếng Anh (cross-lingual, khỏi dịch). Lỗi → AiServiceException.
// Unit test MOCK cái này (không cần AIService thật).
public interface IAiServiceEmbedder
{
    Task<IReadOnlyList<float[]>> EmbedAsync(
        IReadOnlyList<string> texts, string taskType, CancellationToken ct = default);
}
