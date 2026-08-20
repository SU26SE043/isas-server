namespace Isas.InterviewService.Enums;

/// <summary>
/// J4 — cấp độ ứng viên, CHỈ dùng để dựng khoá <see cref="Data.PromptTemplateKeys"/> (thêm giá
/// trị enum là tự có đủ khoá per-level, không phải nhớ sửa danh sách — mẫu <see cref="JobCategory"/>).
///
/// <para>⚠ Tên hằng PHẢI khớp ĐÚNG chữ hoa/thường với CHECK <c>ck_practice_sessions_seniority</c> /
/// <c>ck_campaigns_seniority</c> ở DB và với so sánh case-sensitive của
/// <c>PracticeService.ValidateSeniority</c> (<c>AllowedSeniorities</c>). KHÔNG đổi kiểu cột
/// <c>Seniority</c> trên entity (vẫn là <c>string</c>) — enum này không map DB, không migration.</para>
/// </summary>
public enum Seniority
{
    Fresher,
    Junior,
    Middle,
    Senior
}
