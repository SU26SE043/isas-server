using PaymentService.Models;

namespace Isas.PaymentService.DTOs;

/// <summary>
/// Bảng giá PUBLIC — chiếu gói ra cho người mua, khác hẳn <see cref="PlanResponse"/> (admin).
///
/// Vì sao phải có DTO riêng thay vì tái dùng <c>PlanResponse</c>:
///   • <c>IsActive</c>/<c>EntitlementsVersion</c> là chuyện nội bộ catalog — người mua không quyết định gì
///     dựa vào chúng, mà lộ ra thì FE dễ render nhầm thành "trạng thái gói của tôi".
///   • Người mua cần thứ <c>PlanResponse</c> KHÔNG có: <b>giá + packageId</b>. Gói (<c>plans</c>) không
///     phải thứ mua được — thứ mua được là <c>product_packages</c> có <c>plan_id</c> trỏ tới nó. Thiếu
///     packageId thì FE phải tự join <c>/package</c> ở client và tự đoán pack nào thuộc gói nào.
/// </summary>
/// <param name="Packages">SKU mua được của gói này (rỗng = gói miễn phí / chưa mở bán).</param>
public sealed record PublicPlanResponse(
    Guid Id,
    PlanAudience Audience,
    string Code,
    string Name,
    int Rank,
    InterviewFunding InterviewFunding,
    int? MonthlyQuota,
    bool AdaptiveEnabled,
    int? AdaptiveMaxQuestions,
    int? AdaptiveMaxFollowups,
    bool GroundingEnabled,
    int SelfConsistencyN,
    bool CvAnalysisIncluded,
    bool RepoAnalysisIncluded,
    bool RoadmapEnabled,
    int? MaxQuestionsCap,
    int? MaxActiveCampaigns,
    int? MaxCandidatesCap,
    bool PostpaidEligible,
    int? SeatCount,
    IReadOnlyList<PlanPackageOption> Packages)
{
    public static PublicPlanResponse From(Plan plan, IEnumerable<ProductPackage> packages) => new(
        plan.Id, plan.Audience, plan.Code, plan.Name, plan.Rank, plan.InterviewFunding, plan.MonthlyQuota,
        plan.AdaptiveEnabled, plan.AdaptiveMaxQuestions, plan.AdaptiveMaxFollowups, plan.GroundingEnabled,
        plan.SelfConsistencyN, plan.CvAnalysisIncluded, plan.RepoAnalysisIncluded, plan.RoadmapEnabled,
        plan.MaxQuestionsCap, plan.MaxActiveCampaigns, plan.MaxCandidatesCap, plan.PostpaidEligible,
        plan.SeatCount,
        packages.Select(PlanPackageOption.From).ToList());
}

/// <param name="PackageId">Truyền thẳng vào <c>POST /payment/order</c> để mua gói này.</param>
public sealed record PlanPackageOption(Guid PackageId, string Name, long PriceVnd, int? DurationDays)
{
    public static PlanPackageOption From(ProductPackage p) => new(p.Id, p.Name, p.PriceVnd, p.DurationDays);
}

/// <summary>
/// Gói ĐANG DÙNG của chính người gọi + hạn mức còn lại.
///
/// Nguồn = <see cref="Services.EntitlementResolver"/>, tức <b>đúng cái mà <c>ReserveAsync</c> dùng để gác
/// cửa vào bài</b>. Cố ý không tự truy vấn <c>subscriptions</c> ở đây: hai đường đọc khác nhau thì con số
/// FE hiện ("còn 17 lượt") sẽ có ngày lệch với con số hệ thống thật sự cưỡng chế, và lệch kiểu đó không có
/// triệu chứng nào ngoài việc người dùng bị từ chối trong lúc màn hình bảo còn hạn mức.
/// </summary>
/// <param name="TieringEnabled">
/// Cờ <c>Tiering:Enabled</c>. Tắt = quyền lợi của gói CHƯA có hiệu lực lúc chạy (Interview bỏ qua
/// entitlement, xem ghi chú S11) ⇒ FE phải tắt nút Mua, không thì bán thứ chưa dùng được.
/// </param>
/// <param name="QuotaRemaining">Chỉ có nghĩa với gói Metered; <c>null</c> = gói không đếm lượt theo tháng.</param>
public sealed record MyPlanResponse(
    PlanAudience Audience,
    string TierCode,
    string TierName,
    int TierRank,
    InterviewFunding InterviewFunding,
    bool IsPaid,
    DateTime? ExpiresAt,
    int? MonthlyQuota,
    int? QuotaUsed,
    int? QuotaReserved,
    int? QuotaRemaining,
    DateTime? PeriodStart,
    bool TieringEnabled);
