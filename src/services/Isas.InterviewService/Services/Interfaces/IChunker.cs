using Isas.InterviewService.Enums;

namespace Isas.InterviewService.Services.Interfaces;

// RAG grounding — tách văn bản thành chunk theo source_type (đòn bẩy recall lớn nhất). Ranh giới NGỮ
// NGHĨA (heading/đoạn) quan trọng hơn con số token chính xác; token đếm xấp xỉ ký tự là đủ.
public interface IChunker
{
    // Context7: content = 1 snippet đã phân đoạn sẵn → 1 chunk (split chỉ khi vượt budget).
    // Url: content = HTML → ChunkHtmlByHeading (h1–h3 trước, rồi cửa sổ token/overlap).
    // Manual: content = markdown/plain → tách theo `##`/đoạn rồi cửa sổ.
    // SectionTitle = heading của section (null → caller dùng title nguồn).
    IReadOnlyList<TextChunk> Chunk(KnowledgeSourceType type, string content);
}

public record TextChunk(string Content, string? SectionTitle);
