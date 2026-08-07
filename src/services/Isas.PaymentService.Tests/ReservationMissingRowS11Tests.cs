using Isas.PaymentService.Models;
using Isas.PaymentService.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using PaymentService.Models;
using System.Data.Common;

namespace Isas.PaymentService.Tests;

/// <summary>
/// Khép bất đối xứng còn lại của S11-CATCH: <c>ConsumeAsync</c>/<c>ReleaseAsync</c> vẫn dùng
/// <c>FirstAsync</c> trần ở nhánh <c>moved == 0</c>, trong khi <c>ReserveAsync</c> (đã sửa ở
/// S11-CATCH, cách đó ~200 dòng trong CÙNG file) dùng <c>FirstOrDefaultAsync</c> + rethrow.
///
/// Tập rỗng ở đó làm <c>FirstAsync</c> ném "Sequence contains no elements" — đúng thông điệp mơ hồ
/// mà S11-CATCH sinh ra để diệt, chỉ khác chỗ nổ. Hôm nay bất biến giữ cho tập không rỗng (row
/// reservation không bao giờ bị xoá), nên đây là phòng ngừa chứ không phải vá máu đang chảy — nhưng
/// nó CÙNG HÌNH DẠNG với bug vừa vá và giá gần bằng không.
/// </summary>
public class ReservationMissingRowS11Tests
{
    /// <summary>
    /// Làm row reservation BIẾN MẤT đúng khe giữa câu transition và lần đọc lại.
    ///
    /// Phải xoá HAI LẦN, và lý do đáng ghi lại: lần xoá đầu chạy trên chính connection đang mở
    /// transaction của service, nên <c>tx.RollbackAsync</c> ngay sau đó HỒI SINH row — lần đọc lại
    /// tìm thấy row và trả AlreadyFinalized, tức khe cần test không bao giờ mở ra (lượt đầu viết test
    /// này đã dính: interceptor báo Fired=True, DELETE báo 1 dòng, mà vẫn không có exception nào).
    /// Lần xoá thứ hai chen trước câu SELECT đọc lại — lúc đó transaction đã rollback nên nó nằm ngoài
    /// mọi transaction và tồn tại thật.
    /// </summary>
    private sealed class VanishRowAtRecheckInterceptor(SqliteConnection conn, Guid sessionId)
        : DbCommandInterceptor
    {
        private bool _updateSeen;
        private bool _vanished;

        /// <summary>Row đã thật sự biến mất đúng lúc đọc lại (không phải chỉ "interceptor có chạy").</summary>
        public bool Vanished => _vanished;

        private async Task DeleteAsync(CancellationToken ct)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM credit_reservations WHERE session_id = $sid";
            var p = cmd.CreateParameter();
            p.ParameterName = "$sid";
            // EF lưu Guid vào SQLite dạng TEXT CHỮ HOA. Ghi thường thì DELETE khớp 0 dòng và
            // interceptor thành no-op IM LẶNG — test vẫn xanh trong khi cuộc đua chưa hề xảy ra.
            p.Value = sessionId.ToString().ToUpperInvariant();
            cmd.Parameters.Add(p);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        // (1) Trước câu UPDATE transition → xoá để ExecuteUpdate khớp 0 row ⇒ vào nhánh moved == 0.
        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (!_updateSeen
                && command.CommandText.Contains("UPDATE", StringComparison.OrdinalIgnoreCase)
                && command.CommandText.Contains("credit_reservations", StringComparison.OrdinalIgnoreCase))
            {
                _updateSeen = true;
                await DeleteAsync(cancellationToken);
            }

            return await base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        // (2) Trước câu SELECT đọc lại (đã qua rollback) → xoá lần nữa, lần này nằm ngoài transaction.
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (_updateSeen && !_vanished
                && command.CommandText.Contains("SELECT", StringComparison.OrdinalIgnoreCase)
                && command.CommandText.Contains("credit_reservations", StringComparison.OrdinalIgnoreCase))
            {
                _vanished = true;
                await DeleteAsync(cancellationToken);
            }

            return await base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private static (CreditAccount acc, CreditReservation res) Seed(PaymentTestDb tdb, Guid sessionId)
    {
        var ownerId = Guid.NewGuid();
        var acc = new CreditAccount
        {
            Id = Guid.NewGuid(),
            OwnerType = OwnerType.User,
            OwnerId = ownerId,
            PaymentMode = PaymentMode.Prepaid,
            Status = CreditAccountStatus.Active,
            RemainingCredits = 5,
            ReservedCredits = 1,
            UpdatedAt = DateTime.UtcNow
        };
        var res = new CreditReservation
        {
            Id = Guid.NewGuid(),
            SessionId = sessionId,
            OwnerType = OwnerType.User,
            OwnerId = ownerId,
            Status = ReservationStatus.Reserved,
            FundedBy = ReservationFunding.Credit,
            PaymentMode = PaymentMode.Prepaid,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        tdb.Db.CreditAccounts.Add(acc);
        tdb.Db.CreditReservations.Add(res);
        tdb.Db.SaveChanges();
        return (acc, res);
    }

    private static CreditAccountService NewService(PaymentDbContext db) =>
        new(db, NullLogger<CreditAccountService>.Instance);

    // Consume: row biến mất giữa lần đọc đầu và câu transition → phải ném lỗi NÓI RÕ NGUYÊN NHÂN,
    // không phải "Sequence contains no elements".
    [Fact]
    public async Task Consume_RowBienMat_NemLoiRoRang_KhongPhaiSequenceContainsNoElements()
    {
        using var tdb = new PaymentTestDb();
        var sessionId = Guid.NewGuid();
        Seed(tdb, sessionId);

        var fault = new VanishRowAtRecheckInterceptor(tdb.Connection, sessionId);
        var svc = NewService(tdb.NewContext(null, new IInterceptor[] { fault }));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => svc.ConsumeAsync(sessionId));

        Assert.True(fault.Vanished, "tiền đề của test: row phải thật sự biến mất đúng lúc đọc lại");
        Assert.DoesNotContain("Sequence contains no elements", ex.Message);
        Assert.Contains(sessionId.ToString(), ex.Message);
    }

    // Release: cùng khe, cùng mẫu.
    [Fact]
    public async Task Release_RowBienMat_NemLoiRoRang_KhongPhaiSequenceContainsNoElements()
    {
        using var tdb = new PaymentTestDb();
        var sessionId = Guid.NewGuid();
        Seed(tdb, sessionId);

        var fault = new VanishRowAtRecheckInterceptor(tdb.Connection, sessionId);
        var svc = NewService(tdb.NewContext(null, new IInterceptor[] { fault }));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => svc.ReleaseAsync(sessionId));

        Assert.True(fault.Vanished, "tiền đề của test: row phải thật sự biến mất đúng lúc đọc lại");
        Assert.DoesNotContain("Sequence contains no elements", ex.Message);
        Assert.Contains(sessionId.ToString(), ex.Message);
    }

    // Đối chứng: đua BÌNH THƯỜNG (row còn đó, ai đó vừa consume trước) vẫn hấp thụ đúng theo PAY-11 —
    // bản vá không được biến ca đua hợp lệ thành lỗi.
    [Fact]
    public async Task Consume_DuaHopLe_RowConDo_VanHapThu()
    {
        using var tdb = new PaymentTestDb();
        var sessionId = Guid.NewGuid();
        var (_, res) = Seed(tdb, sessionId);

        // Mô phỏng "ai đó vừa consume trước": lật trạng thái trước khi gọi.
        res.Status = ReservationStatus.Consumed;
        tdb.Db.SaveChanges();

        var svc = NewService(tdb.NewContext());
        var result = await svc.ConsumeAsync(sessionId);

        Assert.Equal(res.Id, result.ReservationId);
    }
}
