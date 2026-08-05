using Isas.PaymentService.DTOs;
using Isas.PaymentService.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PaymentService.Models;

namespace Isas.PaymentService.Services
{
    /// <summary>
    /// F22 (FR18) — ghi nhận tiêu thụ token của AIService + tổng hợp cho admin.
    ///
    /// Xem <see cref="AiUsageLog"/> cho lý do bảng này nằm ở Payment (tóm tắt: GEN-4 cấm AIService ghi DB,
    /// nên nó đẩy qua callback nội bộ; chi phí AI chỉ có nghĩa khi đọc cạnh doanh thu F19).
    ///
    /// Mọi phép cộng đẩy XUỐNG SQL (mẫu <see cref="RevenueService"/>): đây là bảng nhiều dòng nhất trong
    /// service (mỗi lượt gọi LLM một dòng, nhân <c>SelfConsistencyN</c> nếu bật), nạp về rồi cộng trong bộ
    /// nhớ là endpoint chết dần theo thời gian. Gộp theo Year/Month/Day thay vì <c>date_trunc</c> vì
    /// <c>date_trunc</c> chỉ có ở Npgsql — dùng nó thì phần gộp mất hẳn kiểm chứng ở test SQLite.
    /// </summary>
    public class AiUsageService : IAiUsageService
    {
        private readonly PaymentDbContext _db;
        private readonly AiPricingSettings _pricing;
        private readonly ILogger<AiUsageService> _logger;

        public AiUsageService(PaymentDbContext db, IOptions<AiPricingSettings> pricing,
            ILogger<AiUsageService> logger)
        {
            _db = db;
            _pricing = pricing.Value;
            _logger = logger;
        }

        public async Task<Guid> RecordAsync(RecordAiUsageRequest req, CancellationToken ct = default)
        {
            var model = string.IsNullOrWhiteSpace(req.Model) ? "unknown" : req.Model.Trim();
            var operation = string.IsNullOrWhiteSpace(req.Operation) ? "unknown" : req.Operation.Trim();

            if (!_pricing.Models.ContainsKey(model))
            {
                // Không im lặng: model lạ nghĩa là bảng giá đã lạc hậu so với thứ đang chạy thật, và mọi
                // con số chi phí của model đó sẽ tính theo giá mặc định (gần đúng, không đúng).
                _logger.LogWarning(
                    "F22: chưa có đơn giá cho model {Model} — dùng AiPricing:Default.", model);
            }

            var price = _pricing.Resolve(model);

            // Số âm chỉ có thể tới từ caller hỏng. Kẹp thay vì từ chối: mất một dòng thống kê không đáng,
            // nhưng để số âm vào thì tổng chi phí bị TRỪ đi và báo cáo sai theo hướng có lợi cho ta.
            var promptTokens = Math.Max(0, req.PromptTokens);
            var outputTokens = Math.Max(0, req.OutputTokens);
            var totalTokens = Math.Max(0, req.TotalTokens);
            var audioSeconds = req.AudioSeconds.HasValue ? Math.Max(0, req.AudioSeconds.Value) : (int?)null;

            // ĐƠN VỊ TÍNH TIỀN QUYẾT ĐỊNH BỞI DỮ LIỆU CALLER GỬI, KHÔNG BỞI BẢNG GIÁ. Caller là bên duy nhất
            // biết chắc lượt gọi vừa rồi bán theo phút hay theo token; bảng giá thì lạc hậu là chuyện thường
            // (đã có cảnh báo ngay trên cho ca đó). Rẽ nhánh theo bảng giá sẽ khiến một model chép lời MỚI —
            // thứ chưa kịp khai giá — âm thầm tính theo token và ra 0 đồng, tức là đúng cái lỗ đang bịt.
            decimal? pricePerMinute = null;
            if (audioSeconds.HasValue)
            {
                pricePerMinute = _pricing.ResolvePerMinute(model);
                if (pricePerMinute is null or <= 0m)
                {
                    // Không im lặng: đây là lượt CÓ TỐN TIỀN THẬT mà ta sắp ghi 0 đồng.
                    _logger.LogWarning(
                        "F22: lượt chép lời model {Model} ({Seconds}s) chưa có đơn giá theo phút — chi phí sẽ "
                        + "ghi 0. Khai AiPricing:Models:{Model}:PricePerMinuteUsd hoặc AiPricing:Default:"
                        + "PricePerMinuteUsd.", model, audioSeconds.Value, model);
                }
            }
            else if (price.PricePerMinuteUsd.HasValue)
            {
                // Model bán theo PHÚT mà lượt gọi lại không khai độ dài audio ⇒ nó sắp được tính bằng công
                // thức token và ra 0 đồng. Đây là hình dạng của một hợp đồng dây bị lệch: khoá JSON
                // `audioSeconds` bind hụt KHÔNG ném lỗi, nó chỉ điền null. Test ở CI chỉ khoá được phía
                // .NET; nếu AIService đổi tên khoá thì DÒNG LOG NÀY là thứ duy nhất báo trên production.
                _logger.LogWarning(
                    "F22: model {Model} tính tiền theo phút nhưng lượt {Operation} không gửi `audioSeconds` "
                    + "— chi phí sẽ ghi 0. Kiểm tra khoá JSON của callback AIService.", model, operation);
            }

            var entity = new AiUsageLog
            {
                Id = Guid.NewGuid(),
                Operation = Truncate(operation, 64),
                Model = Truncate(model, 64),
                PromptTokens = promptTokens,
                OutputTokens = outputTokens,
                // SDK không trả total (hoặc trả 0) → suy ra từ hai vế; đừng ghi 0 cho một lượt gọi có thật.
                TotalTokens = totalTokens > 0 ? totalTokens : promptTokens + outputTokens,
                InputPricePerMillionUsd = price.InputPerMillionUsd,
                OutputPricePerMillionUsd = price.OutputPerMillionUsd,
                AudioSeconds = audioSeconds,
                PricePerMinuteUsd = pricePerMinute,
                ResourceUrlsProposed = req.ResourceUrlsProposed,
                ResourceUrlsRejected = req.ResourceUrlsRejected,
                CreatedAt = DateTime.UtcNow
            };

            // Tính từ ĐƠN GIÁ ĐÃ SNAPSHOT trên chính entity — không đọc lại _pricing. Đọc lại thì lúc nào
            // cũng đúng ở hiện tại và vô nghĩa về sau; đây là chỗ khoá "tiền của dòng này tính bằng giá của
            // dòng này".
            entity.CostUsd = CostOf(entity);

            _db.AiUsageLogs.Add(entity);
            await _db.SaveChangesAsync(ct);
            return entity.Id;
        }

        /// <summary>
        /// Tiền của một lượt gọi, từ đơn giá đã snapshot trên chính dòng đó.
        ///
        /// RẼ NHÁNH THEO ĐƠN VỊ, và đây là chỗ dễ hỏng ngầm nhất của cả tính năng: lượt chép lời có
        /// <c>PromptTokens = OutputTokens = 0</c>, nên nếu bỏ nhánh phút thì công thức token cho ra ĐÚNG 0 —
        /// không exception, không log, chỉ là chi phí transcribe biến mất khỏi mọi báo cáo.
        ///
        /// <c>AudioSeconds != null</c> (chứ không phải <c>&gt; 0</c>) là dấu hiệu nhận biết: 0 giây nghĩa là
        /// "có chép lời, độ dài 0" ⇒ 0 đồng là đúng; null nghĩa là "không phải lượt chép lời" ⇒ tính theo
        /// token. Cùng quy ước null ≠ 0 với <c>ResourceUrlsProposed</c>.
        /// </summary>
        internal static decimal CostOf(AiUsageLog log) =>
            log.AudioSeconds.HasValue
                ? log.AudioSeconds.Value / 60m * (log.PricePerMinuteUsd ?? 0m)
                : log.PromptTokens / 1_000_000m * log.InputPricePerMillionUsd
                  + log.OutputTokens / 1_000_000m * log.OutputPricePerMillionUsd;

        public async Task<AiUsageReportResponse> GetReportAsync(
            DateTime from, DateTime to, AiUsageGranularity granularity, CancellationToken ct = default)
        {
            // Kỳ nửa mở [from, to) — hai kỳ liền nhau không đếm trùng một lượt gọi (mẫu F19).
            var rows = _db.AiUsageLogs.Where(u => u.CreatedAt >= from && u.CreatedAt < to);

            var totals = await rows
                .GroupBy(_ => 1)
                .Select(g => new
                {
                    Calls = (long)g.Count(),
                    Prompt = (long?)g.Sum(u => (long)u.PromptTokens) ?? 0,
                    Output = (long?)g.Sum(u => (long)u.OutputTokens) ?? 0,
                    Total = (long?)g.Sum(u => (long)u.TotalTokens) ?? 0,
                    // Cộng riêng vì đơn vị khác token — dòng chép lời có 0 token nhưng vẫn tốn tiền.
                    Audio = (long?)g.Sum(u => (long?)u.AudioSeconds) ?? 0,
                    Cost = (decimal?)g.Sum(u => u.CostUsd) ?? 0m,
                    UrlProposed = (long?)g.Sum(u => (long?)u.ResourceUrlsProposed) ?? 0,
                    UrlRejected = (long?)g.Sum(u => (long?)u.ResourceUrlsRejected) ?? 0,
                    UrlRows = (long)g.Count(u => u.ResourceUrlsProposed != null)
                })
                .FirstOrDefaultAsync(ct);

            var byOperation = await rows
                .GroupBy(u => u.Operation)
                .Select(g => new AiUsageByOperationRow
                {
                    Operation = g.Key,
                    Calls = g.Count(),
                    PromptTokens = g.Sum(u => (long)u.PromptTokens),
                    OutputTokens = g.Sum(u => (long)u.OutputTokens),
                    TotalTokens = g.Sum(u => (long)u.TotalTokens),
                    AudioSeconds = g.Sum(u => (long?)u.AudioSeconds) ?? 0,
                    CostUsd = g.Sum(u => u.CostUsd)
                })
                .ToListAsync(ct);

            var buckets = granularity == AiUsageGranularity.Month
                ? await rows
                    .GroupBy(u => new { u.CreatedAt.Year, u.CreatedAt.Month })
                    .Select(g => new
                    {
                        g.Key.Year,
                        g.Key.Month,
                        Day = 1,
                        Calls = (long)g.Count(),
                        Tokens = g.Sum(u => (long)u.TotalTokens),
                        Cost = g.Sum(u => u.CostUsd)
                    })
                    .ToListAsync(ct)
                : await rows
                    .GroupBy(u => new { u.CreatedAt.Year, u.CreatedAt.Month, u.CreatedAt.Day })
                    .Select(g => new
                    {
                        g.Key.Year,
                        g.Key.Month,
                        g.Key.Day,
                        Calls = (long)g.Count(),
                        Tokens = g.Sum(u => (long)u.TotalTokens),
                        Cost = g.Sum(u => u.CostUsd)
                    })
                    .ToListAsync(ct);

            return new AiUsageReportResponse
            {
                From = from,
                To = to,
                Granularity = granularity.ToString(),
                TotalCalls = totals?.Calls ?? 0,
                PromptTokens = totals?.Prompt ?? 0,
                OutputTokens = totals?.Output ?? 0,
                TotalTokens = totals?.Total ?? 0,
                AudioSeconds = totals?.Audio ?? 0,
                TotalCostUsd = totals?.Cost ?? 0m,
                ByOperation = byOperation.OrderByDescending(o => o.CostUsd).ToList(),
                Buckets = buckets
                    // Kind=Utc tường minh: Npgsql đọc timestamptz ra Utc, SQLite thì không đảm bảo gì —
                    // client không phải đoán múi giờ (mẫu F19).
                    .Select(b => new AiUsageBucketRow
                    {
                        PeriodStart = new DateTime(b.Year, b.Month, b.Day, 0, 0, 0, DateTimeKind.Utc),
                        Calls = b.Calls,
                        TotalTokens = b.Tokens,
                        CostUsd = b.Cost
                    })
                    .OrderBy(b => b.PeriodStart)
                    .ToList(),
                // Không có lượt nào sinh tài liệu ⇒ null, KHÔNG phải 0/0: "không áp dụng" khác "AI đề xuất
                // 0 link", và 0/0 hiển thị thành "0% bị loại" là một câu khẳng định ta không có cơ sở để nói.
                ResourceUrls = (totals?.UrlRows ?? 0) == 0 ? null : new AiResourceUrlStats
                {
                    Proposed = totals!.UrlProposed,
                    Rejected = totals.UrlRejected,
                    RejectedRate = totals.UrlProposed == 0
                        ? 0
                        : (double)totals.UrlRejected / totals.UrlProposed
                }
            };
        }

        private static string Truncate(string value, int max) =>
            value.Length <= max ? value : value[..max];
    }
}
