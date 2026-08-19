using Isas.InterviewService.Enums;

namespace Isas.InterviewService.Models;

// BC9/E10 — cấu hình chấm điểm.
public class ScoringOptions
{
    public const string SectionName = "Scoring";

    // Dải mức MẶC ĐỊNH cho tiêu chí KHÔNG khai `rubric_levels` — mà trên production là TẤT CẢ
    // (`select count(*) from rubric_levels` = 0), nên cờ này chạm tới 100% lượt chấm.
    //
    // MẶC ĐỊNH `EveryInteger` = hành vi có từ E9 (liệt kê mọi số nguyên 0..maxScore, descriptor
    // "Mức i/maxScore"). `Descriptive` đổi sang ≤6 mốc trải đều kèm bậc chất lượng có nghĩa —
    // xem `ScoringCriteriaBuilder.DefaultBand` để biết ĐO ĐƯỢC GÌ và CHƯA chứng minh được gì.
    //
    // Vì sao TẮT: giả thuyết sinh ra nó ("mốc rỗng nghĩa làm chấm mất tái lập") ĐÃ ĐO VÀ SAI.
    // Chạy cùng cấu hình hai lần rồi so hai lần với nhau, 40 câu thật: dải cũ 90,7% cặp cùng điểm,
    // dải mới 92,1% — chênh 1,4 điểm phần trăm với sai số chuẩn của hiệu ≈ 2,7, tức không phân biệt
    // được với không đổi. Tốc độ, token cũng như nhau.
    //
    // Bật nó lên là mọi điểm chấm sau đó mang nghĩa khác điểm đã lưu và KHÔNG hồi tố được — đổi
    // thước đo để lấy một cải thiện không đo được là đánh đổi tồi. Giữ code vì phần "thang 30 ra 6
    // dòng prompt thay vì 31" vẫn đúng, và vì phép đo lặp lại được khi có dữ liệu thật nhiều hơn.
    // Cách đo đúng nằm ở `ScoringCriteriaBuilder.DefaultBand` — đọc đó trước khi định bật.
    public DefaultBandStyle DefaultBandStyle { get; set; } = DefaultBandStyle.EveryInteger;

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

    // Ghép ĐÁP ÁN MẪU HR soạn vào prompt chấm, cho đúng câu có nó (B2B).
    //
    // Mặc định BẬT — khác tiền lệ các cờ tính năng khác của repo (Grounding/Tiering/CvScreening đều mặc
    // định tắt). Những cái đó là tính năng mới bật thăm dò; còn đây là dữ liệu HR CHỦ ĐỘNG soạn ra với
    // mục đích duy nhất là để AI chấm theo. Nhập đáp án xong mà mặc định không dùng thì tính năng im
    // lặng vô hiệu — đúng kiểu "có tên mà không có ruột".
    //
    // Vẫn để cờ, vì đây là thay đổi THƯỚC ĐO: có đáp án mẫu nhiều khả năng làm AI chấm khắt khe hơn, mà
    // chưa ai đo được điểm sẽ lên hay xuống. Tắt là quay về đúng cách chấm trước đó, không cần deploy.
    public bool UseSampleAnswer { get; set; } = true;

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
    // thái `Scoring` ⇒ một attempt chết là buổi kẹt vĩnh viễn, cứ mỗi nhịp đẩy-lại lại đốt thêm một
    // lượt Gemini, còn credit thì treo ở `Reserved` (không consume, không release) vì
    // `OrphanReservationReconciler` chỉ xử session TERMINAL. Sự cố 2026-08-15.
    //
    // ⚠ Mốc đo là `practice_answers.created_at`, KHÔNG phải `last_scoring_published_at`: mốc sau bị
    // chính vòng đẩy-lại dời về `now` mỗi lần, lấy nó làm trần thì trần KHÔNG BAO GIỜ tới (bài học
    // `StuckScreeningRepublisher`/C14). 0 = tắt trần (giữ hành vi đẩy-lại vô hạn cũ).
    //
    // ⚠⚠ CON SỐ NÀY ĐI CẶP với `Republisher:ScoringLostMinutes` — ĐỔI MỘT MÀ KHÔNG ĐỔI CÁI KIA LÀ SAI.
    // Trần đong bằng SỐ LƯỢT đẩy lại chứ không bằng phút: số lượt ≈ GiveUpAfterMinutes / ScoringLostMinutes.
    //   • Bản cũ: 60' đi với ngưỡng 15' ⇒ ~3 lượt (15/30/45) rồi bó tay — đó là lý do 60 được chọn.
    //   • 2026-08-20 ngưỡng hạ 15' → 3' (đo prod: p90 thấy điểm = 572,9s, các buổi chậm gom cụm ở
    //     909–1025s = đúng đồng hồ 15' + chu kỳ quét 2'; người dùng chờ ĐỒNG HỒ chứ không chờ AI chấm).
    //     GIỮ NGUYÊN 60' khi đó sẽ thành ~20 lượt đẩy lại cho một answer ĐÃ CHẾT = 20 lượt Gemini đốt
    //     để đổi lấy đúng con số 0. Hạ về 20' để giữ lại ~6 lượt — vẫn DÀY GẤP ĐÔI ngân sách 3 lượt cũ.
    //
    // Đánh đổi nói thẳng: đây vẫn là ngân sách sống sót qua sự cố broker, nên broker chết LÂU HƠN 20'
    // giờ sẽ chốt sổ buổi bằng số attempt đang có (0 attempt ⇒ `SessionAbandoned` ⇒ Payment hoàn credit,
    // nhưng bài làm thì không chấm nữa) — trước đây phải quá 60' mới tới nước đó. Chấp nhận vì 6 lượt
    // trong 20' đã phủ mọi trục trặc ngắn, và đường phục hồi thật cho lỗi tạm thời là retry ở worker,
    // không phải kéo dài đồng hồ chờ của người dùng.
    public int GiveUpAfterMinutes { get; set; } = 20;
}
