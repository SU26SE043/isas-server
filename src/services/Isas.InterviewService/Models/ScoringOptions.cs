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

    // Trần BỎ CUỘC cho một answer đang chờ chấm. Quá ngần này phút kể từ lúc TẠO answer mà vẫn
    // chưa gom đủ N attempt (E10) → thôi đẩy lại, chốt bằng những attempt ĐÃ CÓ (hoặc Skipped nếu
    // chưa có attempt nào) rồi đóng session.
    //
    // Vì sao cần: trước đây `StuckAnswerRepublisher` đẩy lại VÔ HẠN và không sweeper nào phủ trạng
    // thái `Scoring` ⇒ một attempt chết là buổi kẹt vĩnh viễn, mỗi 15 phút lại đốt thêm một lượt
    // Gemini, còn credit thì treo ở `Reserved` (không consume, không release) vì
    // `OrphanReservationReconciler` chỉ xử session TERMINAL. Sự cố 2026-08-15.
    //
    // ⚠ Mốc đo là `practice_answers.created_at`, KHÔNG phải `last_scoring_published_at`: mốc sau bị
    // chính vòng đẩy-lại dời về `now` mỗi lần, lấy nó làm trần thì trần KHÔNG BAO GIỜ tới (bài học
    // `StuckScreeningRepublisher`/C14). 0 = tắt trần (giữ hành vi đẩy-lại vô hạn cũ).
    //
    // Vì sao 60 chứ không phải 30: `StuckAnswerRepublisher.ScoringLostThreshold` là 15', nên 60'
    // chừa ~3 lượt đẩy lại (15/30/45) trước khi bó tay, còn 30' chỉ chừa ĐÚNG MỘT. Đây là ngân sách
    // sống sót qua sự cố broker: trần quá ngắn thì một lần broker chết 45 phút sẽ biến thành hàng
    // loạt buổi bị chốt sổ với 0 attempt (tiền hoàn lại, nhưng bài làm thì không chấm nữa).
    public int GiveUpAfterMinutes { get; set; } = 60;
}
