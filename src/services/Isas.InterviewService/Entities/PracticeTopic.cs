using Isas.InterviewService.Enums;

namespace Isas.InterviewService.Entities;

// TOP1 — danh mục chủ đề luyện tập B2C, admin quản, chọn được lúc tạo buổi.
// Schema-only ở bước này: CHƯA seed dữ liệu, CHƯA đụng luồng tạo buổi (PracticeService).
public class PracticeTopic
{
    public Guid Id { get; set; } = Guid.NewGuid();

    // Khoá ổn định để tham chiếu chủ đề xuyên version (vd "system-design-basics").
    // KHÔNG phải khoá chính — UNIQUE (TopicKey, Language, Version) mới là ràng buộc thật,
    // vì soft-version (mẫu BC16/CAMP-18) sinh nhiều row cùng TopicKey khác Version.
    public string TopicKey { get; set; } = null!;

    public JobCategory JobCategory { get; set; }

    // Lưu string (GEN-2), tập đóng khớp Seniority enum — mẫu ck_practice_sessions_seniority.
    public string Seniority { get; set; } = null!;

    public string Language { get; set; } = "vi";

    public string Label { get; set; } = null!;

    // Liên kết tuỳ chọn tới tên tiêu chí rubric (vd để lái câu hỏi bám đúng tiêu chí) — nullable vì
    // không phải chủ đề nào cũng ánh xạ 1-1 vào một tiêu chí chấm.
    public string? CriterionName { get; set; }

    public int DisplayOrder { get; set; }

    public bool IsActive { get; set; } = true;

    // Soft-version (mẫu BC16/CAMP-18): sửa nội dung không xoá bản cũ, đánh version mới.
    public int Version { get; set; } = 1;
}
