using Isas.CampaignService.Models;

namespace Isas.CampaignService.Services
{
    /// <summary>
    /// <c>campaigns.max_candidates</c> = trần RIÊNG của chiến dịch. MỘT nguồn duy nhất cho CẢ HAI
    /// đường ghi — <c>POST /campaign</c> (qua <c>ValidateEntitledSelection</c>) và
    /// <c>PUT /campaign/{id}</c> (qua <c>ValidateEntitledMutation</c>) — vì hai bản luật rời nhau
    /// chính là thứ đã để lọt số ≤ 0: nhánh create kiểm <c>is &gt; 0 &amp;&amp; &gt; cap</c>, nhánh
    /// update kiểm <c>HasValue &amp;&amp; &gt; cap</c>, và một số ÂM không lớn hơn cap nào nên đi
    /// qua cả hai.
    ///
    /// <para><b>Vì sao phải chặn cận DƯỚI, không chỉ cận trên.</b> Trần này được đọc ở
    /// <c>EnsureCandidateCapacityAsync</c> dưới dạng
    /// <c>effectiveCap = Math.Min(campaign.MaxCandidates, entitlement.MaxCandidatesCap)</c> rồi so
    /// <c>currentCount + batchCount &gt; effectiveCap</c>. Với <c>maxCandidates = -5</c> thì
    /// <c>effectiveCap = -5</c> ⇒ ngay lời mời ĐẦU TIÊN đã là <c>0 + 1 &gt; -5</c> ⇒ ném. Với
    /// <c>0</c> cũng vậy. Chiến dịch tạo ra <b>không bao giờ mời được ai</b>, mà thông báo lúc đó
    /// (<i>"Vượt giới hạn lời mời hiệu lực (-5)…"</i>) KHÔNG chỉ ra nguyên nhân là con số HR đã nhập
    /// lúc tạo, nên HR sẽ đi tìm ở chỗ khác. Chặn tại đầu vào để lỗi nổ đúng nơi sinh ra nó.</para>
    ///
    /// <para><b><c>null</c> giữ nguyên nghĩa cũ</b> = "không đặt trần RIÊNG cho chiến dịch"; chiến
    /// dịch vẫn chịu trần <c>MaxCandidatesCap</c> của gói. Đó là lý do thông báo lỗi mời HR để TRỐNG
    /// thay vì gợi ý <c>0</c> — <c>0</c> không phải cách diễn đạt "không giới hạn", nó là một trần
    /// bằng không.</para>
    ///
    /// <para>Ném <see cref="System.ArgumentException"/> ⇒ controller map <b>400</b> (create:
    /// <c>CampaignController</c> POST · update: PUT). Kiểm cận dưới chạy TRƯỚC cận trên: nói với HR
    /// rằng <c>-5</c> "vượt trần 25" là vô nghĩa, cái sai thật là số âm.</para>
    /// </summary>
    internal static class MaxCandidatesRule
    {
        /// <summary>Trần riêng nhỏ nhất có nghĩa: 1 ứng viên. Nhỏ hơn = không ai vào được.</summary>
        public const int MinCandidates = 1;

        public static void Validate(int? maxCandidates, CampaignEntitlement entitlement)
        {
            if (maxCandidates is not int value) return;   // null = không đặt trần riêng (vẫn chịu trần gói)

            if (value < MinCandidates)
                throw new ArgumentException(
                    $"maxCandidates phải >= {MinCandidates} (hiện: {value}). "
                    + "Để TRỐNG nếu không muốn giới hạn riêng cho chiến dịch.");

            // Cận trên: `MaxCandidatesCap` luôn >= 1 (EntitlementClient loại snapshot có cap < 1;
            // fallback Starter = 25, Legacy = int.MaxValue) ⇒ sau khi cận dưới đã chặn, ở đây value >= 1.
            if (value > entitlement.MaxCandidatesCap)
                throw new ArgumentException(
                    $"maxCandidates vượt trần {entitlement.MaxCandidatesCap} của gói {entitlement.TierCode}.");
        }
    }
}
