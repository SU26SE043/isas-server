using Isas.CampaignService.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Isas.CampaignService.Tests;

/// <summary>
/// Khoá bất biến: MỌI cột enum-lưu-string trên CampaignDbContext phải đủ dài cho giá trị enum DÀI
/// NHẤT của nó.
///
/// Vì sao test cấu trúc thay vì test hành vi: SQLite (harness test) **KHÔNG enforce** độ dài varchar,
/// còn Postgres thì có. Một cột quá ngắn đi qua toàn bộ test suite mà vẫn xanh, rồi hỏng 100% ở
/// production ("value too long for type character varying(N)"). Đã xảy ra thật ở Payment
/// (<c>credit_reservations.funded_by</c> varchar(16) vs <c>SubscriptionMetered</c> dài 19) — xem
/// <c>Isas.PaymentService.Tests.EnumColumnLengthTests</c>. MON1-B1 thêm <c>session_flags.source</c>
/// (enum <see cref="FlagSource"/>) nên mang guard tương đương sang Campaign.
///
/// Test quét chính EF model ⇒ tự phủ cả cột enum-string thêm sau này, không cần nhớ cập nhật.
/// </summary>
public class CampaignEnumColumnLengthTests
{
    [Fact]
    public void MoiCotEnumString_PhaiDuDaiChoGiaTriDaiNhat()
    {
        using var t = new CampaignTestDb();
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

    // Ca cụ thể MON1-B1 — giữ riêng để tên test nói đúng cột đang khoá: đủ dài + NOT NULL + default 'Client'.
    // ⚠ NOT NULL được bảo đảm bởi HAI lớp độc lập — kiểu CLR non-nullable (FlagSource, không FlagSource?)
    // VÀ `.IsRequired()` trong config; chỉ gỡ CẢ HAI mới làm `IsNullable` thành true (mutation M5c).
    [Fact]
    public void SessionFlagSource_NotNull_DefaultClient_DuDaiChoServer()
    {
        using var t = new CampaignTestDb();
        var prop = t.Db.Model.FindEntityType(typeof(SessionFlag))!
            .FindProperty(nameof(SessionFlag.Source))!;

        Assert.False(prop.IsNullable, "session_flags.source phải NOT NULL.");

        // DB tự điền khi INSERT không truyền giá trị — đây là dấu vết của `.HasDefaultValue(...)`.
        // ⚠ KHÔNG kiểm mỗi `GetDefaultValue().ToString() == "Client"`: FlagSource.Client = 0 nên
        // `default(FlagSource).ToString()` cũng ra "Client" ⇒ gỡ `.HasDefaultValue` vẫn lọt. `ValueGenerated`
        // mới phân biệt được "có cấu hình DEFAULT" với "CLR zero tình cờ stringify thành Client".
        Assert.Equal(ValueGenerated.OnAdd, prop.ValueGenerated);
        Assert.Equal("Client", prop.GetDefaultValue()?.ToString());

        Assert.NotNull(prop.GetMaxLength());
        Assert.True(prop.GetMaxLength() >= nameof(FlagSource.Server).Length,
            $"source max={prop.GetMaxLength()} < {nameof(FlagSource.Server).Length}");
    }
}
