namespace Isas.InterviewService.Models;

// Phỏng vấn THÍCH ỨNG — cấu hình B2C (section "Adaptive"). B2B lấy toggle/trần từ campaign (cross-service).
// Enabled=false (mặc định) = feature flag TẮT → giữ nguyên luồng batch tĩnh cũ (rollout an toàn).
public class AdaptiveOptions
{
    public const string SectionName = "Adaptive";

    // Bật vòng lặp câu-kế-động cho B2C. Tắt → sinh câu hỏi batch như cũ (không đụng B2B).
    public bool Enabled { get; set; } = false;

    // INT-17b — số câu GỐC sinh sẵn lúc tạo buổi. Khi adaptive bật, đây cũng là số câu XIN AIService
    // (trước đây xin `questionCount` rồi vứt bớt = đốt token vô ích). Mỗi câu gốc sau đó được đào sâu
    // tối đa `MaxDeepPerQuestion` lần, xen kẽ ngay sau nó.
    public int SeedCount { get; set; } = 5;

    // Trần TỔNG số câu (seed + thích ứng) đóng dấu lên session lúc tạo. 0 = không trần cứng.
    // 5 gốc × (1 + 3 đào sâu) = 20 — vừa khít CHECK `max_questions BETWEEN 0 AND 20` ở DB.
    public int MaxQuestions { get; set; } = 20;

    // Trần số câu THÍCH ỨNG cho CẢ BUỔI, đóng dấu lên session lúc tạo. 0 = không trần cứng.
    // ⚠ Ở chế độ chuỗi-theo-câu, trần buổi PHẢI là 0 (trần 3 bó chặt hơn trần theo câu 5×3=15 ⇒ hội thoại
    // chết ở câu đào sâu thứ 3) — nhưng việc đó do CODE ép (`PracticeService` khi `MaxDeepPerQuestion > 0`),
    // KHÔNG phải do giá trị mặc định ở đây. Giữ 3 vì giá trị này chỉ còn hiệu lực ở chế độ frontier cũ,
    // tức đúng lúc kill-switch được bật: để 0 ở đó nghĩa là "KHÔNG trần" ⇒ tắt chế độ chuỗi lại ra một
    // hành vi thứ ba (thích ứng không giới hạn tới trần buổi) thay vì hành vi trước INT-17b.
    public int MaxFollowUps { get; set; } = 3;

    // INT-17b — trần số câu ĐÀO SÂU cho MỖI câu gốc. 0 = chế độ CŨ (frontier: chỉ sinh câu kế khi
    // MỌI câu đã trả lời, ngân sách tính theo buổi) ⇒ vừa là kill-switch vừa là bộ chọn chế độ.
    public int MaxDeepPerQuestion { get; set; } = 3;

    // INT-17b — số lần `/decide-next` lỗi trong 1 buổi trước khi thôi gọi. Chế độ chuỗi gọi AI sau gần
    // như MỌI câu trả lời; AIService hỏng mà vẫn gọi thì mỗi lượt phải chờ hết timeout ⇒ cộng hàng chục
    // phút chờ chết vào đúng một buổi thi. Chạm trần → degrade về luồng tĩnh (answer vẫn lưu bình thường).
    public int MaxFailuresPerSession { get; set; } = 3;
}
