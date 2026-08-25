using Isas.InterviewService.Enums;

namespace Isas.InterviewService.Entities;

// MIS1-B4 — 1 LỖI SAI trích từ một buổi luyện đã chấm, gắn cho ĐÚNG roadmap này (không phải bảng
// dùng chung xuyên roadmap). Trích lúc TẠO roadmap (RoadmapMistakeLoader), CHƯA nối vào AI ở bước
// này (B5). Bảng CON thay vì jsonb trên `roadmaps`: 12 lỗi × ~3KB ≈ 36KB/hàng chắc chắn TOAST, mà
// `roadmaps` được nạp ở MỌI đường đọc kể cả mở 1 lesson (RoadmapLessonService.cs ThenInclude(Roadmap))
// — mỗi lần mở lesson sẽ detoast 36KB dữ liệu không dùng tới.
//
// `mistake_key` ("m1".."m12") do RoadmapMistakeLoader MINT MỘT LẦN theo đúng thứ tự đã sort — không
// nơi nào khác được re-derive nó từ chỉ số mảng. UNIQUE(roadmap_id, mistake_key) ép điều đó ở TẦNG DB,
// không chỉ là trang trí.
//
// `criterion_id` (FK Restrict) + `criterion_name` (snapshot) cùng tồn tại theo đúng khuôn
// SessionCriterionScore/AnswerScore: id để join/lọc chính xác (đổi tên tiêu chí không làm rớt lỗi),
// tên để hiển thị không phải join lại — rubric có Version, tiêu chí có thể bị xoá/đổi tên sau khi lỗi
// đã được trích.
//
// `answer_id` (FK SetNull) KHÔNG có navigation trên entity này (mẫu Roadmap.CvId /
// RoadmapLesson.SessionId) — hàng lỗi tự mang đủ snapshot Question/Answer/Reasoning nên không cần
// answer sống mới đọc được; SetNull (không Restrict) vì xoá buổi luyện gốc không được phép làm hỏng
// một roadmap đã tạo trước đó.
public class RoadmapMistake
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid RoadmapId { get; set; }
    public Roadmap Roadmap { get; set; } = null!;

    // "m1".."m12" — mint MỘT LẦN trong RoadmapMistakeLoader theo thứ tự đã sort.
    public string MistakeKey { get; set; } = null!;

    public Guid CriterionId { get; set; }
    public RubricCriterion Criterion { get; set; } = null!;

    // Snapshot tên tiêu chí LÚC TRÍCH — rubric có Version, đổi tên sau đó không làm lệch nhãn cũ.
    public string CriterionName { get; set; } = null!;

    // FK SetNull → practice_answers. Nullable: xoá answer gốc (vd xoá session) không kéo sập hàng lỗi.
    public Guid? AnswerId { get; set; }

    public string Question { get; set; } = null!;
    public string Answer { get; set; } = null!;
    public string Reasoning { get; set; } = null!;

    // Nullable — PracticeAnswer.SampleAnswer bản thân đã nullable (F13: null = chưa chấm/model không trả).
    public string? SampleAnswer { get; set; }

    // numeric(5,2) — nguồn là phép chia, làm tròn lúc GỬI (B5), không phải lúc LƯU (luật "lưu đủ").
    public decimal ScorePct { get; set; }
    public decimal ThresholdPct { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
