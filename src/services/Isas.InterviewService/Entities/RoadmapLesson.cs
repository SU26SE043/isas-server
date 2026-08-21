using Isas.InterviewService.DTOs;
using Isas.InterviewService.Enums;

namespace Isas.InterviewService.Entities;

// BC12 — lesson trong 1 milestone. order_no UNIQUE(milestone_id, order_no).
// theory_content: AI sinh LẦN ĐẦU mở lesson (lazy, BC14) — BC12 tạo với null.
// session_id: session luyện gắn lesson, set khi /start (BC14) — FK Restrict → practice_sessions.
public class RoadmapLesson
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid MilestoneId { get; set; }
    public RoadmapMilestone Milestone { get; set; } = null!;

    public int OrderNo { get; set; }

    public string Title { get; set; } = null!;

    // markdown lý thuyết — null cho tới khi mở lesson (BC14).
    public string? TheoryContent { get; set; }
    public DateTime? TheoryGeneratedAt { get; set; }

    // F15 (FR09) — tài liệu học gợi ý, sinh CÙNG lượt với lý thuyết (không thêm lần gọi AI).
    // jsonb, non-null (mặc định rỗng). RỖNG LÀ HỢP LỆ: AI có thể không gợi ý được tài liệu nào,
    // hoặc mọi link nó đưa ra đều bị allowlist tên miền loại bỏ (AIService app/resources.py).
    public List<LessonResource> Resources { get; set; } = [];

    // Ref FK Restrict → practice_sessions (session luyện của lesson) — set khi /start (BC14).
    public Guid? SessionId { get; set; }

    // RAG grounding (Cách 2 — precompute). Snapshot chunk truy hồi LÚC TẠO roadmap (RoadmapService.CreateAsync):
    // batch-embed tên bài → Qdrant search → lưu 3–4 chunk (chunkId + content + sourceUrl + sourceTitle) vào đây.
    // Lúc MỞ lesson (OpenLessonAsync) đọc thẳng snapshot này feed /generate-lesson-theory → KHÔNG retrieve
    // realtime (khỏi thêm độ trễ đường lazy). Content cần để feed prompt AIService.
    // 3 TRẠNG THÁI như PracticeQuestion.GroundingRefs: null = roadmap cũ (precompute chưa chạy) → không nhãn;
    // [] = precompute chạy nhưng corpus không phủ → ungrounded; non-empty = có nguồn. jsonb nullable.
    public List<GroundingChunk>? GroundingRefs { get; set; }

    public LessonStatus Status { get; set; } = LessonStatus.Theory;

    // Lịch sử MỌI lần làm bài này (kể cả các lần làm lại). `SessionId` ở trên vẫn trỏ lần MỚI NHẤT.
    // Cascade theo lesson_id.
    public ICollection<RoadmapLessonAttempt> Attempts { get; set; } = [];
}

/// <summary>
/// F15 — 1 tài liệu học gợi ý. Lưu jsonb trong <see cref="RoadmapLesson.Resources"/>, KHÔNG tách
/// bảng: luôn đọc/ghi trọn gói theo lesson, không truy vấn/lọc theo tài liệu riêng lẻ.
/// </summary>
/// <param name="Title">Tên tài liệu/khoá học/chương sách.</param>
/// <param name="Type">Doc · Course · Book · Video · Article (AIService chuẩn hoá, lạ → Doc).</param>
/// <param name="Publisher">Nơi phát hành, có thể null.</param>
/// <param name="Url">
/// CÓ THỂ NULL VÌ CÓ CHỦ ĐÍCH. Link do LLM sinh chỉ được giữ khi https + tên miền thuộc allowlist
/// ở AIService (<c>app/resources.py</c>); host lạ → url bị bỏ, tài liệu vẫn giữ tên. Allowlist bảo
/// đảm link trỏ đúng TÊN MIỀN có thật, KHÔNG bảo đảm đường dẫn tồn tại (không fetch để xác minh) —
/// nên FE phải gắn nhãn "chưa kiểm chứng" cạnh link.
/// </param>
public record LessonResource(string Title, string Type, string? Publisher, string? Url);
