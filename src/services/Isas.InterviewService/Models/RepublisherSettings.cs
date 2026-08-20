namespace Isas.InterviewService.Models;

// DB29 — cấu hình StuckAnswerRepublisher (BackgroundService quét answer kẹt để đẩy lại job chấm).
//
// ⚠ Ba mốc thời gian dưới đây TRƯỚC 2026-08-20 là hằng `static readonly` nằm trong chính
// `StuckAnswerRepublisher`. Chúng được đưa ra đây vì đo prod cho thấy chúng KHÔNG phải chi tiết nội
// bộ: chúng là thứ quyết định người dùng chờ 18 giây hay 15 phút để thấy điểm (xem `ScoringLostMinutes`),
// mà muốn sửa lại phải build lại image.
public class RepublisherSettings
{
    public const string SectionName = "Republisher";

    // Trần số answer xử lý mỗi vòng quét. Trước DB29 truy vấn KHÔNG có Take() → sự cố broker dồn
    // hàng chục nghìn answer sẽ nạp hết (kèm transcript TEXT) vào bộ nhớ trong 1 vòng: chính component
    // sinh ra để CỨU quá tải lại là thứ gục trước. Batch có trần → mỗi vòng tiêu hoá 1 phần, vòng sau
    // lấy tiếp (quét mỗi 2') → vẫn thoát hàng, không nổ.
    public int BatchSize { get; set; } = 200;

    // Chu kỳ quét. Nó CỘNG THẲNG vào độ trễ cứu hộ thật: answer kẹt phải chờ tới vòng quét KẾ TIẾP,
    // nên trễ thật nằm trong [ngưỡng, ngưỡng + 1 chu kỳ]. Chính dấu vân tay đó lộ nguyên nhân trong
    // đo prod 2026-08-20 (xem `ScoringLostMinutes`): các độ trễ đo được đều rơi đúng vào cửa sổ
    // [15', 15'+2'] — không phải phân bố của "AI chấm lâu", mà là của một cái đồng hồ.
    //
    // GIỮ 2': phần lớn độ trễ nằm ở NGƯỠNG chứ không ở chu kỳ, nên hạ chu kỳ chỉ thêm query mỗi phút
    // mà rút được vài chục giây. Hạ ngưỡng mới là chỗ ăn tiền.
    public int ScanIntervalMinutes { get; set; } = 2;

    // Uploaded mà chưa publish lần nào (LastScoringPublishedAt == null) quá ngưỡng này
    // = publish hụt lúc upload -> đẩy lại sớm. Đo theo CreatedAt để chừa cửa sổ
    // upload đang dở (request còn chạy, status chưa kịp thành Scoring).
    //
    // GIỮ 2': trong 10 buổi chậm đo được ở prod, nhánh này để lại dấu vân tay 90s (= 2' ngưỡng, bắt
    // đúng vòng quét sớm) — tức nó đã nhanh sẵn. Điểm nghẽn nằm ở `ScoringLostMinutes`, không phải ở đây.
    public int PublishFailedMinutes { get; set; } = 2;

    // Đã publish nhưng quá lâu không thấy callback = worker mất tích (crash/mất message/nack mà không
    // thử lại) -> đẩy lại. Đo theo LastScoringPublishedAt.
    //
    // ĐO PROD 2026-08-20 (77 buổi đã chấm xong; thời gian từ lúc người dùng bấm nộp
    // `practice_sessions.completed_at` tới dòng điểm cuối cùng `max(answer_scores.created_at)`):
    //   p50 = 18,6s   ·   p90 = 572,9s   ·   max = 4529s   ·   10/77 buổi (13%) vượt 120s.
    // TOÀN BỘ 10 buổi chậm đó đều có ít nhất một answer bị publish lại trễ, và độ trễ gom cụm rất
    // chặt ở 909 · 919 · 949 · 966 · 1001 · 1014 · 1025 giây = ĐÚNG ngưỡng cũ 15' + một chu kỳ quét 2'
    // (và 90 · 90 giây = `PublishFailedMinutes` + chu kỳ). Nói cách khác: người dùng KHÔNG chờ AI chấm
    // (p50 18,6s mới là tốc độ thật), họ chờ CÁI ĐỒNG HỒ NÀY.
    // Gốc rễ: worker Python nack lỗi tạm thời (bắt được `503 UNAVAILABLE` thật trong log prod) mà
    // không thử lại, nên đường phục hồi DUY NHẤT là ngưỡng này. Retry phía worker đang được vá riêng —
    // ngưỡng ở đây vẫn phải hạ, vì nó là lưới cuối cho MỌI cách mất message, không riêng 503.
    //
    // VÌ SAO 3' chứ KHÔNG phải 90s: ngưỡng phải LỚN HƠN ca chấm chậm nhất còn HỢP LỆ — chép lời ~24s
    // cộng 3 lượt gọi Gemini có retry ≈ 90s — rồi cộng biên an toàn. Đặt sát 90s là đẩy lại CHỒNG LÊN
    // worker đang chấm bình thường ⇒ chấm trùng: tốn thêm lượt Gemini mà không về sớm hơn một giây nào.
    // Còn 15' cũ thì ngược lại — an toàn tuyệt đối, đổi bằng 15 phút người dùng ngồi nhìn màn hình chờ.
    //
    // ⚠ ĐI CẶP với `Scoring:GiveUpAfterMinutes` (xem `ScoringOptions`): ngân sách bỏ cuộc đong bằng SỐ
    // LƯỢT đẩy lại = GiveUpAfterMinutes / ngưỡng này. Đổi một con số mà giữ nguyên con kia là SAI —
    // hạ ngưỡng này 5 lần mà giữ trần 60' là biến ~3 lượt thành ~20 lượt Gemini cho một answer đã chết.
    public int ScoringLostMinutes { get; set; } = 3;
}
