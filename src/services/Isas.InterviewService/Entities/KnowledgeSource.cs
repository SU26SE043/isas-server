using Isas.InterviewService.Enums;

namespace Isas.InterviewService.Entities;

// RAG grounding — METADATA nguồn tri thức. Chunk (vector) KHÔNG nằm ở Postgres mà ở Qdrant
// (collection `knowledge`, mỗi chunk = 1 point). Bảng này chỉ giữ metadata để list/xoá/reindex; xoá
// nguồn = xoá point Qdrant theo source_id TRƯỚC rồi mới xoá row này (thứ tự chống orphan vector).
public class KnowledgeSource
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Title { get; set; } = null!;

    // Nghề áp dụng (BA/BE/FE). null = chung mọi nghề (không lọc được theo jobCategory khi retrieve).
    public JobCategory? JobCategory { get; set; }

    public KnowledgeSourceType SourceType { get; set; }

    // Context7 libraryId (vd `/reactjs/react.dev`) · URL · null (Manual dán tay).
    public string? SourceRef { get; set; }

    // Nội dung GỐC để re-chunk khi reindex (KHÔNG phải chunk — chunk vẫn ở Qdrant):
    //   Manual → markdown/plain đã dán · Context7 → topics (mỗi dòng 1 topic, refetch qua SourceRef) ·
    //   Url → null (reindex refetch từ SourceRef). null → reindex báo không hỗ trợ.
    public string? RawContent { get; set; }

    // Uy tín nguồn (Context7 trả kèm; Manual/Url do admin khai). Chỉ hiển thị.
    public string? Reputation { get; set; }

    public KnowledgeStatus Status { get; set; } = KnowledgeStatus.Active;

    // Số chunk đã upsert vào Qdrant cho nguồn này (đối soát + hiển thị).
    public int ChunkCount { get; set; }

    // Admin đã tạo (từ JWT sub); ref lỏng, KHÔNG FK xuyên service (GEN-2).
    public Guid? CreatedBy { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
