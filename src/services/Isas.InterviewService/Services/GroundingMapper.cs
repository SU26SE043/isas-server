using Isas.InterviewService.DTOs;

namespace Isas.InterviewService.Services;

// RAG grounding — map citedChunkIds (AIService trả) → Citation hiển thị, RESOLVE url/title từ chính tập
// grounding đã cấp. GUARD chống bịa nguồn (drop by-construction): id AIService cite mà KHÔNG nằm trong tập
// grounding cấp vào → BỎ. Cùng nhóm bảo vệ với "AIService DROP citedChunkId ngoài tập" (Contract 2) — 2
// lớp: AIService drop lúc sinh, InterviewService drop lại khi resolve (phòng thủ chiều sâu).
public static class GroundingMapper
{
    public static List<Citation> ResolveCitations(
        IReadOnlyList<GroundingChunk> provided, IReadOnlyList<string>? citedChunkIds)
    {
        var result = new List<Citation>();
        if (citedChunkIds is null || citedChunkIds.Count == 0 || provided.Count == 0)
            return result;

        // Dedup tập cấp theo chunkId (nguồn sự thật để resolve). Chunk id vốn duy nhất; defensive.
        var byId = new Dictionary<string, GroundingChunk>(StringComparer.Ordinal);
        foreach (var g in provided) byId[g.ChunkId] = g;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in citedChunkIds)
            if (byId.TryGetValue(id, out var g) && seen.Add(id))
                result.Add(new Citation(g.ChunkId, g.SourceUrl, g.SourceTitle));

        return result;
    }

    // Snapshot grounding (roadmap_lessons.grounding_refs) → Citation hiển thị (bỏ content). GIỮ 3 trạng
    // thái: null → null (không grounding), [] → [] (ungrounded), non-empty → grounded.
    public static List<Citation>? ToCitations(IReadOnlyList<GroundingChunk>? refs)
        => refs?.Select(g => new Citation(g.ChunkId, g.SourceUrl, g.SourceTitle)).ToList();
}
