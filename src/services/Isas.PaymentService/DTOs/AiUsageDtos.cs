namespace Isas.PaymentService.DTOs
{
    /// <summary>
    /// F22 — body AIService đẩy về (<c>POST /internal/ai-usage</c>).
    ///
    /// CỐ Ý KHÔNG NHẬN TIỀN TỪ CALLER: chỉ token + tên model. Giá do Payment giữ và tự tính (xem
    /// <see cref="PaymentService.Models.AiPricingSettings"/>) — để AIService gửi luôn số tiền thì đơn giá
    /// phải sống ở hai nơi và sẽ lệch nhau vào đúng ngày Google đổi giá.
    /// </summary>
    public class RecordAiUsageRequest
    {
        public string Operation { get; set; } = string.Empty;
        public string Model { get; set; } = string.Empty;
        public int PromptTokens { get; set; }
        public int OutputTokens { get; set; }
        public int TotalTokens { get; set; }

        /// <summary>F15 — chỉ lượt sinh lý thuyết bài học gửi; null ở mọi lượt khác.</summary>
        public int? ResourceUrlsProposed { get; set; }
        public int? ResourceUrlsRejected { get; set; }
    }

    /// <summary>F22 — báo cáo tiêu thụ token/chi phí cho PlatformAdmin (AUTH-7).</summary>
    public class AiUsageReportResponse
    {
        public DateTime From { get; set; }
        public DateTime To { get; set; }
        public string Granularity { get; set; } = "Day";

        public long TotalCalls { get; set; }
        public long PromptTokens { get; set; }
        public long OutputTokens { get; set; }
        public long TotalTokens { get; set; }
        public decimal TotalCostUsd { get; set; }

        /// <summary>Tiêu thụ THEO ENDPOINT — trả lời "tiền đi đâu", không chỉ "hết bao nhiêu".</summary>
        public List<AiUsageByOperationRow> ByOperation { get; set; } = new();

        /// <summary>Tiêu thụ theo ngày/tháng.</summary>
        public List<AiUsageBucketRow> Buckets { get; set; } = new();

        /// <summary>F15 — tỉ lệ URL tài liệu bị allowlist tên miền loại (null khi kỳ này không có lượt nào
        /// sinh tài liệu). Cao bất thường = AI đang bịa tên miền, hoặc allowlist quá chặt.</summary>
        public AiResourceUrlStats? ResourceUrls { get; set; }
    }

    public class AiUsageByOperationRow
    {
        public string Operation { get; set; } = string.Empty;
        public long Calls { get; set; }
        public long PromptTokens { get; set; }
        public long OutputTokens { get; set; }
        public long TotalTokens { get; set; }
        public decimal CostUsd { get; set; }
    }

    public class AiUsageBucketRow
    {
        public DateTime PeriodStart { get; set; }
        public long Calls { get; set; }
        public long TotalTokens { get; set; }
        public decimal CostUsd { get; set; }
    }

    public class AiResourceUrlStats
    {
        public long Proposed { get; set; }
        public long Rejected { get; set; }
        /// <summary>Tỉ lệ bị loại [0,1]. Proposed = 0 → 0 (không chia cho 0).</summary>
        public double RejectedRate { get; set; }
    }

    public enum AiUsageGranularity
    {
        Day,
        Month
    }
}
