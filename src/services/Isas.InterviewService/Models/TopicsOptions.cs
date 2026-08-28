namespace Isas.InterviewService.Models;

// TOP1-B5 — kill-switch danh mục chủ đề luyện tập B2C (mẫu GroundingOptions). Mặc định TẮT: pool
// mới vừa seed (B1/B2), bật sớm khi FE/admin chưa kiểm được nội dung thì rủi ro y hệt bật
// Grounding trước khi có corpus — giữ tắt để luồng tạo buổi cũ nguyên vẹn tới khi verify xong.
public class TopicsOptions
{
    public const string SectionName = "Interview:Topics";

    public bool Enabled { get; set; } = false;
}
