using Isas.InterviewService.DTOs;

namespace Isas.InterviewService.Services.Interfaces;

public interface IAiServiceQuestionGenerator
{
    Task<List<GeneratedQuestion>> GenerateQuestionsAsync(
        string jobCategory, string? cvText, string? jdText, CancellationToken ct = default);

    // Overload ĐẦY ĐỦ: focusCriteria (BC14 — bám tiêu chí trọng tâm của roadmap lesson) + count
    // (F2b — số câu ứng viên chọn; null = để AIService dùng mặc định của nó).
    //
    // ⚠ Giữ overload 4 tham số ở trên NGUYÊN chữ ký có chủ ý: nó là đường đi của luồng thường và có
    // ~18 điểm mock trong test. Chèn thêm tham số vào giữa sẽ làm mọi lời gọi positional đó vỡ biên
    // dịch, đổi lấy đúng một tham số mà luồng thường không dùng tới.
    Task<List<GeneratedQuestion>> GenerateQuestionsAsync(
        string jobCategory, string? cvText, string? jdText,
        IReadOnlyList<string>? focusCriteria, int? count, CancellationToken ct = default);

    // RAG grounding — overload GROUNDED: truyền `grounding[]` (chunk truy hồi) + đọc `citations` per-câu
    // (Contract 2). Trả CẢ câu hỏi lẫn citation để PracticeService lưu/hiển thị. grounding rỗng → AIService
    // sinh ungrounded, citations rỗng. Tách overload riêng (không đổi 2 overload trên) — chỉ PracticeService
    // gọi khi Grounding:Enabled.
    Task<GeneratedQuestionsResult> GenerateQuestionsAsync(
        string jobCategory, string? cvText, string? jdText,
        IReadOnlyList<string>? focusCriteria, int? count,
        IReadOnlyList<GroundingChunk>? grounding, CancellationToken ct = default);

    Task<GeneratedQuestionsResult> GenerateQuestionsAsync(
        string jobCategory, string? cvText, string? jdText,
        IReadOnlyList<string>? focusCriteria, int? count,
        IReadOnlyList<GroundingChunk>? grounding, string language, CancellationToken ct = default);
}

// Nhét cái này vào chung file DTOs của ông
public class GeneratedQuestion
{
    public string Content { get; set; } = string.Empty;
}

// RAG grounding — kết quả sinh câu hỏi GROUNDED: danh sách câu (như cũ) + citation per-câu (questionIndex
// → citedChunkIds). Citations rỗng khi không truyền grounding / AI không cite.
public record GeneratedQuestionsResult(
    List<GeneratedQuestion> Questions,
    IReadOnlyList<QuestionCitationDto> Citations);

public record QuestionCitationDto(int QuestionIndex, IReadOnlyList<string> CitedChunkIds);
