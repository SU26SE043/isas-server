namespace Isas.CampaignService.Services
{
    /// <summary>
    /// E5 / SCP1 — ngưỡng đạt (<c>pass_score_pct</c>) là % điểm tổng ⇒ phải ∈ [0, 100] khi có giá
    /// trị (<c>null</c> = HR quyết tay). MỘT nguồn duy nhất cho MỌI đường ghi ngưỡng:
    /// <c>campaigns.pass_score_pct</c> (CampaignService POST/PUT — E5) và
    /// <c>scoring_policies.pass_score_pct</c> (ScoringPolicyService tạo + xem trước — SCP1/B9).
    ///
    /// <para>CHECK ở tầng DB (<c>ck_campaigns_pass_score_pct_range</c>,
    /// <c>ck_scoring_policies_pass_score_pct</c>) là LƯỚI CUỐI, không phải chỗ báo lỗi: để nó nổ thì
    /// thành <see cref="Microsoft.EntityFrameworkCore.DbUpdateException"/> → 500 kèm stack trace
    /// (prod chạy Development). Guard này chặn TRƯỚC khi chạm DB, ném
    /// <see cref="System.ArgumentException"/> ⇒ controller map 400.</para>
    ///
    /// <para>⚠ Đừng đoán nguyên nhân từ triệu chứng: bắt <c>DbUpdateException</c> rồi đổi thành 400
    /// là sai — cùng exception đó còn đến từ đụng UNIQUE và FK.</para>
    /// </summary>
    internal static class PassScorePctRule
    {
        public static void Validate(int? pct)
        {
            if (pct is int p && (p < 0 || p > 100))
                throw new ArgumentException($"pass_score_pct phải trong khoảng [0, 100] (hiện: {p}).");
        }
    }
}
