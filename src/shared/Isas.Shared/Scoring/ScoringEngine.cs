namespace Isas.Shared.Scoring;

/// <summary>
/// SCP1 · HĐ-3/HĐ-4 — PHIÊN BẢN của bộ đánh giá biểu thức (lexer + parser + evaluator + nghĩa biến
/// trong <see cref="ScoringContext"/>).
///
/// <para>Ghim lên từng <c>scoring_policies.engine_version</c> lúc tạo và vào VÂN TAY (HĐ-4) để so
/// trước/sau chỉ có nghĩa khi cùng phiên bản engine. Đổi ngữ nghĩa của bất kỳ toán tử/hàm/biến nào
/// (kể cả "sửa cho đúng") ⇒ <b>PHẢI</b> bump số này — nếu không, điểm của các buổi thi đã đóng đổi
/// ÂM THẦM ở lần tính lại kế tiếp mà không nhãn nào cảnh báo.</para>
/// </summary>
public static class ScoringEngine
{
    public const string Version = "1";
}
