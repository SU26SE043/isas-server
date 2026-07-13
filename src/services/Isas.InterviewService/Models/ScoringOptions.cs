namespace Isas.InterviewService.Models;

// BC9/E10 — cấu hình chấm điểm.
public class ScoringOptions
{
    public const string SectionName = "Scoring";

    // Tiêu chí có percentage < ngưỡng này (mặc định 50%) bị gắn needs_improvement.
    // Ngưỡng CHỐT lúc tính (đổi ngưỡng sau không hồi tố kết quả đã lưu).
    public decimal ImprovementThresholdPct { get; set; } = 50m;

    // E10 — self-consistency: chấm 1 answer N lần (mỗi lần 1 attempt_no) rồi lấy median/tiêu chí.
    // Mặc định 1 = TẮT (opt-in) để giữ chi phí Whisper/Gemini (throughput đã là trần — ai.md §Vấn đề);
    // bật bằng cách đặt >1. N=1 ⇒ median-of-1 = giá trị cũ ⇒ KHÔNG đổi hành vi.
    public int SelfConsistencyN { get; set; } = 1;

    // E10 — spread = max−min điểm giữa các attempt (mỗi tiêu chí). Vượt ngưỡng này (tuyệt đối) →
    // answer.needs_review = true (cờ HR/người luyện xem lại). CHỐT lúc tính (không hồi tố).
    public decimal VarianceThreshold { get; set; } = 1m;

    // E10 — nhiệt độ cho attempt 2..N (tạo dao động thật để ĐO spread). Attempt 1 luôn temp=0
    // (tái lập). Không dùng khi N=1.
    public double SelfConsistencyTemperature { get; set; } = 0.4;

    // E11 — chuẩn "NHẬN XÉT OK": reasoning (mỗi tiêu chí) ngắn hơn ngưỡng này (số ký tự sau trim)
    // → answer.needs_review = true (cờ HR/người luyện soi lại). KHÔNG hard-fail, KHÔNG mất điểm —
    // reuse cờ E10. Đây là guard MỀM defense-in-depth (worker Python đã reject reasoning RỖNG ở
    // nguồn; .NET bắt cả "quá ngắn" + phòng worker/image lệch gửi nhận xét kém → không tin 100%).
    // Mặc định 0 = TẮT (opt-in như SelfConsistencyN) để không hồi tố flag nhận xét ngắn hợp lệ;
    // production bật qua cấu hình Scoring:MinReasoningLen. Điểm AI = gợi ý, HR chốt điểm cuối (E11b).
    public int MinReasoningLen { get; set; } = 0;
}
