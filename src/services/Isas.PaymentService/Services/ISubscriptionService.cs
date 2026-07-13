namespace Isas.PaymentService.Services
{
    public interface ISubscriptionService
    {
        Task ActivateSubscriptionAsync(Guid userId, Guid orderId, Guid packageId, CancellationToken ct = default);

        Task<bool> HasActiveSubscriptionAsync(Guid userId, CancellationToken ct = default);
    }
}
