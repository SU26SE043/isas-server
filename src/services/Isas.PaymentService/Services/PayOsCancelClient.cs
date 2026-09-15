using PayOS;

namespace Isas.PaymentService.Services
{
    /// <summary>Impl thật của <see cref="IPayOsCancelClient"/> — bọc SDK payOS 2.1.0.</summary>
    public class PayOsCancelClient : IPayOsCancelClient
    {
        private readonly PayOSClient _payos;

        public PayOsCancelClient(PayOSClient payos)
        {
            _payos = payos;
        }

        public async Task CancelPaymentLinkAsync(long orderCode, string reason, CancellationToken ct = default)
        {
            try
            {
                await _payos.PaymentRequests.CancelAsync(orderCode, reason);
            }
            catch (PayOS.Exceptions.ApiException ex)
            {
                throw new PaymentGatewayException($"PayOS từ chối huỷ payment-link {orderCode}: {ex.Message}", ex);
            }
        }
    }
}
