using Microsoft.EntityFrameworkCore;
using PaymentService.Models;

namespace Isas.PaymentService.Services
{
    public class CreditAccountService : ICreditAccountService
    {
        private readonly PaymentDbContext _db;

        public CreditAccountService(PaymentDbContext db)
        {
            _db = db;
        }

        public async Task<CreditAccount> CreateAccountAsync(OwnerType ownerType, Guid ownerId, CancellationToken ct = default)
        {
            var exists = await _db.CreditAccounts
                .AnyAsync(x => x.OwnerType == ownerType && x.OwnerId == ownerId, ct);

            if (exists)
                throw new InvalidOperationException($"Credit account already exists for {ownerType}:{ownerId}.");

            var account = new CreditAccount
            {
                Id = Guid.NewGuid(),
                OwnerType = ownerType,
                OwnerId = ownerId,
                PaymentMode = PaymentMode.Prepaid,
                Status = CreditAccountStatus.Active,
                RemainingCredits = 0,
                ReservedCredits = 0,
                UpdatedAt = DateTime.UtcNow
            };

            _db.CreditAccounts.Add(account);
            await _db.SaveChangesAsync(ct);

            return account;
        }

        public async Task<CreditAccount?> GetAccountAsync(OwnerType ownerType, Guid ownerId, CancellationToken ct = default)
        {
            return await _db.CreditAccounts
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.OwnerType == ownerType && x.OwnerId == ownerId, ct);
        }
    }
}
