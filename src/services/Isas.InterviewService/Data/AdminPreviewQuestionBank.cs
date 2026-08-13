using Isas.InterviewService.Enums;

namespace Isas.InterviewService.Data;

/// <summary>
/// Bộ câu hỏi mẫu dùng cho CHẤM THỬ bộ chuẩn B2C — hằng số trong code.
///
/// <para>🔴 CỐ Ý loại phương án "rút một câu từ <c>practice_questions</c> thật". Câu hỏi B2C được sinh
/// từ CV/JD của chính người dùng, nên chúng chứa tên công ty, tên dự án, đôi khi cả con số nội bộ mà
/// ứng viên viết trong CV. Hiện những câu đó cho admin là RÒ RỈ DỮ LIỆU, và nó sẽ xảy ra mà không ai
/// nhận ra vì màn hình chỉ trông như "một câu hỏi ví dụ".</para>
///
/// <para>Ba câu mỗi (nghề, ngôn ngữ), chọn theo tiêu chí phân biệt được thang điểm: đủ rộng để bài
/// yếu có chỗ nông và bài giỏi có chỗ nêu đánh đổi, nhưng không phải câu "đại luận" mà mọi thang đều
/// cho điểm cao. Admin vẫn tự gõ câu khác được.</para>
/// </summary>
public static class AdminPreviewQuestionBank
{
    private static readonly Dictionary<(JobCategory, string), string[]> Bank = new()
    {
        [(JobCategory.BA, "vi")] =
        [
            "Bạn nhận một yêu cầu mơ hồ từ khách hàng: “hệ thống cần nhanh hơn”. Bạn làm rõ nó thành yêu cầu cụ thể như thế nào?",
            "Kể một lần bạn phát hiện hai bên liên quan có mục tiêu mâu thuẫn nhau. Bạn xử lý ra sao?",
            "Làm sao bạn viết tiêu chí chấp nhận (acceptance criteria) cho một user story mà đội phát triển không hiểu nhầm?"
        ],
        [(JobCategory.BA, "en")] =
        [
            "A client says “the system needs to be faster”. How do you turn that into a concrete, testable requirement?",
            "Describe a time two stakeholders wanted conflicting outcomes. How did you handle it?",
            "How do you write acceptance criteria that the development team cannot misread?"
        ],
        [(JobCategory.BE, "vi")] =
        [
            "Một API đang chậm dần khi dữ liệu lớn lên. Bạn tìm nguyên nhân và xử lý theo thứ tự nào?",
            "Giải thích cách bạn giữ cho một thao tác ghi tiền không bị tính hai lần khi client gửi lại request.",
            "Khi nào bạn chọn thêm chỉ mục (index) và cái giá phải trả là gì?"
        ],
        [(JobCategory.BE, "en")] =
        [
            "An API gets slower as the data grows. In what order do you investigate and fix it?",
            "Explain how you keep a money-writing operation from being applied twice when a client retries.",
            "When do you add a database index, and what does it cost you?"
        ],
        [(JobCategory.FE, "vi")] =
        [
            "Trang danh sách của bạn giật khi cuộn trên máy yếu. Bạn tìm nguyên nhân và xử lý thế nào?",
            "Bạn quản lý trạng thái dùng chung giữa nhiều màn hình ra sao, và khi nào thì KHÔNG nên đưa vào store chung?",
            "Một nút bấm được người dùng dùng bàn phím và trình đọc màn hình. Bạn cần làm gì để nó dùng được?"
        ],
        [(JobCategory.FE, "en")] =
        [
            "Your list page stutters while scrolling on a low-end device. How do you find and fix the cause?",
            "How do you manage state shared across screens, and when should something NOT go into a global store?",
            "A button is used with a keyboard and a screen reader. What does it take to make it usable?"
        ]
    };

    /// <summary>Bộ câu mẫu của một (nghề, ngôn ngữ). Rỗng nếu chưa soạn cho tổ hợp đó.</summary>
    public static IReadOnlyList<string> For(JobCategory jobCategory, string language)
        => Bank.TryGetValue((jobCategory, language), out var questions) ? questions : [];
}
