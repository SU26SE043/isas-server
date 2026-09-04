using Isas.CampaignService.Models;

namespace Isas.CampaignService.Services
{
    /// <summary>
    /// Dựng bộ tiêu chí chấm từ <c>campaign_criteria</c> — NGUỒN DUY NHẤT cho cả hai đường:
    /// <list type="number">
    /// <item><b>Chấm thật</b>: <see cref="CampaignSessionClient"/> gửi sang Interview lúc tạo buổi thi.</item>
    /// <item><b>Chấm thử</b>: <c>POST /campaign/{id}/rubric-preview</c> gửi sang AIService.</item>
    /// </list>
    ///
    /// <para><b>Vì sao phải là MỘT hàm.</b> Cả tính năng chấm thử đứng trên một lời hứa: thứ HR kiểm
    /// chứng chính là thứ ứng viên bị chấm. Hai đường dựng payload riêng sẽ trôi xa nhau theo thời gian
    /// — thêm field ở đường này, đổi thứ tự ở đường kia — và KHÔNG CÓ TRIỆU CHỨNG NÀO: cả hai vẫn trả
    /// điểm, chỉ là điểm của hai thước đo khác nhau. Có test khoá hai đường ra JSON bằng nhau.</para>
    /// </summary>
    public static class ScoringCriteriaBuilder
    {
        public static List<SessionCriterionInput> Build(IEnumerable<CampaignCriterion> criteria)
            => criteria
                // Thứ tự PHẢI tất định: nó đi vào prompt chấm, và cùng một rubric xếp khác thứ tự là
                // hai prompt khác nhau (⇒ vân tay khác, ⇒ "khác thước đo" giả). OrderNo là khoá chính
                // (UNIQUE campaign_id, order_no); Name là chốt chặn cho bộ đang dựng dở trong bộ nhớ.
                .OrderBy(c => c.OrderNo)
                .ThenBy(c => c.Name, StringComparer.Ordinal)
                .Select(c => new SessionCriterionInput(c.Name, c.Description, c.Weight, c.MaxScore)
                {
                    // RNK1 · HĐ-5 — khoá ỔN ĐỊNH để snapshot chấm khớp về campaign_criteria (điểm sàn
                    // read-time). Interview ghi vào rubric_criteria.source_criterion_id.
                    CriterionId = c.Id,
                    Levels = (c.Levels ?? new List<CampaignCriterionLevel>())
                        .OrderBy(l => l.Score)
                        .Select(l => new SessionCriterionLevelInput(l.Score, l.Descriptor))
                        .ToList()
                })
                .ToList();
    }
}
