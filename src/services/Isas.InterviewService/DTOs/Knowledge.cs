using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Isas.InterviewService.Enums;

namespace Isas.InterviewService.DTOs;

// RAG grounding — hợp đồng chung với AIService (W1) + FE (W3). Xem grounding-contracts.md.

// Options dùng chung cho serialize jsonb grounding_refs (practice_questions + roadmap_lessons) — 1 chỗ
// để 2 config + service không lệch nhau (camelCase Web defaults, khớp cách các jsonb khác trong service).
public static class KnowledgeJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}

// ── Grounding (Contract 2/4) ────────────────────────────────────────────────
// 1 chunk truy hồi từ Qdrant. GỬI NGUYÊN shape này vào AIService `grounding[]`
// (chunkId để model tham chiếu ngược; content = nội dung để đọc; sourceUrl/sourceTitle để cite).
// Cũng là snapshot LƯU vào roadmap_lessons.grounding_refs (content cần để feed /generate-lesson-theory
// lúc mở lesson mà KHÔNG retrieve lại — Cách 2 precompute).
public record GroundingChunk(string ChunkId, string Content, string SourceUrl, string SourceTitle);

// Citation ĐÃ RESOLVE để hiển thị (không kèm content). Lưu practice_questions.grounding_refs +
// surface trong QuestionResponse/LessonResponse. Rỗng → nội dung `ungrounded` (FE nhãn "chưa có nguồn").
public record Citation(string ChunkId, string SourceUrl, string SourceTitle);

// ── Admin Knowledge API (Contract 3) ────────────────────────────────────────
public record KnowledgeSourceResponse(
    Guid Id,
    string Title,
    string? JobCategory,
    string SourceType,
    string? SourceRef,
    string? Reputation,
    string Status,
    int ChunkCount,
    DateTime CreatedAt);

// POST /api/admin/knowledge — nạp nguồn dán tay (Manual) hoặc URL (Url).
public record CreateKnowledgeRequest(
    [Required] string? Title,
    [Required] JobCategory? JobCategory,
    [Required] KnowledgeSourceType? SourceType,
    string? Content,   // Manual — markdown/plain dán tay
    string? Url);      // Url — HTML để tải + tách heading

// GET /api/admin/knowledge/context7/search — 1 thư viện ứng viên từ Context7.
public record Context7SearchResult(
    string Id,
    string Title,
    string? Reputation,
    int Snippets);

// POST /api/admin/knowledge/context7/ingest — nạp N chủ đề của 1 thư viện Context7.
public record Context7IngestRequest(
    [Required] string? LibraryId,
    [Required] List<string>? Topics,
    [Required] JobCategory? JobCategory);
