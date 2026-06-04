namespace Isas.InterviewService.Services;

public class StubQuestionGenerator : IQuestionGenerator
{
    public Task<IReadOnlyList<string>> GenerateAsync(
        string jobCategory, string? cvText, string? jdText, CancellationToken ct = default)
    {
        IReadOnlyList<string> questions = jobCategory.ToUpperInvariant() switch
        {
            "BA" => new[]
            {
                "Giải thích sự khác biệt giữa functional và non-functional requirements.",
                "Bạn xử lý conflicting requirements giữa các stakeholder thế nào?",
                "Mô tả một use case bạn từng viết và cách bạn xác định actors.",
                "Khi nào nên dùng user story thay vì use case?",
                "Làm sao bạn validate rằng requirements đã đầy đủ?"
            },
            "BE" => new[]
            {
                "Giải thích sự khác biệt giữa SQL và NoSQL, khi nào dùng cái nào.",
                "Bạn thiết kế REST API để xử lý tác vụ chạy lâu (long-running) thế nào?",
                "Database indexing hoạt động ra sao và đánh đổi là gì?",
                "Mô tả cách bạn xử lý concurrency trong một service.",
                "Khi nào nên dùng message queue thay vì gọi đồng bộ?"
            },
            "FE" => new[]
            {
                "Giải thích virtual DOM và vì sao nó cải thiện hiệu năng.",
                "Bạn quản lý state trong một ứng dụng React lớn thế nào?",
                "Sự khác biệt giữa controlled và uncontrolled component?",
                "Mô tả cách bạn tối ưu thời gian tải trang.",
                "Bạn đảm bảo accessibility (a11y) ra sao?"
            },
            _ => new[]
            {
                "Giới thiệu về bản thân và kinh nghiệm của bạn.",
                "Điểm mạnh lớn nhất của bạn là gì?",
                "Mô tả một thử thách kỹ thuật bạn từng vượt qua."
            }
        };

        return Task.FromResult(questions);

    }
}