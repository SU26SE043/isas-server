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

    // ── TU1 — BÙ CÂU GỐC khi chuỗi hết sớm mà ngân sách buổi vẫn còn ────────────────────────────
    //
    // VẤN ĐỀ ĐÃ ĐO trên production (chỉ tính buổi chạy trọn, `status='Scored'`) — buổi thích ứng
    // giao ÍT CÂU HƠN số ứng viên đã chọn và đã trả credit:
    //
    //   | chọn (max_questions) | max_deep_per_question | số buổi | câu thực tế TB | ít nhất | thiếu |
    //   |---------------------:|----------------------:|--------:|---------------:|--------:|------:|
    //   |                   20 |                     3 |       6 |            9,5 |       6 |  6/6  |
    //   |                    5 |                     3 |      17 |            4,9 |       4 |  1/17 |
    //   |                    5 |                     0 |      10 |            3,6 |       2 |  7/10 |
    //   |                    6 |                     0 |       9 |            2,9 |       1 |  9/9  |
    //   |                    4 |                     3 |       8 |            3,6 |       2 |  2/8  |
    //
    // Chọn 20 nhận về 9,5 — chưa tới một nửa. Theo F2b ứng viên trả 1 credit cho ĐÚNG số câu họ chọn,
    // nên đây là giao thiếu thứ đã bán. Nguyên nhân: `AnswerService.TryRunAdaptiveAsync` đóng chuỗi khi
    // chạm trần độ sâu hoặc khi AI trả `end`; hết câu gốc chưa trả lời ⇒ buổi đóng, dù ngân sách còn.
    //
    // MẶC ĐỊNH BẬT — cố ý khác tiền lệ Grounding/Tiering/CvScreening (mặc định TẮT). Những cái đó là
    // tính năng MỚI bật thăm dò; đây là bản vá KHÔI PHỤC thứ ứng viên đã trả tiền. Để mặc định tắt thì
    // đúng cái lỗi vừa đo được vẫn chạy trong mọi buổi production cho tới khi có người nhớ bật, mà lỗi
    // này KHÔNG có triệu chứng nào ngoài con số trong bảng trên. Cùng lập luận đã dùng cho
    // `Scoring:UseSampleAnswer`. Vẫn để cờ vì nó thêm một lời gọi AI mỗi lần chuỗi kết thúc: điểm số
    // không đổi nghĩa, nhưng chất lượng câu bù thì phụ thuộc model ⇒ tắt được ngay, không cần deploy.
    public bool TopUpRootQuestions { get; set; } = true;

    // Trần SỐ LẦN bù mỗi buổi (0 = không trần riêng, chỉ còn `MaxQuestions`).
    //
    // Vì sao cần thêm trần này khi `MaxQuestions` đã là trần cứng: câu bù là câu GỐC, nên nó tự mọc
    // chuỗi đào sâu của chính nó (tối đa `MaxDeepPerQuestion` tầng). 5 lần bù × (1 + 3) = 20 khe —
    // đúng bằng trần `max_questions` cao nhất mà DB cho phép (CHECK 0..20), tức đủ để lấp cả khoảng
    // hụt tệ nhất trong bảng trên (20 chọn → 9,5 giao). Trên con số đó thì mỗi lần bù thêm chỉ còn là
    // một lượt gọi `/generate-questions` nữa cho một AI đang trả câu vô dụng.
    public int MaxTopUpsPerSession { get; set; } = 5;
}
