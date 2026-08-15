namespace Isas.InterviewService.DTOs;

// Phỏng vấn THÍCH ỨNG — hợp đồng giữa InterviewService và AIService /decide-next (typed HttpClient).

// 1 lượt Q&A trước đó (stateless — Interview gửi kèm lịch sử; AIService không giữ state, GEN-4).
public record DecideTurnDto(string Question, string? Answer, string Kind);

// Tiêu chí năng lực để AI NEO câu hỏi thích ứng về cùng rubric (giữ công bằng chấm/ranking B2B).
public record DecideCriterionDto(string Name, string? Description);

// Evidence-driven adaptive interview: trạng thái do InterviewService sở hữu và snapshot sang AIService.
public record CriterionEvidenceStateDto(string CriterionId, string Name, string State,
    IReadOnlyList<string> EvidenceFound, IReadOnlyList<string> MissingEvidence, int DeepCount);

/// <summary>
/// INT-17b — toàn bộ đầu vào của 1 lượt <c>/decide-next</c>, gói thành record thay vì rải tham số.
///
/// VÌ SAO GÓI: chữ ký cũ đã 9 tham số + <c>ct</c>. Moq <c>Setup</c>/<c>Verify</c> nằm trong expression
/// tree, mà expression tree KHÔNG được phép bỏ optional argument (CS0854) — nên mỗi lần thêm tham số
/// là vỡ toàn bộ mock dù tham số mới có giá trị mặc định. Gói lại thì mọi khối <c>It.IsAny&lt;&gt;</c>
/// ×10 rút còn một, và lần mở rộng sau không đụng test nào.
/// </summary>
public record AdaptiveDecisionRequest(
    string AudioObjectKey,
    string JobCategory,
    string CurrentQuestion,
    IReadOnlyList<DecideTurnDto> History,
    int AskedCount,
    int FollowUpCount,
    int MaxQuestions,
    int MaxFollowUps,
    IReadOnlyList<DecideCriterionDto> Criteria,
    // INT-17b — ngữ cảnh CHUỖI: câu gốc của chuỗi đang đào sâu (mỏ neo chủ đề), đang ở tầng mấy và
    // trần tầng là bao nhiêu. Cho AI biết "đào sâu ĐÚNG chủ đề này, còn N lượt".
    string? RootQuestion = null,
    int CurrentDepth = 0,
    int MaxDepth = 0,
    // Tên các câu gốc KHÁC của buổi (không kèm transcript) — để AI không hỏi trùng chủ đề đã có sẵn.
    IReadOnlyList<string>? OtherTopics = null,
    string Language = "vi",
    string Seniority = "Junior",
    IReadOnlyList<CriterionEvidenceStateDto>? CurrentEvidenceState = null);

// Kết quả decide-next: action + câu hỏi kế (null nếu end) + transcript đã transcribe đồng bộ.
public record DecideNextResult(
    string Action,          // follow_up | clarify | new_question | end
    string? NextQuestion,   // null ⇔ end
    string? Transcript,     // transcript AIService trả về (single-source; đẩy vào ScoringJob)
    string? Reason,
    // F11 — chỉ số cách nói đo trong CÙNG lượt transcribe đó. Đây là lần đo DUY NHẤT của câu trả
    // lời ở đường thích ứng (worker sau đó bỏ Whisper) → không lấy ở đây là mất luôn.
    // Optional (default null) để call site/test cũ dựng 4 tham số vẫn compile.
    DeliveryMetricsDto? DeliveryMetrics = null,
    // Engine đã chép ra `Transcript` ngay trên. Đi CẶP với nó: đường thích ứng là lần chép DUY NHẤT
    // (worker sau đó bỏ Whisper) nên không lấy con dấu ở đây là mất vĩnh viễn lai lịch của đúng bản
    // chép đã dùng để chấm. AIService rơi từ engine từ xa về Whisper cục bộ khi mạng hỏng ⇒ giá trị
    // này thay đổi giữa các câu trong cùng một buổi.
    // 🔴 Khoá dây phía AIService: `transcriptEngine` (camelCase) — xem AnswerScoreCallbackRequest.
    string? TranscriptEngine = null,
    string? TargetCriterionId = null,
    IReadOnlyList<string>? EvidenceFound = null,
    IReadOnlyList<string>? MissingEvidence = null,
    string? NewEvidenceState = null,
    // AIService từ chối bản chép: "no_speech" (VAD không thấy vùng tiếng nói nào — KHÔNG gọi nhà
    // cung cấp, KHÔNG có transcript) hoặc "junk_transcript" (cả hai engine đều ra chuỗi rác máy sinh).
    // null = bản chép dùng được (đường thường).
    // 🔴 Khoá dây phía AIService: `rejectReason` (camelCase) — đổi tên KHÔNG ném lỗi, chỉ làm .NET
    //    bind hụt rồi im lặng chấm lại sự im lặng, đúng lớp bug `focusCriteria`/`metricsVersion`.
    string? RejectReason = null);
