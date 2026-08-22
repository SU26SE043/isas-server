using System.Linq.Expressions;
using Isas.InterviewService.Entities;
using Isas.InterviewService.Enums;

namespace Isas.InterviewService.Services;

/// <summary>
/// MỘT nguồn sự thật cho "buổi luyện nào đủ điều kiện làm nguồn baseline roadmap" — buổi B2C
/// (không phải campaign) ĐÃ được chấm. Dùng bởi CẢ HAI:
/// <list type="bullet">
/// <item><see cref="RoadmapService.CreateAsync"/> — chọn buổi cụ thể theo id (nguồn thật, ném
/// 404 batch nếu id nào không thoả).</item>
/// <item><see cref="PracticeService.GetHistoryAsync"/> — picker của wizard tạo roadmap, gọi với
/// <c>status=Scored&amp;excludeCampaign=true</c>.</item>
/// </list>
///
/// <para>Lệch một vế giữa hai nơi là picker cho chọn buổi mà <c>CreateAsync</c> sẽ từ chối bằng
/// 404 batch không nói id nào sai — người dùng chọn đúng thứ UI cho phép rồi vẫn bị lỗi không
/// giải thích được. Vế "không phải campaign" (<see cref="NotCampaign"/>) là MỘT expression object
/// dùng lại y nguyên ở cả hai nơi; vế trạng thái (<see cref="RequiredStatus"/>) là một hằng số
/// duy nhất — <c>GetHistoryAsync</c> nhận <c>status</c> tuỳ ý (không chỉ Scored) nên không thể ép
/// dùng chung MỘT expression cho cả điều kiện trạng thái, nhưng giá trị "Scored đúng nghĩa
/// roadmap cần" chỉ được định nghĩa MỘT LẦN ở đây — đổi giá trị này đổi luôn cả hai nơi.</para>
/// </summary>
public static class RoadmapSessionEligibility
{
    // Trạng thái BẮT BUỘC để một buổi được dùng làm nguồn roadmap (BC-3: chỉ buổi đã chấm).
    public const SessionStatus RequiredStatus = SessionStatus.Scored;

    // Không phải buổi B2B (campaign) — dùng lại NGUYÊN VĂN ở cả CreateAsync lẫn GetHistoryAsync
    // (?excludeCampaign=true), nên EF dịch ra đúng một dạng SQL ở cả hai đường.
    public static Expression<Func<PracticeSession, bool>> NotCampaign { get; } =
        s => s.CampaignId == null;

    // Hợp cả hai vế — CreateAsync dùng trực tiếp cho .Where(...) chọn buổi theo id.
    public static Expression<Func<PracticeSession, bool>> Predicate { get; } =
        s => s.CampaignId == null && s.Status == RequiredStatus;
}
