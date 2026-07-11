using System.Text.Json;
using PayOS;
using PayOS.Models.V2.PaymentRequests;

namespace Isas.PaymentService.Services
{
    /// <summary>
    /// P3 — impl thật của <see cref="IPayOsQueryClient"/> bọc SDK payOS 2.1.0
    /// (<c>PaymentRequests.GetAsync(orderCode)</c> = getPaymentLinkInformation). Chỉ đọc (query), KHÔNG
    /// ghi DB — quyết định cộng credit nằm ở <see cref="OrderStatusService"/> (reuse WebhookService).
    /// </summary>
    public class PayOsQueryClient : IPayOsQueryClient
    {
        private readonly PayOSClient _payos;

        public PayOsQueryClient(PayOSClient payos)
        {
            _payos = payos;
        }

        public async Task<PayOsPaymentInfo> GetPaymentInfoAsync(long orderCode, CancellationToken ct = default)
        {
            var link = await _payos.PaymentRequests.GetAsync(orderCode);

            var status = link.Status switch
            {
                PaymentLinkStatus.Paid => PayOsPaymentStatus.Paid,
                PaymentLinkStatus.Processing => PayOsPaymentStatus.Processing,
                PaymentLinkStatus.Underpaid => PayOsPaymentStatus.Underpaid,
                PaymentLinkStatus.Cancelled => PayOsPaymentStatus.Cancelled,
                PaymentLinkStatus.Expired => PayOsPaymentStatus.Expired,
                PaymentLinkStatus.Failed => PayOsPaymentStatus.Failed,
                _ => PayOsPaymentStatus.Pending
            };

            // gateway_txn_id = reference giao dịch ngân hàng (giống webhook data.Reference) — lấy giao dịch
            // gần nhất nếu có. raw = JSON gốc PaymentLink để lưu bằng chứng đối soát (append-only).
            var txnRef = link.Transactions?.LastOrDefault()?.Reference;
            var raw = JsonSerializer.Serialize(link);

            return new PayOsPaymentInfo(status, txnRef, raw);
        }
    }
}
