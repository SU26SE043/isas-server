using PaymentService.Models;

namespace Isas.PaymentService.DTOs
{
    public class InvoiceRequest
    {
        // POST /admin/invoices/close — chốt kỳ 1 org (PlatformAdmin). period_start/period_end tuỳ chọn
        // (mặc định: đầu tháng UTC → now) — chỉ để ghi mốc kỳ lên hóa đơn, không ảnh hưởng số tiền.
        public class CloseBillingPeriodRequest
        {
            public Guid OrgId { get; set; }
            public DateTime? PeriodStart { get; set; }
            public DateTime? PeriodEnd { get; set; }
        }

        public class InvoiceResponse
        {
            public Guid Id { get; set; }
            public OwnerType OwnerType { get; set; }
            public Guid OwnerId { get; set; }
            public Guid? AccountId { get; set; }
            public DateTime PeriodStart { get; set; }
            public DateTime PeriodEnd { get; set; }
            public int InterviewCount { get; set; }
            public decimal UnitPrice { get; set; }
            public decimal Amount { get; set; }
            public InvoiceStatus Status { get; set; }
            public DateTime CreatedAt { get; set; }

            public static InvoiceResponse ToResponse(Invoice i) => new InvoiceResponse
            {
                Id = i.Id,
                OwnerType = i.OwnerType,
                OwnerId = i.OwnerId,
                AccountId = i.AccountId,
                PeriodStart = i.PeriodStart,
                PeriodEnd = i.PeriodEnd,
                InterviewCount = i.InterviewCount,
                UnitPrice = i.UnitPrice,
                Amount = i.Amount,
                Status = i.Status,
                CreatedAt = i.CreatedAt
            };
        }
    }
}
