namespace Isas.InterviewService.Enums;

// RAG grounding — nguồn tri thức nạp vào kho vector. Lưu string (GEN-2).
public enum KnowledgeSourceType
{
    // Nạp qua Context7 (context7.com) — trả sẵn snippet markdown đã phân đoạn (1 snippet ≈ 1 chunk).
    Context7,
    // URL HTML — tải + tách theo heading (h1–h3) rồi cửa sổ token.
    Url,
    // Dán tay markdown/plain — tách theo `##`/đoạn.
    Manual
}

public enum KnowledgeStatus
{
    Active,
    Archived
}
