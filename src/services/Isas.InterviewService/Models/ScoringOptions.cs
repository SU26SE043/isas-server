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

    // P1-1 — B2C (Deadline null) KHÔNG có hard-deadline nên SessionAbandonSweeper (chỉ quét Deadline!=null)
    // không bao giờ đụng → session tạo-rồi-bỏ giữ credit reserve VĨNH VIỄN. Coi buổi B2C là "bỏ ngang"
    // khi KHÔNG có hoạt động (không tạo answer mới) quá số phút này → phát SessionAbandoned để Payment
    // release credit ví User. CONSERVATIVE (mặc định 120') để KHÔNG bao giờ quét nhầm người ĐANG luyện.
    public int B2CInactivityMinutes { get; set; } = 120;

    // Settlement-outbox (Option A) — SettlementReconciler phát lại settlement-event cho session B2C
    // terminal (Scored/SessionAbandoned) mà settlement_published_at còn null (publish hụt lúc đóng session).
    // GRACE: chờ tối thiểu sau CompletedAt trước khi phát lại — chừa cửa sổ cho publish đường-chính vừa
    // xong nhưng marker chưa kịp commit (tránh phát trùng vô ích; Payment idempotent nên vẫn an toàn nếu
    // trùng). <=0 = TẮT reconciler (an toàn: không tự phát lại).
    public int SettlementRepublishGraceMinutes { get; set; } = 2;

    // Settlement-outbox — chỉ ngó lại session đóng trong khung này (giới hạn khối lượng quét + tránh
    // "hồi sinh" event quá cũ khi bật lần đầu trên DB lịch sử). <=0 = TẮT reconciler.
    public int SettlementRepublishLookbackHours { get; set; } = 24;
}
