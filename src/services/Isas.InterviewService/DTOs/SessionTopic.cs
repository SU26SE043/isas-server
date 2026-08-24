namespace Isas.InterviewService.DTOs;

// TOP1-B5 — danh mục đề tài GẮN cho MỘT buổi cụ thể (snapshot lúc tạo session — xem
// Entities.PracticeSession.Topics). Không phải practice_topics (danh mục sống, TOP1-B1/B2).

/// <summary>
/// Giá trị hợp lệ của <see cref="SessionTopic.Source"/> — chuỗi (GEN-2), không phải C# enum: tránh
/// thêm converter enum-string riêng cho một trường jsonb lồng bên trong entity khác (mẫu
/// <c>SessionResultResponse.RubricSource</c> — cùng kiểu "tập đóng lưu string, không mint type mới").
/// </summary>
public static class TopicSource
{
    public const string Catalog = "Catalog";
    public const string CvRequirement = "CvRequirement";
}

/// <summary>
/// 1 đề tài đã gắn vào buổi — LƯU TRỮ nội bộ (jsonb <c>practice_sessions.topics</c>) VÀ payload gửi
/// AIService (chỉ <c>Label</c>/<c>CvLevel</c>/<c>CvEvidence</c> — xem <c>AiServiceQuestionGenerator</c>).
///
/// <para><b><see cref="CriterionName"/> KHÔNG BAO GIỜ lộ ra ngoài .NET</b> — không vào payload
/// AIService (CẤM tường minh của TOP1-B5), không vào response client (xem
/// <see cref="SessionTopicResponse"/>, thiếu hẳn field này). Nó chỉ phục vụ nội bộ (đối chiếu/audit
/// với rubric buổi tạo lúc chọn chủ đề).</para>
///
/// <para><b>Lưu TÊN tiêu chí, KHÔNG lưu criterionId (GUID)</b> — GUID khác nhau giữa <c>vi</c>/<c>en</c>
/// (F12) và rubric riêng của ứng viên (BC16) mint GUID mới cho cùng một tên; resolve theo TÊN lúc
/// cần, cùng lý do <c>B2CRubricSeed</c>/SC2 đã chốt cho <c>ScoringScope</c>.</para>
/// </summary>
public record SessionTopic(
    string Key,
    string Label,
    string Source,
    string? CriterionName = null,
    string? CvLevel = null,
    string? CvEvidence = null);

/// <summary>
/// Hình dạng <see cref="SessionTopic"/> gửi cho CLIENT — cố ý THIẾU <see cref="SessionTopic.CriterionName"/>.
/// Hợp đồng khoá cứng với FE: <c>{ "key", "label", "source", "cvLevel", "cvEvidence" }</c>, camelCase
/// (JsonStringEnumConverter/camelCase policy toàn cục ở Program.cs áp cho response, xem AddJsonOptions).
/// </summary>
public record SessionTopicResponse(
    string Key,
    string Label,
    string Source,
    string? CvLevel = null,
    string? CvEvidence = null);
