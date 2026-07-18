namespace Isas.InterviewService.Models;

// Phỏng vấn THÍCH ỨNG — cấu hình B2C (section "Adaptive"). B2B lấy toggle/trần từ campaign (cross-service).
// Enabled=false (mặc định) = feature flag TẮT → giữ nguyên luồng batch tĩnh cũ (rollout an toàn).
public class AdaptiveOptions
{
    public const string SectionName = "Adaptive";

    // Bật vòng lặp câu-kế-động cho B2C. Tắt → sinh câu hỏi batch như cũ (không đụng B2B).
    public bool Enabled { get; set; } = false;

    // Số câu SEED giữ lại cho B2C khi bật adaptive (AIService vẫn trả ~5 câu → lấy N đầu). 1 = hội thoại
    // ngay từ lượt 2 (khớp lựa chọn "B2C 1 seed"). Adaptive tắt → bỏ qua (giữ cả bộ câu như cũ).
    public int SeedCount { get; set; } = 1;

    // Trần TỔNG số câu (seed + thích ứng) đóng dấu lên session lúc tạo. 0 = không trần cứng.
    public int MaxQuestions { get; set; } = 10;

    // Trần số câu THÍCH ỨNG thêm, đóng dấu lên session lúc tạo. 0 = không trần cứng.
    public int MaxFollowUps { get; set; } = 3;
}
