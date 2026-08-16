namespace Isas.Shared.Rubric;

/// <summary>
/// Ảnh chụp một tiêu chí chấm ở dạng KHÔNG phụ thuộc EF — dùng làm ngôn ngữ chung giữa
/// CampaignService (<c>campaign_criteria</c>) và InterviewService (<c>rubric_criteria</c>).
///
/// <para>Cố ý KHÔNG khai interface để hai entity EF cùng implement: làm vậy thì thứ tự property
/// (quyết định thứ tự khoá JSON, tức quyết định vân tay) nằm rải ở hai file thuộc hai service và
/// deploy lệch nhịp là hai bên băm ra hai vân tay khác nhau cho cùng một bộ thước đo. Record ở
/// đây là NƠI DUY NHẤT định nghĩa thứ tự đó; caller map vào, không mang kiểu riêng của mình sang.</para>
/// </summary>
public sealed record RubricCriterionSnapshot(
    int OrderNo,
    string Name,
    string? Description,
    decimal Weight,
    int MaxScore,
    IReadOnlyList<RubricLevelSnapshot> Levels);

/// <summary>Một mốc điểm: <paramref name="Score"/> và mô tả "thế nào là được ngần này điểm".</summary>
public sealed record RubricLevelSnapshot(int Score, string Descriptor);
