using Isas.PaymentService.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PaymentService.Models;

namespace Isas.PaymentService.Tests;

/// <summary>
/// Chi tiền hoàn tự động qua kênh chi payOS. Tính năng này CHUYỂN TIỀN THẬT RA KHỎI tài khoản công ty,
/// nên test ở đây bám vào đúng những chỗ hỏng thì mất tiền, không phải chỗ hỏng thì xấu UI:
///
/// <list type="bullet">
/// <item>khoá idempotency phải nằm trên đĩa TRƯỚC lời gọi mạng, và phải được DÙNG LẠI ở mọi lần sau;</item>
/// <item>timeout KHÔNG được đọc thành thất bại;</item>
/// <item>chỉ trạng thái "đã chuyển xong" mới được đóng dấu đã hoàn;</item>
/// <item>đơn đã đóng dấu thì không chuyển lần nữa;</item>
/// <item>không dựng được đích chuyển thì KHÔNG đoán.</item>
/// </list>
/// </summary>
public class RefundPayoutTests
{
    private static readonly Guid Admin = Guid.NewGuid();

    /// <summary>BIN thật của một ngân hàng (dạng 6 số) — đường chi tự động chỉ nhận dạng này.</summary>
    private const string RealBin = "970422";

    /// <summary>
    /// Dạng mã 8 số mà webhook thật trả về cho ĐA SỐ giao dịch (đo trên dữ liệu production: 12/15).
    /// Không phải BIN ⇒ phải rơi về chuyển tay.
    /// </summary>
    private const string EightDigitBankId = "01203001";

    // ── Fake payOS ──────────────────────────────────────────────────────────────────────────────
    private sealed class FakePayout : IPayoutClient
    {
        public bool IsConfigured { get; set; } = true;
        public long? Balance { get; set; } = 100_000_000;

        public Func<Guid, PayoutCreateResult>? OnCreate { get; set; }
        public Func<string, PayoutSnapshot?>? OnGet { get; set; }

        public List<Guid> KeysUsed { get; } = new();
        public int CreateCalls => KeysUsed.Count;

        public Task<PayoutCreateResult> CreateAsync(
            string referenceId, long amountVnd, string description, string toBin,
            string toAccountNumber, Guid idempotencyKey, CancellationToken ct = default)
        {
            KeysUsed.Add(idempotencyKey);
            return Task.FromResult(OnCreate?.Invoke(idempotencyKey)
                ?? new PayoutCreateResult(PayoutCallOutcome.Created,
                    new PayoutSnapshot(PayoutState.InFlight, "payout_1", null, null), null));
        }

        public Task<PayoutSnapshot?> GetAsync(string payoutId, CancellationToken ct = default) =>
            Task.FromResult(OnGet?.Invoke(payoutId));

        public Task<long?> GetBalanceAsync(CancellationToken ct = default) => Task.FromResult(Balance);
    }

    private static RefundService NewService(
        PaymentTestDb tdb, FakePayout payout, RefundPayoutSettings? options = null)
    {
        var opts = options ?? new RefundPayoutSettings { Enabled = true };
        return new RefundService(
            tdb.Db, null, payout, new BankBinResolver(Options.Create(opts)), Options.Create(opts));
    }

    /// <summary>Đơn đã Refunded + webhook gốc mang tài khoản người trả (nguồn của đích chuyển).</summary>
    private static async Task<Order> SeedRefundedAsync(
        PaymentTestDb tdb,
        string? bankId = RealBin,
        string? accountNumber = "0123456789",
        string? accountName = "NGUYEN VAN A",
        DateTime? settledAt = null,
        long amountVnd = 100_000)
    {
        var order = new Order
        {
            Id = Guid.NewGuid(),
            OwnerType = OwnerType.User,
            OwnerId = Guid.NewGuid(),
            Kind = OrderKind.CreditPack,
            Status = OrderStatus.Refunded,
            AmountVnd = amountVnd,
            PayosOrderCode = Random.Shared.NextInt64(1, long.MaxValue / 2),
            ExpiredAt = DateTime.UtcNow.AddMinutes(30),
            PaidAt = DateTime.UtcNow.AddMinutes(-30),
            RefundedAt = DateTime.UtcNow.AddMinutes(-5),
            RefundSettledAt = settledAt,
            CreatedAt = DateTime.UtcNow.AddHours(-1),
        };
        tdb.Db.Orders.Add(order);

        tdb.Db.PaymentTransactions.Add(new PaymentTransaction
        {
            Id = Guid.NewGuid(),
            OrderId = order.Id,
            Gateway = "payos",
            Status = "success",
            RawWebhookPayload = Payload(bankId, accountNumber, accountName),
            CreatedAt = DateTime.UtcNow.AddMinutes(-30),
        });

        await tdb.Db.SaveChangesAsync();
        return order;
    }

    // Hình dạng webhook THẬT của payOS (code/desc/success/data/signature) — đích chuyển được đọc từ đây.
    private static string Payload(string? bankId, string? accountNumber, string? accountName) =>
        $$"""
        {"code":"00","desc":"success","success":true,"data":{
          "orderCode":123,"amount":100000,"description":"x","accountNumber":"12345678",
          "reference":"TF230204212323","transactionDateTime":"2023-02-04 18:25:00","currency":"VND",
          "paymentLinkId":"abc","code":"00","desc":"Thành công",
          "counterAccountBankId":"{{bankId}}","counterAccountBankName":"NH",
          "counterAccountName":"{{accountName}}","counterAccountNumber":"{{accountNumber}}",
          "virtualAccountName":"","virtualAccountNumber":""},"signature":"sig"}
        """;

    private static async Task<Order> ReloadAsync(PaymentTestDb tdb, Guid orderId) =>
        await tdb.Db.Orders.AsNoTracking().FirstAsync(o => o.Id == orderId);

    // ── Khoá idempotency: bền vững TRƯỚC lời gọi, và dùng lại mãi ───────────────────────────────

    [Fact]
    public async Task Initiate_GhiKhoaIdempotency_TruocKhiGoiPayOS()
    {
        using var tdb = new PaymentTestDb();
        var order = await SeedRefundedAsync(tdb);
        var payout = new FakePayout();

        // Đọc DB ngay TRONG lúc payOS đang được gọi: nếu khoá chưa nằm trên đĩa ở thời điểm này thì một
        // cú crash giữa chừng sẽ làm mất dấu lệnh, và lần thử lại sinh khoá mới ⇒ chuyển tiền hai lần.
        Guid? keyOnDiskDuringCall = null;
        payout.OnCreate = _ =>
        {
            using var probe = tdb.NewContext();
            keyOnDiskDuringCall = probe.Orders.AsNoTracking().First(o => o.Id == order.Id).PayoutIdempotencyKey;
            return new PayoutCreateResult(PayoutCallOutcome.Created,
                new PayoutSnapshot(PayoutState.InFlight, "payout_1", null, null), null);
        };

        await NewService(tdb, payout).InitiateRefundPayoutAsync(order.Id, Admin);

        Assert.NotNull(keyOnDiskDuringCall);
        Assert.Equal(keyOnDiskDuringCall, payout.KeysUsed.Single());
    }

    [Fact]
    public async Task GoiLaiNhieuLan_DungLaiKhoaCu_KhongBaoGioSinhKhoaMoi()
    {
        using var tdb = new PaymentTestDb();
        var order = await SeedRefundedAsync(tdb);
        var payout = new FakePayout
        {
            OnCreate = _ => new PayoutCreateResult(PayoutCallOutcome.Unknown, null, "timeout"),
            OnGet = _ => null,
        };
        var svc = NewService(tdb, payout);

        await svc.InitiateRefundPayoutAsync(order.Id, Admin);
        await svc.InitiateRefundPayoutAsync(order.Id, Admin);
        await svc.PollRefundPayoutAsync(order.Id);

        Assert.True(payout.CreateCalls >= 2, "phải có gọi lại để dò kết quả");
        Assert.Single(payout.KeysUsed.Distinct());
    }

    // ── Timeout ≠ thất bại ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Timeout_GiuDangBay_KhongDanhDauHong()
    {
        using var tdb = new PaymentTestDb();
        var order = await SeedRefundedAsync(tdb);
        var payout = new FakePayout { OnCreate = _ => new PayoutCreateResult(PayoutCallOutcome.Unknown, null, "timeout") };

        var result = await NewService(tdb, payout).InitiateRefundPayoutAsync(order.Id, Admin);

        Assert.Equal(RefundPayoutOutcome.InFlight, result.Outcome);
        var fresh = await ReloadAsync(tdb, order.Id);
        Assert.Equal(PayoutStatus.InFlight, fresh.PayoutStatus);
        Assert.Null(fresh.RefundSettledAt);
    }

    [Fact]
    public async Task PayOsBaoTrungKhoa_HieuLaDaCoLenh_KhongTaoLenhMoi()
    {
        using var tdb = new PaymentTestDb();
        var order = await SeedRefundedAsync(tdb);
        var payout = new FakePayout
        {
            OnCreate = _ => PayoutCreateResult.Simple(PayoutCallOutcome.AlreadyExists, "Idempotency key đã tồn tại")
        };

        var result = await NewService(tdb, payout).InitiateRefundPayoutAsync(order.Id, Admin);

        Assert.Equal(RefundPayoutOutcome.InFlight, result.Outcome);
        Assert.Equal(PayoutStatus.InFlight, (await ReloadAsync(tdb, order.Id)).PayoutStatus);
    }

    // ── Chỉ "đã chuyển xong" mới được đóng dấu ──────────────────────────────────────────────────

    [Fact]
    public async Task DangXuLy_KhongDongDauDaHoan()
    {
        using var tdb = new PaymentTestDb();
        var order = await SeedRefundedAsync(tdb);
        var payout = new FakePayout
        {
            OnCreate = _ => new PayoutCreateResult(PayoutCallOutcome.Created,
                new PayoutSnapshot(PayoutState.InFlight, "payout_1", "NGUYEN VAN A", null), null)
        };

        var result = await NewService(tdb, payout).InitiateRefundPayoutAsync(order.Id, Admin);

        Assert.Equal(RefundPayoutOutcome.InFlight, result.Outcome);
        Assert.Null((await ReloadAsync(tdb, order.Id)).RefundSettledAt);
    }

    [Fact]
    public async Task ChuyenXong_TenKhop_DongDauDaHoan()
    {
        using var tdb = new PaymentTestDb();
        var order = await SeedRefundedAsync(tdb, accountName: "NGUYEN VAN A");
        var payout = new FakePayout
        {
            OnCreate = _ => new PayoutCreateResult(PayoutCallOutcome.Created,
                new PayoutSnapshot(PayoutState.Succeeded, "payout_9", "Nguyễn Văn A", null), null)
        };

        var result = await NewService(tdb, payout).InitiateRefundPayoutAsync(order.Id, Admin);

        Assert.Equal(RefundPayoutOutcome.Settled, result.Outcome);
        var fresh = await ReloadAsync(tdb, order.Id);
        Assert.NotNull(fresh.RefundSettledAt);
        Assert.Equal(PayoutStatus.Succeeded, fresh.PayoutStatus);
        Assert.Equal("payout_9", fresh.RefundGatewayRef);
    }

    [Fact]
    public async Task PayOsBaoHong_DanhDauHong_KhongDongDau()
    {
        using var tdb = new PaymentTestDb();
        var order = await SeedRefundedAsync(tdb);
        var payout = new FakePayout
        {
            OnCreate = _ => new PayoutCreateResult(PayoutCallOutcome.Created,
                new PayoutSnapshot(PayoutState.Failed, "payout_x", null, "sai tài khoản"), null)
        };

        var result = await NewService(tdb, payout).InitiateRefundPayoutAsync(order.Id, Admin);

        Assert.Equal(RefundPayoutOutcome.Rejected, result.Outcome);
        var fresh = await ReloadAsync(tdb, order.Id);
        Assert.Equal(PayoutStatus.Failed, fresh.PayoutStatus);
        Assert.Null(fresh.RefundSettledAt);
    }

    // ── Tên người nhận lệch = tiền đã đi nhầm chỗ ───────────────────────────────────────────────

    [Fact]
    public async Task ChuyenXong_TenLech_KHONGDongDauDaHoan()
    {
        using var tdb = new PaymentTestDb();
        var order = await SeedRefundedAsync(tdb, accountName: "NGUYEN VAN A");
        var payout = new FakePayout
        {
            OnCreate = _ => new PayoutCreateResult(PayoutCallOutcome.Created,
                new PayoutSnapshot(PayoutState.Succeeded, "payout_7", "TRAN THI B", null), null)
        };

        var result = await NewService(tdb, payout).InitiateRefundPayoutAsync(order.Id, Admin);

        Assert.Equal(RefundPayoutOutcome.NameMismatch, result.Outcome);
        var fresh = await ReloadAsync(tdb, order.Id);
        Assert.Null(fresh.RefundSettledAt);
        Assert.Contains("không khớp", fresh.PayoutFailureReason);
    }

    [Fact]
    public async Task WebhookKhongCoTen_VanDongDau_VaKhongCoiLaLech()
    {
        // 3/15 giao dịch thật không có tên người trả. Mất bộ dò KHÔNG phải là bằng chứng chuyển nhầm.
        using var tdb = new PaymentTestDb();
        var order = await SeedRefundedAsync(tdb, accountName: "");
        var payout = new FakePayout
        {
            OnCreate = _ => new PayoutCreateResult(PayoutCallOutcome.Created,
                new PayoutSnapshot(PayoutState.Succeeded, "payout_5", "AI DO", null), null)
        };

        var result = await NewService(tdb, payout).InitiateRefundPayoutAsync(order.Id, Admin);

        Assert.Equal(RefundPayoutOutcome.Settled, result.Outcome);
        Assert.NotNull((await ReloadAsync(tdb, order.Id)).RefundSettledAt);
    }

    [Theory]
    [InlineData("NGUYEN VAN A", "Nguyễn Văn A", true)]   // bỏ dấu + hoa/thường
    [InlineData("NGUYEN VAN A", "NGUYENVANA", true)]     // khác cách đặt khoảng trắng
    [InlineData("NGUYEN VAN A", "TRAN THI B", false)]
    public void SoTen_BoDauVaKhoangTrang(string expected, string received, bool match)
        => Assert.Equal(match, RefundService.NamesMatch(expected, received));

    [Theory]
    [InlineData(null, "NGUYEN VAN A")]
    [InlineData("NGUYEN VAN A", "")]
    public void SoTen_ThieuDuLieu_TraNull_KhongPhaiFalse(string? expected, string? received)
        // "Không biết" phải khác "không khớp": trả false ở đây sẽ chặn oan mọi lệnh thiếu tên.
        => Assert.Null(RefundService.NamesMatch(expected, received));

    // ── Đã chuyển rồi thì không chuyển lại ──────────────────────────────────────────────────────

    [Fact]
    public async Task DaDongDauTruocDo_KhongChuyenLanHai()
    {
        using var tdb = new PaymentTestDb();
        var order = await SeedRefundedAsync(tdb, settledAt: DateTime.UtcNow.AddMinutes(-1));
        var payout = new FakePayout();

        var result = await NewService(tdb, payout).InitiateRefundPayoutAsync(order.Id, Admin);

        Assert.Equal(RefundPayoutOutcome.AlreadySettled, result.Outcome);
        Assert.Equal(0, payout.CreateCalls);
    }

    [Fact]
    public async Task DonChuaHoan_KhongChuyenTien()
    {
        using var tdb = new PaymentTestDb();
        var order = await SeedRefundedAsync(tdb);
        await tdb.Db.Orders.Where(o => o.Id == order.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.Status, OrderStatus.Paid));
        var payout = new FakePayout();

        var result = await NewService(tdb, payout).InitiateRefundPayoutAsync(order.Id, Admin);

        Assert.Equal(RefundPayoutOutcome.NotRefunded, result.Outcome);
        Assert.Equal(0, payout.CreateCalls);
    }

    [Fact]
    public async Task LenhTruocDaHong_KhongTuThuLai()
    {
        // Thử lại đòi khoá idempotency mới ⇒ mở lại đúng cửa chuyển-tiền-hai-lần. Để người quyết định.
        using var tdb = new PaymentTestDb();
        var order = await SeedRefundedAsync(tdb);
        await tdb.Db.Orders.Where(o => o.Id == order.Id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(o => o.PayoutStatus, PayoutStatus.Failed)
                .SetProperty(o => o.PayoutIdempotencyKey, Guid.NewGuid()));
        var payout = new FakePayout();

        var result = await NewService(tdb, payout).InitiateRefundPayoutAsync(order.Id, Admin);

        Assert.Equal(RefundPayoutOutcome.Rejected, result.Outcome);
        Assert.Equal(0, payout.CreateCalls);
    }

    // ── Fail-closed: không dựng được đích thì KHÔNG đoán ────────────────────────────────────────

    [Fact]
    public async Task MaNganHang8So_KhongChiTuDong()
    {
        using var tdb = new PaymentTestDb();
        var order = await SeedRefundedAsync(tdb, bankId: EightDigitBankId);
        var payout = new FakePayout();

        var result = await NewService(tdb, payout).InitiateRefundPayoutAsync(order.Id, Admin);

        Assert.Equal(RefundPayoutOutcome.DestinationUnresolved, result.Outcome);
        Assert.Equal(0, payout.CreateCalls);
    }

    [Fact]
    public async Task CoBangAnhXa_ThiChiDuoc()
    {
        using var tdb = new PaymentTestDb();
        var order = await SeedRefundedAsync(tdb, bankId: EightDigitBankId);
        var payout = new FakePayout();
        var options = new RefundPayoutSettings
        {
            Enabled = true,
            BankBinMap = new Dictionary<string, string> { [EightDigitBankId] = RealBin }
        };

        var result = await NewService(tdb, payout, options).InitiateRefundPayoutAsync(order.Id, Admin);

        Assert.Equal(RefundPayoutOutcome.InFlight, result.Outcome);
        Assert.Equal(1, payout.CreateCalls);
    }

    [Fact]
    public async Task WebhookKhongCoSoTaiKhoan_KhongChiTuDong()
    {
        using var tdb = new PaymentTestDb();
        var order = await SeedRefundedAsync(tdb, accountNumber: "");
        var payout = new FakePayout();

        var result = await NewService(tdb, payout).InitiateRefundPayoutAsync(order.Id, Admin);

        Assert.Equal(RefundPayoutOutcome.DestinationUnresolved, result.Outcome);
        Assert.Equal(0, payout.CreateCalls);
    }

    // ── Đổi mã ngân hàng: CITAD → BIN ───────────────────────────────────────────────────────────

    private static BankBinResolver Resolver(params (string key, string bin)[] map) =>
        new(Options.Create(new RefundPayoutSettings
        {
            BankBinMap = map.ToDictionary(x => x.key, x => x.bin)
        }));

    [Theory]
    // Mã CITAD phân biệt tới CHI NHÁNH ([2 số tỉnh][3 số tổ chức][3 số chi nhánh]), nên khớp theo 3 số
    // GIỮA. Khớp cả 8 số sẽ trượt đúng những khách mở tài khoản ở chi nhánh khác.
    [InlineData("01203001", "970436")]   // Vietcombank hội sở Hà Nội
    [InlineData("79203015", "970436")]   // cùng Vietcombank, tỉnh + chi nhánh khác
    [InlineData("01358001", "970423")]   // TPBank
    [InlineData("48358001", "970423")]   // TPBank Đà Nẵng
    public void CitadDoiSangBin_PhuMoiChiNhanh(string citad, string expected)
        => Assert.Equal(expected, Resolver(("203", "970436"), ("358", "970423")).Resolve(citad));

    [Fact]
    public void MaVonDaLaBin_DungThang()
        // payOS trả BIN với ngân hàng này, CITAD với ngân hàng khác — cùng một trường.
        => Assert.Equal("970422", Resolver().Resolve("970422"));

    [Fact]
    public void KhongCoTrongBang_TraNull_KhongDoan()
        => Assert.Null(Resolver(("203", "970436")).Resolve("01999001"));

    [Fact]
    public void KhopNguyenMa_ThangKhopTheo3SoGiua()
    {
        // Cho phép ops ghim một mã cụ thể mà không đụng cả nhóm.
        var r = Resolver(("01203001", "970499"), ("203", "970436"));
        Assert.Equal("970499", r.Resolve("01203001"));
        Assert.Equal("970436", r.Resolve("79203015"));
    }

    [Fact]
    public void DongCauHinhGoSai_KhongBienThanhLenhChuyenMu()
    {
        // Giá trị không phải BIN hợp lệ → trả null (chuyển tay), KHÔNG gửi nguyên chuỗi rác cho payOS.
        Assert.Null(Resolver(("203", "97043")).Resolve("01203001"));      // thiếu số
        Assert.Null(Resolver(("203", "abcdef")).Resolve("01203001"));     // không phải số
        Assert.Null(Resolver(("203", "")).Resolve("01203001"));
    }

    [Fact]
    public void BangMacDinh_PhuCacNganHangPhoBien()
    {
        // Khoá lại bảng đã đối chiếu 2 nguồn (danh sách CITAD ngân hàng công bố + BIN NAPAS).
        // Ai sửa một dòng ở đây là đổi nơi tiền chảy tới, nên nó phải làm test đỏ chứ không lặng lẽ.
        var r = Resolver(("203", "970436"), ("201", "970415"), ("202", "970418"),
                         ("204", "970405"), ("310", "970407"), ("311", "970422"));
        Assert.Equal("970436", r.Resolve("01203001"));   // Vietcombank
        Assert.Equal("970415", r.Resolve("01201001"));   // VietinBank
        Assert.Equal("970418", r.Resolve("01202001"));   // BIDV
        Assert.Equal("970405", r.Resolve("01204001"));   // Agribank
        Assert.Equal("970407", r.Resolve("01310001"));   // Techcombank
        Assert.Equal("970422", r.Resolve("01311001"));   // MBBank
    }

    // ── Phanh: cờ tắt, trần, số dư ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task CoTat_KhongChiDuDaCoDuDuLieu()
    {
        using var tdb = new PaymentTestDb();
        var order = await SeedRefundedAsync(tdb);
        var payout = new FakePayout();

        var result = await NewService(tdb, payout, new RefundPayoutSettings { Enabled = false })
            .InitiateRefundPayoutAsync(order.Id, Admin);

        Assert.Equal(RefundPayoutOutcome.NotEnabled, result.Outcome);
        Assert.Equal(0, payout.CreateCalls);
    }

    [Fact]
    public async Task VuotTran_KhongChiTuDong()
    {
        using var tdb = new PaymentTestDb();
        var order = await SeedRefundedAsync(tdb, amountVnd: 5_000_000);
        var payout = new FakePayout();
        var options = new RefundPayoutSettings { Enabled = true, MaxAutoPayoutVnd = 2_000_000 };

        var result = await NewService(tdb, payout, options).InitiateRefundPayoutAsync(order.Id, Admin);

        Assert.Equal(RefundPayoutOutcome.OverCeiling, result.Outcome);
        Assert.Equal(0, payout.CreateCalls);
    }

    [Fact]
    public async Task ViChiKhongDuSoDu_DungTruocKhiGoi()
    {
        using var tdb = new PaymentTestDb();
        var order = await SeedRefundedAsync(tdb, amountVnd: 100_000);
        var payout = new FakePayout { Balance = 50_000 };

        var result = await NewService(tdb, payout).InitiateRefundPayoutAsync(order.Id, Admin);

        Assert.Equal(RefundPayoutOutcome.InsufficientBalance, result.Outcome);
        Assert.Equal(0, payout.CreateCalls);
    }

    [Fact]
    public async Task KhongDocDuocSoDu_VanChi_KhongCoiLaHetTien()
    {
        // "Không biết" khác "bằng 0". Chặn ở đây sẽ làm mọi lệnh hoàn chết mỗi khi API số dư trục trặc.
        using var tdb = new PaymentTestDb();
        var order = await SeedRefundedAsync(tdb);
        var payout = new FakePayout { Balance = null };

        var result = await NewService(tdb, payout).InitiateRefundPayoutAsync(order.Id, Admin);

        Assert.Equal(RefundPayoutOutcome.InFlight, result.Outcome);
        Assert.Equal(1, payout.CreateCalls);
    }

    // ── Phân loại lỗi payOS: chỉ "chắc chắn chưa đi" mới được coi là từ chối ────────────────────

    [Fact]
    public void ChiTuChoiKhiCHUNGMINHDuocLaChuaVao()
    {
        // Đúng nhóm này mới an toàn coi là "tiền chưa đi": payOS chặn trước khi xử lý.
        Assert.True(PayoutClient.IsDefinitiveRejection(new PayOS.Exceptions.BadRequestException()));
        Assert.True(PayoutClient.IsDefinitiveRejection(new PayOS.Exceptions.UnauthorizedException()));
        Assert.True(PayoutClient.IsDefinitiveRejection(new PayOS.Exceptions.ForbiddenException()));

        // Còn lại là "không biết". Xếp nhầm chúng vào từ chối sẽ mở đường tạo lệnh mới ⇒ chuyển hai lần.
        Assert.False(PayoutClient.IsDefinitiveRejection(new PayOS.Exceptions.ConnectionTimeoutException()));
        Assert.False(PayoutClient.IsDefinitiveRejection(new PayOS.Exceptions.ConnectionException()));
        Assert.False(PayoutClient.IsDefinitiveRejection(new PayOS.Exceptions.InternalServerErrorException(500)));
        Assert.False(PayoutClient.IsDefinitiveRejection(new PayOS.Exceptions.TooManyRequestsException()));
        Assert.False(PayoutClient.IsDefinitiveRejection(new TimeoutException()));
    }

    [Fact]
    public void NhanDienTrungKhoa_KhongKhop_ThiKhongDuocCoiLaTrung()
    {
        Assert.True(PayoutClient.IsDuplicateKey(
            new PayOS.Exceptions.ApiException(400, "xxx", "Idempotency key đã tồn tại")));
        // Không nhận ra → rơi xuống nhánh "không biết" (an toàn), KHÔNG được nhận bừa.
        Assert.False(PayoutClient.IsDuplicateKey(
            new PayOS.Exceptions.ApiException(400, "xxx", "Số tài khoản không hợp lệ")));
    }

    // ── Đường poll của reconciler ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Poll_PayOsBaoXong_DongDauDaHoan()
    {
        using var tdb = new PaymentTestDb();
        var order = await SeedRefundedAsync(tdb, accountName: "NGUYEN VAN A");
        var payout = new FakePayout
        {
            OnCreate = _ => new PayoutCreateResult(PayoutCallOutcome.Created,
                new PayoutSnapshot(PayoutState.InFlight, "payout_3", null, null), null),
            OnGet = id => new PayoutSnapshot(PayoutState.Succeeded, id, "NGUYEN VAN A", null),
        };
        var svc = NewService(tdb, payout);

        await svc.InitiateRefundPayoutAsync(order.Id, Admin);
        var result = await svc.PollRefundPayoutAsync(order.Id);

        Assert.Equal(RefundPayoutOutcome.Settled, result.Outcome);
        Assert.NotNull((await ReloadAsync(tdb, order.Id)).RefundSettledAt);
    }

    [Fact]
    public async Task Poll_KhongTraDuocTrangThai_GiuNguyenDangBay()
    {
        using var tdb = new PaymentTestDb();
        var order = await SeedRefundedAsync(tdb);
        var payout = new FakePayout
        {
            OnCreate = _ => new PayoutCreateResult(PayoutCallOutcome.Created,
                new PayoutSnapshot(PayoutState.InFlight, "payout_4", null, null), null),
            OnGet = _ => null,   // payOS không trả lời
        };
        var svc = NewService(tdb, payout);

        await svc.InitiateRefundPayoutAsync(order.Id, Admin);
        var result = await svc.PollRefundPayoutAsync(order.Id);

        Assert.Equal(RefundPayoutOutcome.InFlight, result.Outcome);
        var fresh = await ReloadAsync(tdb, order.Id);
        Assert.Equal(PayoutStatus.InFlight, fresh.PayoutStatus);
        Assert.Null(fresh.RefundSettledAt);
    }
}
