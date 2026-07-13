namespace Isas.PaymentService.Services
{
    public class SubscriptionService : ISubscriptionService
    {
        public Task ActivateSubscriptionAsync(Guid userId, Guid orderId, Guid packageId, CancellationToken ct = default)
        {
            throw new NotImplementedException();
        }

        public Task<bool> HasActiveSubscriptionAsync(Guid userId, CancellationToken ct = default)
        {
            throw new NotImplementedException();
        }
    }
}
