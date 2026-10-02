namespace Isas.InterviewService.Enums;

/// <summary>
/// RUB1 — công thức gộp điểm tiêu chí thành điểm tổng của MỘT buổi luyện B2C (INT-10). Ghim lên
/// <c>practice_sessions.b2c_score_formula</c> lúc TẠO buổi (cùng mẫu SkipPenalty/B2CRubricVersion:
/// "dùng luật lúc bắt đầu, không phải luật đổi sau").
///
/// <para>Cột <c>null</c> = buổi tạo TRƯỚC RUB1 ⇒ tính như <see cref="Average"/> (không hồi tố điểm cũ).
/// Code hiện tại chỉ ghi <see cref="Weighted"/>; <see cref="Average"/> có trong tập để một buổi có thể
/// ghi nhận tường minh luật cũ (vd kill-switch sau này) thay vì nói dối bằng null.</para>
/// </summary>
public enum B2CScoreFormula
{
    /// <summary>Trung bình cộng % các tiêu chí CÓ điểm (equal weight) — luật B2C trước RUB1.</summary>
    Average = 0,

    /// <summary>
    /// Σ(pct × w) / Σw, chỉ trên tiêu chí CÓ điểm — tiêu chí không được hỏi rơi khỏi CẢ tử lẫn mẫu
    /// (cùng tinh thần INT-18: không tính 0 cho thứ không được hỏi).
    /// </summary>
    Weighted = 1
}
