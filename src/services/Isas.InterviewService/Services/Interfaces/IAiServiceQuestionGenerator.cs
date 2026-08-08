using Isas.InterviewService.DTOs;

namespace Isas.InterviewService.Services.Interfaces;

public interface IAiServiceQuestionGenerator
{
    // ── SEN1 — `seniority` trên MỌI overload, không chỉ overload giàu nhất ──────────────────────
    //
    // `PracticeService` chọn overload theo 4 nhánh (labeled / grounded / focus-hoặc-count / plain),
    // và nhánh nào cũng có người dùng thật:
    //   • labeled  → rubric seed B2C (có tiêu chí `WhenTargeted`);
    //   • các nhánh còn lại → rubric campaign B2B **và** rubric riêng BC16 — cả hai đều nhận DEFAULT
    //     `ScoringScope='Always'` nên `targetable` RỖNG (rules.md §INT-18, mục SC2).
    // Đặt `seniority` riêng ở overload giàu nhất là bỏ rơi trọn dòng B2B + mọi ứng viên dùng rubric
    // riêng: họ chọn *Senior* mà bộ câu gốc vẫn ra y hệt *Fresher*, không lỗi, không log. Đó đúng
    // là kiểu hỏng SEN1 sinh ra để diệt, chỉ đổi chỗ chứ không mất đi.
    //
    // Vị trí: NGAY TRƯỚC `ct` (CA1068 buộc CancellationToken đứng cuối) — cùng quy ước với
    // `CampaignSessionClient.CreateOrGetSessionAsync` khi PR160 wire seniority sang Interview.
    // Mặc định `"Junior"` = mặc định của cột `practice_sessions.seniority` ở DB, nên caller chưa
    // wire vẫn gửi đúng thứ DB của chính họ đang ghi.
    Task<List<GeneratedQuestion>> GenerateQuestionsAsync(
        string jobCategory, string? cvText, string? jdText,
        string seniority = "Junior", CancellationToken ct = default);

    // Overload ĐẦY ĐỦ: focusCriteria (BC14 — bám tiêu chí trọng tâm của roadmap lesson) + count
    // (F2b — số câu ứng viên chọn; null = để AIService dùng mặc định của nó).
    Task<List<GeneratedQuestion>> GenerateQuestionsAsync(
        string jobCategory, string? cvText, string? jdText,
        IReadOnlyList<string>? focusCriteria, int? count,
        string seniority = "Junior", CancellationToken ct = default);

    // RAG grounding — overload GROUNDED: truyền `grounding[]` (chunk truy hồi) + đọc `citations` per-câu
    // (Contract 2). Trả CẢ câu hỏi lẫn citation để PracticeService lưu/hiển thị. grounding rỗng → AIService
    // sinh ungrounded, citations rỗng. Tách overload riêng (không đổi 2 overload trên) — chỉ PracticeService
    // gọi khi Grounding:Enabled.
    //
    // ⚠ Overload này CỐ Ý giữ nguyên chữ ký (KHÔNG có `seniority`): thêm `string seniority` vào đây
    // sẽ đụng độ với `string language` của overload ngay dưới — một lời gọi 7 tham số kết thúc bằng
    // chuỗi khớp được CẢ HAI (seniority-với-ct-mặc-định vs language-với-seniority-và-ct-mặc-định)
    // ⇒ CS0121 ambiguous. Đường grounded của `PracticeService` vì thế gọi thẳng overload dưới với
    // `session.Language` (giá trị ở nhánh đó vốn LÀ "vi", nên hành vi không đổi).
    Task<GeneratedQuestionsResult> GenerateQuestionsAsync(
        string jobCategory, string? cvText, string? jdText,
        IReadOnlyList<string>? focusCriteria, int? count,
        IReadOnlyList<GroundingChunk>? grounding, CancellationToken ct = default);

    Task<GeneratedQuestionsResult> GenerateQuestionsAsync(
        string jobCategory, string? cvText, string? jdText,
        IReadOnlyList<string>? focusCriteria, int? count,
        IReadOnlyList<GroundingChunk>? grounding, string language,
        string seniority = "Junior", CancellationToken ct = default);

    // Overload GIÀU NHẤT: thêm `criteria` = danh sách tiêu chí NỘI DUNG của rubric buổi này, để
    // AIService gắn nhãn "câu hỏi i nhắm tiêu chí nào" (`targetCriteria`, mảng SONG SONG theo index
    // với `questions`). Nhãn đó quyết định phạm vi chấm về sau (ScoringScopeFilter).
    //
    // ⚠ Tách overload riêng thay vì thêm tham số vào 4 overload trên: PracticeService chỉ gọi
    // overload này khi THẬT SỰ có tiêu chí nội dung để gắn nhãn — rubric không có tiêu chí nào
    // `WhenTargeted` (B2B, rubric riêng BC16) thì không có gì để gửi, nên giữ nguyên đường gọi cũ
    // vừa đúng hợp đồng ("criteria vắng ⇒ không gắn nhãn") vừa không đụng vào hành vi đã có.
    Task<GeneratedQuestionsResult> GenerateQuestionsAsync(
        string jobCategory, string? cvText, string? jdText,
        IReadOnlyList<string>? focusCriteria, int? count,
        IReadOnlyList<GroundingChunk>? grounding, string language,
        IReadOnlyList<QuestionTargetCriterionDto>? criteria,
        string seniority = "Junior", CancellationToken ct = default);
}

/// <summary>
/// Tiêu chí NỘI DUNG cấp cho AIService để gắn nhãn câu hỏi.
///
/// 🔒 KHOÁ DÂY: JSON là <c>{ "criterionId": "&lt;guid&gt;", "name": "..." }</c> (camelCase). Payload
/// gửi đi dựng bằng anonymous object viết thẳng tên trường — <c>JsonContent.Create</c> KHÔNG áp
/// naming policy nào, nên record này serialize ra PascalCase nếu đưa thẳng vào payload. Lệch tên
/// giữa .NET và Python KHÔNG ném lỗi ở đâu cả, chỉ im lặng bỏ trường (repo đã dính 3 lần:
/// <c>focusCriteria</c> bị pydantic nuốt · <c>adaptiveMaxQuestions</c> vs <c>maxQuestions</c> ·
/// <c>metricsVersion</c> rụng ở schema response).
/// </summary>
public record QuestionTargetCriterionDto(Guid CriterionId, string Name);

// Nhét cái này vào chung file DTOs của ông
public class GeneratedQuestion
{
    public string Content { get; set; } = string.Empty;

    // Tiêu chí NỘI DUNG câu hỏi này nhắm tới (AIService trả qua `targetCriteria`, mảng song song
    // theo index với `questions`). Gắn LÊN CHÍNH câu hỏi thay vì trả thành danh sách riêng như
    // `Citations`: nhãn đi kèm câu là quan hệ 1-1, tách ra là mở đường cho lệch index.
    //
    // null = AIService không gắn nhãn (image cũ / criteria không gửi / fail-open) ⇒ chấm đủ rubric.
    public IReadOnlyList<Guid>? TargetCriterionIds { get; set; }
}

// RAG grounding — kết quả sinh câu hỏi GROUNDED: danh sách câu (như cũ) + citation per-câu (questionIndex
// → citedChunkIds). Citations rỗng khi không truyền grounding / AI không cite.
public record GeneratedQuestionsResult(
    List<GeneratedQuestion> Questions,
    IReadOnlyList<QuestionCitationDto> Citations);

public record QuestionCitationDto(int QuestionIndex, IReadOnlyList<string> CitedChunkIds);
