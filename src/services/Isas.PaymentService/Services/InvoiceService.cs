using Isas.PaymentService.Models;
using Isas.PaymentService.DTOs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PaymentService.Models;
using static Isas.PaymentService.DTOs.InvoiceRequest;
using static Isas.PaymentService.Services.IInvoiceService;

namespace Isas.PaymentService.Services
{
    /// <summary>
    /// P8b — hóa đơn postpaid (payment.md §Postpaid chốt kỳ + §Invoice + §State machine Invoice).
    /// </summary>
    public class InvoiceService : IInvoiceService
    {
        private readonly PaymentDbContext _db;
        private readonly IOrderService _orders;
        private readonly IOptions<BillingSettings> _billing;

        public InvoiceService(PaymentDbContext db, IOrderService orders, IOptions<BillingSettings> billing)
        {
            _db = db;
            _orders = orders;
            _billing = billing;
        }

        public async Task<CloseBillingPeriodResult> CloseBillingPeriodAsync(
            Guid orgId, DateTime? periodStart = null, DateTime? periodEnd = null, CancellationToken ct = default)
        {
            var unitPrice = _billing.Value.UnitPrice;
            if (unitPrice <= 0)
                return new CloseBillingPeriodResult(CloseBillingPeriodOutcome.UnitPriceNotConfigured, null);

            var now = DateTime.UtcNow;
            var pEnd = periodEnd ?? now;
            // Mốc kỳ chỉ để hiển thị/đối soát — không ảnh hưởng số tiền. Mặc định đầu tháng UTC → now.
            var pStart = periodStart ?? new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);

            // Đọc ví Org để snapshot period_usage. AsNoTracking: reset làm bằng ExecuteUpdate (atomic),
            // không cần entity tracked.
            var account = await _db.CreditAccounts.AsNoTracking()
                .FirstOrDefaultAsync(a => a.OwnerType == OwnerType.Org && a.OwnerId == orgId, ct);
            if (account is null)
                return new CloseBillingPeriodResult(CloseBillingPeriodOutcome.WalletMissing, null);
            // F23/BK24 — org Prepaid không có "kỳ postpaid" để chốt (period_usage của Prepaid không dùng
            // cho billing kiểu này). Guard chặn chốt kỳ nhầm org, tránh sinh hóa đơn vô nghĩa.
            if (account.PaymentMode != PaymentMode.Postpaid)
                return new CloseBillingPeriodResult(CloseBillingPeriodOutcome.NotPostpaid, null);

            // Kiểm tra xem kỳ này đã được chốt chưa
            var alreadyClosed = await _db.Invoices.AsNoTracking().AnyAsync(i => i.OwnerType == OwnerType.Org && i.OwnerId == orgId && i.PeriodEnd == pEnd, ct);
            if (alreadyClosed)
                return new CloseBillingPeriodResult(CloseBillingPeriodOutcome.AlreadyClosed, null);

            // Nếu tổng số sử dụng trong kỳ bằng hoặc nhỏ hơn 0, không lập hóa đơn
            if ((account.PeriodUsage ?? 0) <= 0)
                return new CloseBillingPeriodResult(CloseBillingPeriodOutcome.NothingToBill, null);
            // 1 transaction (payment.md §Postpaid): snapshot period_usage → tạo invoice → reset period_usage=0.
            // Fail giữa chừng → rollback cả 2 (không mất/nhân nợ).
            // DB25b — bọc IExecutionStrategy vì Npgsql bật EnableRetryOnFailure: chiến lược retry
            // TỪ CHỐI transaction do người dùng tự mở, và khi chạy lại delegate nó KHÔNG reset change
            // tracker (chi tiết + hệ quả với sổ cái: xem <see cref="DbRetry"/>).
            return await DbRetry.RunAsync(_db, async Task<CloseBillingPeriodResult> () =>
            {
                await using var tx = await _db.Database.BeginTransactionAsync(ct);

                var count = account.PeriodUsage ?? 0;
                var amount = count * unitPrice;

                var invoice = new Invoice
                {
                    Id = Guid.NewGuid(),
                    OwnerType = OwnerType.Org,
                    OwnerId = orgId,
                    AccountId = account.Id,
                    PeriodStart = pStart,
                    PeriodEnd = pEnd,
                    InterviewCount = count,
                    UnitPrice = unitPrice,
                    Amount = amount,
                    Status = InvoiceStatus.Issued,
                    // F23/BK24 — hạn tất toán = periodEnd + Billing:InvoiceDueDays (snapshot lúc lập, đổi
                    // config sau không hồi tố hóa đơn đã có DueAt).
                    DueAt = pEnd + TimeSpan.FromDays(_billing.Value.InvoiceDueDays),
                    CreatedAt = now
                };
                _db.Invoices.Add(invoice);

                // BK17 — TRỪ ĐÚNG snapshot đã chốt (period_usage − count) CÙNG transaction, KHÔNG reset=0.
                // count = period_usage đọc lúc snapshot ở trên (đã chốt vào invoice.interview_count). Nếu có
                // Consume commit XEN GIỮA snapshot-read và câu update này (race), reset=0 sẽ NUỐT lượt đó;
                // trừ-snapshot giữ phần phát sinh sau snapshot lại cho kỳ sau: period_usage_hiện_tại − count.
                // Self-referential SQL (atomic) — Context7 EF Core ExecuteUpdate.
                await _db.CreditAccounts
                    .Where(a => a.OwnerType == OwnerType.Org && a.OwnerId == orgId)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(a => a.PeriodUsage, a => (int?)((a.PeriodUsage ?? 0) - count))
                        .SetProperty(a => a.UpdatedAt, _ => DateTime.UtcNow), ct);

                await _db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);

                return new CloseBillingPeriodResult(CloseBillingPeriodOutcome.Closed, InvoiceResponse.ToResponse(invoice));
            });
        }

        /// <summary>
        /// Chốt kỳ thanh toán đã đến hạn.
        /// </summary>
        /// <param name="asOfUtc">Thời điểm UTC để xác định kỳ cần chốt.</param>
        /// <param name="ct">Token hủy bỏ.</param>
        /// <returns>Số lượng kỳ đã chốt.</returns>
        public async Task<int> CloseDuePeriodsAsync(DateTime asOfUtc, CancellationToken ct = default)
        {
            var currentMonthStart = new DateTime(asOfUtc.Year, asOfUtc.Month, 1, 0, 0, 0, DateTimeKind.Utc);
            var previousMonthStart = currentMonthStart.AddMonths(-1);

            var postpaidOrgIds = await _db.CreditAccounts.AsNoTracking()
                .Where(a => a.OwnerType == OwnerType.Org && a.PaymentMode == PaymentMode.Postpaid)
                .Select(a => a.OwnerId)
                .ToListAsync(ct);

            int closedCount = 0;
            foreach (var orgId in postpaidOrgIds)
            {
                var result = await CloseBillingPeriodAsync(orgId, previousMonthStart, currentMonthStart, ct);
                if (result.Outcome == CloseBillingPeriodOutcome.Closed)
                {
                    closedCount++;
                }
            }

            return closedCount;
        }

        /// <summary>
        /// Danh sách việc cho admin: org nào sắp chạm hạn mức, org nào còn nợ, org nào chưa từng chốt kỳ.
        /// Trước đây admin chỉ tra được ví theo từng ownerId, nên không có cách nào biết còn sót org nào; và job chốt kỳ tự động sẽ chạy trong bóng tối nếu không có gì để đối chiếu.
        /// Headroom và LastInvoicePeriodEnd để null khi chưa đặt hạn mức / chưa từng chốt kỳ, không quy về 0 — hai ca đó dẫn tới hành động khác hẳn nhau.
        /// Mỗi org tốn 3 truy vấn (N+1); chấp nhận được vì số org trả sau nhỏ và đây là màn admin.
        /// </summary>
        public async Task<List<PostpaidOverviewRow>> GetPostpaidOverviewAsync(CancellationToken ct = default)
        {
            var unitPrice = _billing.Value.UnitPrice;
            var postpaidAccounts = await _db.CreditAccounts.AsNoTracking()
                .Where(a => a.OwnerType == OwnerType.Org && a.PaymentMode == PaymentMode.Postpaid)
                .Select(a => new { a.OwnerId, a.CreditLimit, a.PeriodUsage, a.ReservedCredits })
                .ToListAsync(ct);

            var overviewRows = new List<PostpaidOverviewRow>();

            foreach (var acc in postpaidAccounts)
            {
                var usage = acc.PeriodUsage ?? 0;
                var unpaidCount = await _db.Invoices.AsNoTracking()
                    .Where(i => i.OwnerType == OwnerType.Org && i.OwnerId == acc.OwnerId && (i.Status == InvoiceStatus.Issued || i.Status == InvoiceStatus.Overdue))
                    .CountAsync(ct);
                var hasOverdue = await _db.Invoices.AsNoTracking()
                    .AnyAsync(i => i.OwnerType == OwnerType.Org && i.OwnerId == acc.OwnerId && i.Status == InvoiceStatus.Overdue, ct);
                var lastPeriodEnd = await _db.Invoices.AsNoTracking()
                    .Where(i => i.OwnerType == OwnerType.Org && i.OwnerId == acc.OwnerId)
                    .OrderByDescending(i => i.PeriodEnd)
                    .Select(i => (DateTime?)i.PeriodEnd)
                    .FirstOrDefaultAsync(ct);
                var headroom = acc.CreditLimit is null ? (int?)null : acc.CreditLimit - usage - acc.ReservedCredits;

                overviewRows.Add(new PostpaidOverviewRow(acc.OwnerId, acc.CreditLimit, usage, acc.ReservedCredits,
                    headroom, usage * unitPrice, unpaidCount, hasOverdue, lastPeriodEnd));
            }

            return overviewRows.OrderByDescending(r => r.HasOverdue).ThenByDescending(r => r.PendingAmountVnd).ToList();
        }

        public async Task<int> MarkOverdueInvoicesAsync(int graceHours, CancellationToken ct = default)
        {
            var logger = new LoggerFactory().CreateLogger<InvoiceService>();
            var cutoff = DateTime.UtcNow.AddHours(-graceHours);

            // F23/BK24 — "phanh hỏng câm": Issued mà DueAt=NULL (hóa đơn tạo trước migration/lỗi ghi) sẽ
            // KHÔNG BAO GIỜ bị quét bởi câu ExecuteUpdate dưới (điều kiện DueAt != null loại chúng ra) →
            // phải LOG để ai đó thấy và xử lý tay, không được âm thầm bỏ qua mãi mãi.
            var missingDueAt = await _db.Invoices.AsNoTracking()
                .CountAsync(i => i.Status == InvoiceStatus.Issued && i.DueAt == null, ct);
            if (missingDueAt > 0)
                logger?.LogWarning(
                    "F23/BK24 — {Count} hóa đơn Issued KHÔNG có DueAt → KHÔNG thể tự động chuyển Overdue, cần xử lý tay.",
                    missingDueAt);

            return await _db.Invoices
                .Where(i => i.Status == InvoiceStatus.Issued && i.DueAt != null && i.DueAt < cutoff)
                .ExecuteUpdateAsync(s => s.SetProperty(i => i.Status, InvoiceStatus.Overdue), ct);
        }

        public async Task<PayInvoiceResult> PayInvoiceAsync(
            OwnerType ownerType, Guid ownerId, Guid invoiceId, CancellationToken ct = default)
        {
            var invoice = await _db.Invoices.AsNoTracking()
                .FirstOrDefaultAsync(i => i.Id == invoiceId, ct);

            // Không tồn tại HOẶC của chủ khác → 404 (owner-scope, không lộ hóa đơn người khác).
            if (invoice is null || invoice.OwnerType != ownerType || invoice.OwnerId != ownerId)
                return PayInvoiceResult.NotFound();

            // Chỉ Issued/Overdue mới tất toán được; Paid (đã trả) / Void (hủy) → 409 no-op.
            if (invoice.Status != InvoiceStatus.Issued && invoice.Status != InvoiceStatus.Overdue)
                return PayInvoiceResult.NotPayable();

            var order = await _orders.CreateInvoiceSettlementOrderAsync(invoice, ct);
            return PayInvoiceResult.Created(order);
        }

        public async Task<List<InvoiceResponse>> GetInvoicesAsync(
            OwnerType ownerType, Guid ownerId, CancellationToken ct = default)
        {
            return await _db.Invoices.AsNoTracking()
                .Where(i => i.OwnerType == ownerType && i.OwnerId == ownerId)
                .OrderByDescending(i => i.CreatedAt)
                .Select(i => InvoiceResponse.ToResponse(i))
                .ToListAsync(ct);
        }

        public async Task<InvoiceResponse?> GetInvoiceAsync(
            OwnerType ownerType, Guid ownerId, Guid invoiceId, CancellationToken ct = default)
        {
            var invoice = await _db.Invoices.AsNoTracking()
                .FirstOrDefaultAsync(i => i.Id == invoiceId, ct);

            // Owner-scope: không tồn tại / của chủ khác → null (404, không lộ hóa đơn người khác).
            if (invoice is null || invoice.OwnerType != ownerType || invoice.OwnerId != ownerId)
                return null;

            return InvoiceResponse.ToResponse(invoice);
        }
    }
}
