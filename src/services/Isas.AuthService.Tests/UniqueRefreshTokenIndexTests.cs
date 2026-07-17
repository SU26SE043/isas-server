using Isas.AuthService.Models;
using Microsoft.EntityFrameworkCore;

namespace Isas.AuthService.Tests;

/// <summary>
/// DB12: unique index trên refresh_tokens.token. Token là SHA-256 hash ngẫu nhiên →
/// unique an toàn; luồng refresh (mỗi lần sinh hash mới, khác nhau) không bị chặn,
/// còn hash trùng thì bị từ chối. Logic hash không đổi (chỉ thêm index).
/// </summary>
public class UniqueRefreshTokenIndexTests
{
    private static User NewUser() => new()
    {
        Id = Guid.NewGuid(),
        UserName = "u@acme.test",
        NormalizedUserName = "U@ACME.TEST",
        Email = "u@acme.test",
        NormalizedEmail = "U@ACME.TEST"
    };

    [Fact]
    public void RefreshFlow_DistinctTokenHashes_BothPersist()
    {
        using var test = new AuthTestDb();
        var user = NewUser();
        test.Db.Users.Add(user);
        test.Db.SaveChanges();

        // Luồng refresh: token cũ + token thay thế (2 hash khác nhau) cùng tồn tại.
        test.Db.RefreshTokens.Add(new RefreshToken
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            Token = "hash-old",
            ExpiresAt = DateTime.UtcNow.AddDays(7)
        });
        test.Db.RefreshTokens.Add(new RefreshToken
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            Token = "hash-new",
            ExpiresAt = DateTime.UtcNow.AddDays(7)
        });
        test.Db.SaveChanges();

        using var verify = test.NewContext();
        Assert.Equal(2, verify.RefreshTokens.Count());
        // Lookup theo hash (validate refresh) vẫn trả đúng bản ghi.
        Assert.NotNull(verify.RefreshTokens.SingleOrDefault(x => x.Token == "hash-new"));
    }

    [Fact]
    public void DuplicateTokenHash_SecondInsertRejected()
    {
        using var test = new AuthTestDb();
        var user = NewUser();
        test.Db.Users.Add(user);
        test.Db.SaveChanges();

        test.Db.RefreshTokens.Add(new RefreshToken
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            Token = "same-hash",
            ExpiresAt = DateTime.UtcNow.AddDays(7)
        });
        test.Db.SaveChanges();

        test.Db.RefreshTokens.Add(new RefreshToken
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            Token = "same-hash",
            ExpiresAt = DateTime.UtcNow.AddDays(7)
        });

        Assert.Throws<DbUpdateException>(() => test.Db.SaveChanges());
    }
}
