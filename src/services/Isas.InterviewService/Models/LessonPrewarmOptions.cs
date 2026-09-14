namespace Isas.InterviewService.Models;

// Prewarm lý thuyết bài học (sinh NỀN trước khi người học mở): bài 1 ngay sau khi tạo roadmap + bài kế
// tiếp khi mở bài. Vì sao: mỗi bài sinh mất 20–50s ĐỒNG BỘ trong GET (đo prod 2026-09-14: 38–52s); người
// học đọc bài N là lúc rẻ nhất để sinh sẵn bài N+1. Chi phí: ≤1 bài sinh thừa mỗi phiên duyệt (bài 1 vốn
// FE đã prefetch ⇒ trung hoà). BẬT mặc định — đây là vá hành vi đo được (tiền lệ F11), không phải tính
// năng mới; tắt bằng `LessonPrewarm__Enabled=false` khi cần.
public class LessonPrewarmOptions
{
    public const string SectionName = "LessonPrewarm";

    public bool Enabled { get; set; } = true;

    // Trần hàng đợi in-memory. Đầy → bỏ (DropWrite), người học mở bài vẫn sinh on-demand như cũ.
    public int QueueCapacity { get; set; } = 64;
}
