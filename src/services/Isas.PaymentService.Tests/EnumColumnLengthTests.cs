using Microsoft.EntityFrameworkCore;
using PaymentService.Models;

namespace Isas.PaymentService.Tests;

/// <summary>
/// Khoá bất biến: MỌI cột enum-lưu-string phải đủ dài cho giá trị enum DÀI NHẤT của nó.
///
/// Vì sao cần test cấu trúc thay vì test hành vi: SQLite (harness test) **KHÔNG enforce** độ dài
/// varchar, còn Postgres thì có. Nên một cột quá ngắn đi qua toàn bộ test suite mà vẫn xanh, rồi
/// hỏng 100% ở production. Đã xảy ra thật: `credit_reservations.funded_by` là varchar(16) trong khi
/// giá trị mới `SubscriptionMetered` dài 19 ⇒ MỌI reserve gói metered ném
/// "value too long for type character varying(16)", và lỗi còn bị `catch (DbUpdateException)`
/// hiểu nhầm thành đua session-id nên hiện ra dưới dạng "Sequence contains no elements".
///
/// Test này quét chính EF model nên tự phủ cả cột thêm sau này — không cần nhớ cập nhật.
/// </summary>
public class EnumColumnLengthTests
{
    [Fact]
    public void MoiCotEnumString_PhaiDuDaiChoGiaTriDaiNhat()
    {
        using var t = new PaymentTestDb();
        var viPham = new List<string>();

        foreach (var entity in t.Db.Model.GetEntityTypes())
        foreach (var prop in entity.GetProperties())
        {
            // chỉ xét property kiểu enum được convert sang string
            var clr = Nullable.GetUnderlyingType(prop.ClrType) ?? prop.ClrType;
            if (!clr.IsEnum) continue;
            if (prop.GetProviderClrType() != typeof(string) &&
                prop.FindTypeMapping()?.ClrType != typeof(string)) continue;

            var max = prop.GetMaxLength();
            if (max is null) continue;   // không giới hạn → không thể tràn

            var daiNhat = Enum.GetNames(clr).OrderByDescending(n => n.Length).First();
            if (daiNhat.Length > max)
                viPham.Add($"{entity.GetTableName()}.{prop.GetColumnName()}: max={max} " +
                           $"nhưng '{daiNhat}' dài {daiNhat.Length}");
        }

        Assert.True(viPham.Count == 0,
            "Cột enum ngắn hơn giá trị enum dài nhất — SQLite bỏ qua, Postgres sẽ ném " +
            "'value too long':\n  " + string.Join("\n  ", viPham));
    }

    // Ca cụ thể đã gây sự cố — giữ riêng để tên test nói đúng lỗi lịch sử.
    [Fact]
    public void FundedBy_ChuaDuocGiaTriSubscriptionMetered()
    {
        using var t = new PaymentTestDb();
        var prop = t.Db.Model.FindEntityType(typeof(CreditReservation))!
            .FindProperty(nameof(CreditReservation.FundedBy))!;

        Assert.True(prop.GetMaxLength() >= nameof(ReservationFunding.SubscriptionMetered).Length,
            $"funded_by max={prop.GetMaxLength()} < {nameof(ReservationFunding.SubscriptionMetered).Length}");
    }
}
