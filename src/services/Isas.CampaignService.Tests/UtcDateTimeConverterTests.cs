using System.Text.Json;
using System.Text.Json.Serialization;
using Isas.CampaignService.DTOs;
using Isas.Shared.Json;

namespace Isas.CampaignService.Tests;

/// <summary>
/// Regression: POST /api/v1/campaign trả 500 khi startsAt/expiresAt mang offset SỐ (+00:00) thay vì Z.
/// System.Text.Json parse offset số thành DateTimeKind.Local; Npgsql chỉ ghi được UTC vào timestamptz
/// → DbUpdateException → 500. Converter chuẩn hoá về UTC ngay tại biên deserialize.
/// (Phát hiện trong E2E INT-17 2026-07-18 — client Python isoformat() gửi "+00:00".)
/// </summary>
public class UtcDateTimeConverterTests
{
    // Khớp cấu hình thật ở Program.cs (AddJsonOptions).
    private static JsonSerializerOptions Opts()
    {
        var o = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        o.Converters.Add(new JsonStringEnumConverter());
        o.Converters.Add(new UtcDateTimeConverter());
        return o;
    }

    // ── Lõi: mọi Kind đầu vào đều ra UTC ────────────────────────────────
    [Fact]
    public void ToUtc_Local_ChuyenVeUtc()
    {
        var local = new DateTime(2026, 7, 18, 21, 0, 0, DateTimeKind.Local);
        var utc = UtcDateTimeConverter.ToUtc(local);
        Assert.Equal(DateTimeKind.Utc, utc.Kind);
        Assert.Equal(local.ToUniversalTime(), utc);
    }

    [Fact]
    public void ToUtc_Unspecified_CoiNhuUtc_KhongPhuThuocTimezoneMay()
    {
        var unspec = new DateTime(2026, 7, 18, 14, 0, 0, DateTimeKind.Unspecified);
        var utc = UtcDateTimeConverter.ToUtc(unspec);
        Assert.Equal(DateTimeKind.Utc, utc.Kind);
        Assert.Equal(14, utc.Hour);   // giữ nguyên giờ, chỉ gắn Kind
    }

    [Fact]
    public void ToUtc_Utc_GiuNguyen()
    {
        var utc0 = new DateTime(2026, 7, 18, 14, 0, 0, DateTimeKind.Utc);
        Assert.Equal(utc0, UtcDateTimeConverter.ToUtc(utc0));
        Assert.Equal(DateTimeKind.Utc, UtcDateTimeConverter.ToUtc(utc0).Kind);
    }

    // ── Ca gây 500 thật: offset số trên DTO có DateTime? ────────────────
    [Fact]
    public void Deserialize_CreateCampaign_OffsetSo_RaUtc_KhongConLocal()
    {
        // Chính xác payload đã làm 500 (Python datetime.isoformat()).
        var json = """
        {"title":"E2E","domain":"BE","timeLimitMinutes":30,
         "startsAt":"2026-07-18T14:19:00+00:00","expiresAt":"2026-07-20T14:19:00+00:00"}
        """;

        var req = JsonSerializer.Deserialize<CreateCampaignRequest>(json, Opts())!;

        Assert.NotNull(req.StartsAt);
        Assert.NotNull(req.ExpiresAt);
        // Điểm mấu chốt: Npgsql chỉ chấp nhận Utc. Trước fix, đây là Local → DbUpdateException → 500.
        Assert.Equal(DateTimeKind.Utc, req.StartsAt!.Value.Kind);
        Assert.Equal(DateTimeKind.Utc, req.ExpiresAt!.Value.Kind);
        // +00:00 ≡ Z ⇒ không đổi thời điểm.
        Assert.Equal(new DateTime(2026, 7, 18, 14, 19, 0, DateTimeKind.Utc), req.StartsAt.Value);
    }

    [Fact]
    public void Deserialize_OffsetLechMuiGio_QuyDoiDungThoiDiem()
    {
        var json = """{"title":"x","domain":"BE","timeLimitMinutes":30,"startsAt":"2026-07-18T21:00:00+07:00"}""";
        var req = JsonSerializer.Deserialize<CreateCampaignRequest>(json, Opts())!;
        Assert.Equal(DateTimeKind.Utc, req.StartsAt!.Value.Kind);
        Assert.Equal(new DateTime(2026, 7, 18, 14, 0, 0, DateTimeKind.Utc), req.StartsAt.Value);  // 21:00+07 = 14:00Z
    }

    [Fact]
    public void Deserialize_ZVanChay_KhongHoiQuy()
    {
        var json = """{"title":"x","domain":"BE","timeLimitMinutes":30,"startsAt":"2026-07-18T14:19:00Z"}""";
        var req = JsonSerializer.Deserialize<CreateCampaignRequest>(json, Opts())!;
        Assert.Equal(DateTimeKind.Utc, req.StartsAt!.Value.Kind);
        Assert.Equal(new DateTime(2026, 7, 18, 14, 19, 0, DateTimeKind.Utc), req.StartsAt.Value);
    }

    [Fact]
    public void Deserialize_Null_VanLaNull()
    {
        var json = """{"title":"x","domain":"BE","timeLimitMinutes":30,"startsAt":null}""";
        var req = JsonSerializer.Deserialize<CreateCampaignRequest>(json, Opts())!;
        Assert.Null(req.StartsAt);
    }

    // UpdateCampaignRequest cũng nhận 2 field này (PUT /campaign/{id}) → cùng lớp lỗi.
    [Fact]
    public void Deserialize_UpdateCampaign_OffsetSo_RaUtc()
    {
        var json = """{"expiresAt":"2026-08-01T10:00:00+00:00"}""";
        var req = JsonSerializer.Deserialize<UpdateCampaignRequest>(json, Opts())!;
        Assert.Equal(DateTimeKind.Utc, req.ExpiresAt!.Value.Kind);
    }
}
