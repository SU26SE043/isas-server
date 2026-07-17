using Isas.AuthService.Models;
using Microsoft.EntityFrameworkCore;

namespace Isas.AuthService.Tests;

/// <summary>
/// DB11: email UNIQUE (filtered index). Index EmailIndex trên normalized_email giờ là
/// UNIQUE + filtered (normalized_email IS NOT NULL) → 2 user cùng email chuẩn hoá bị chặn.
/// SQLite (EnsureCreated) áp partial unique index nên vẫn chứng minh được ràng buộc.
/// </summary>
public class UniqueEmailIndexTests
{
    [Fact]
    public void TwoUsers_SameNormalizedEmail_SecondInsertRejected()
    {
        using var test = new AuthTestDb();

        var u1 = new User
        {
            Id = Guid.NewGuid(),
            UserName = "alice@acme.test",
            NormalizedUserName = "ALICE@ACME.TEST",
            Email = "alice@acme.test",
            NormalizedEmail = "DUP@ACME.TEST"
        };
        test.Db.Users.Add(u1);
        test.Db.SaveChanges();

        var u2 = new User
        {
            Id = Guid.NewGuid(),
            // username khác (UserNameIndex unique) → chỉ email đụng nhau
            UserName = "bob@acme.test",
            NormalizedUserName = "BOB@ACME.TEST",
            Email = "bob@acme.test",
            NormalizedEmail = "DUP@ACME.TEST"
        };
        test.Db.Users.Add(u2);

        // Unique EmailIndex chặn user thứ 2 cùng normalized_email.
        Assert.Throws<DbUpdateException>(() => test.Db.SaveChanges());
    }

    [Fact]
    public void TwoUsers_DifferentNormalizedEmail_BothPersist()
    {
        using var test = new AuthTestDb();

        test.Db.Users.Add(new User
        {
            Id = Guid.NewGuid(),
            UserName = "carol@acme.test",
            NormalizedUserName = "CAROL@ACME.TEST",
            Email = "carol@acme.test",
            NormalizedEmail = "CAROL@ACME.TEST"
        });
        test.Db.Users.Add(new User
        {
            Id = Guid.NewGuid(),
            UserName = "dave@acme.test",
            NormalizedUserName = "DAVE@ACME.TEST",
            Email = "dave@acme.test",
            NormalizedEmail = "DAVE@ACME.TEST"
        });

        test.Db.SaveChanges();

        using var verify = test.NewContext();
        Assert.Equal(2, verify.Users.Count());
    }
}
