using Isas.PaymentService.Models;
using Isas.PaymentService.Services;
using Isas.Shared.Extensions;
using Isas.Shared.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi;
using PaymentService.Models;
using PayOS;
using Scalar.AspNetCore;
using System.Security.Claims;
using System.Text;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddServiceCors(builder.Configuration);
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddHealthChecks();
builder.Services.AddOpenApi(options =>
{
    options.AddDocumentTransformer((document, context, ct) =>
    {
        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();

        document.Components.SecuritySchemes["Bearer"] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "JWT",
            Description = "Enter your JWT access token"
        };

        return Task.CompletedTask;
    });
});

builder.Services.AddControllers().AddJsonOptions(options =>
{
    options.JsonSerializerOptions.ReferenceHandler = ReferenceHandler.IgnoreCycles;
    options.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.Never;
    // Chuẩn hoá DateTime nhận vào về UTC (KHÔNG đụng enum — Payment giữ enum SỐ theo hợp đồng FE).
    // Reachable: POST /admin/invoices/close nhận periodStart/periodEnd → ghi invoices (timestamptz);
    // offset SỐ (+00:00) bị parse thành Kind=Local → Npgsql từ chối → 500. Xem UtcDateTimeConverter.
    options.JsonSerializerOptions.Converters.Add(new UtcDateTimeConverter());
});

builder.Services.AddHttpContextAccessor();

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.MapInboundClaims = false;

        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            // A5 — role claim khớp AuthService (JwtService phát ClaimTypes.Role) để [Authorize(Roles)]
            // enforce tất định, không phụ thuộc default của thư viện (MapInboundClaims=false ở trên).
            RoleClaimType = ClaimTypes.Role,
            ValidIssuer = builder.Configuration["Jwt:Issuer"],
            ValidAudience = builder.Configuration["Jwt:Audience"],
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(builder.Configuration["Jwt:Key"]))
        };
    });
builder.Services.AddAuthorization();

builder.Services.AddDbContext<PaymentDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection"))
        .UseSnakeCaseNamingConvention());

builder.Services.Configure<PayOSSettings>(
    builder.Configuration.GetSection("PayOS"));

// P8b — đơn giá 1 lượt (config Billing:UnitPrice) để lập hóa đơn postpaid cuối kỳ.
builder.Services.Configure<BillingSettings>(
    builder.Configuration.GetSection("Billing"));

// F22 — bảng giá token (USD/1 triệu). Giá sống ở Payment chứ không ở AIService: AIService biết SỐ TOKEN,
// Payment biết TIỀN. Mỗi dòng usage snapshot lại đơn giá đã dùng nên đổi giá không hồi tố số liệu cũ.
builder.Services.Configure<AiPricingSettings>(
    builder.Configuration.GetSection("AiPricing"));
builder.Services.Configure<HttpTrafficRetentionSettings>(
    builder.Configuration.GetSection(HttpTrafficRetentionSettings.SectionName));
builder.Services.Configure<TieringSettings>(
    builder.Configuration.GetSection(TieringSettings.SectionName));

// DB4 — cấu hình reconciler credit_accounts.reserved_credits ↔ count(reservations Reserved).
builder.Services.Configure<ReconcileSettings>(
    builder.Configuration.GetSection("Reconcile"));

builder.Services.Configure<InvoiceOverdueSettings>
    (builder.Configuration.GetSection("InvoiceOverdue"));

// DB18 — cấu hình reconciler bù trừ orphan reservation (session không bao giờ được insert lúc Start).
builder.Services.Configure<OrphanReconcileSettings>(
    builder.Configuration.GetSection(OrphanReconcileSettings.SectionName));

// PAY-10 — cấu hình sweeper đóng đơn Pending quá hạn sang Expired (đối soát PayOS trước khi đóng).
builder.Services.Configure<OrderExpirySettings>(
    builder.Configuration.GetSection(OrderExpirySettings.SectionName));

// DB18 — chiều gọi nội bộ Payment→Interview `/internal/sessions/exists` (X-Internal-Token, KHÔNG qua
// gateway). BaseUrl từ Interview:BaseUrl; trống → không set BaseAddress (call sẽ ném → reconciler skip vòng).
builder.Services.AddHttpClient<IInterviewSessionClient, InterviewSessionClient>(c =>
{
    var baseUrl = builder.Configuration["Interview:BaseUrl"];
    if (!string.IsNullOrWhiteSpace(baseUrl))
        c.BaseAddress = new Uri(baseUrl);
    c.Timeout = TimeSpan.FromSeconds(10);   // exists = query DB nhanh, không phải LLM
});

builder.Services.AddSingleton<PayOSClient>(sp =>
{
    var settings = sp.GetRequiredService<IOptions<PayOSSettings>>().Value;
    return new PayOSClient(new PayOSOptions
    {
        ClientId = settings.ClientId,
        ApiKey = settings.ApiKey,
        ChecksumKey = settings.ChecksumKey,
    });
});

// Cấu hình chi tiền hoàn tự động (kênh CHI payOS). Mặc định TẮT — xem RefundPayoutSettings.
builder.Services.Configure<RefundPayoutSettings>(
    builder.Configuration.GetSection(RefundPayoutSettings.SectionName));
builder.Services.Configure<PayoutChannelSettings>(
    builder.Configuration.GetSection(PayoutChannelSettings.SectionName));

// Client thứ hai, credential RIÊNG của kênh chi. Không dùng chung với kênh thu: đã kiểm chứng bằng lệnh
// gọi thật — API key kênh thu gọi API chi trả code 601 "API key không tồn tại".
builder.Services.AddKeyedSingleton<PayOSClient>(PayoutChannelSettings.SectionName, (sp, _) =>
{
    var channel = sp.GetRequiredService<IOptions<PayoutChannelSettings>>().Value;
    return new PayOSClient(new PayOSOptions
    {
        ClientId = channel.ClientId,
        ApiKey = channel.ApiKey,
        ChecksumKey = channel.ChecksumKey,
    });
});

builder.Services.AddScoped<IOrderService, OrderService>();
builder.Services.AddScoped<IPackageService, PackageService>();
// P8b: hóa đơn postpaid — chốt kỳ → tất toán (reuse OrderService/PayOS) → settle qua webhook (branch Kind).
builder.Services.AddScoped<IInvoiceService, InvoiceService>();
builder.Services.AddScoped<ISubscriptionService, SubscriptionService>();
builder.Services.AddScoped<PlanService>();
builder.Services.AddScoped<EntitlementResolver>();
// F18: hoàn tiền — đơn Paid→Refunded + bút toán đảo gắn bút toán mua gốc + thu hồi credit (kẹp trần).
builder.Services.AddScoped<IRefundService, RefundService>();
// Chi tiền hoàn qua kênh chi payOS: client bọc SDK (mockable) + đổi mã ngân hàng webhook sang BIN.
builder.Services.AddScoped<IPayoutClient, PayoutClient>();
builder.Services.AddScoped<IBankBinResolver, BankBinResolver>();
// F19: tổng hợp doanh thu theo kỳ cho PlatformAdmin (đọc `orders`, không đụng sổ cái credit).
builder.Services.AddScoped<IRevenueService, RevenueService>();
// F22: nhận số liệu token AIService đẩy về (GEN-4 — AIService không ghi DB) + tổng hợp chi phí cho admin.
builder.Services.AddScoped<IAiUsageService, AiUsageService>();
// F20 (vế Payment): admin cấp credit khuyến mãi — ví tăng + bút toán PromoGrant ghi rõ người cấp.
builder.Services.AddScoped<IAdminCreditService, AdminCreditService>();
// P1: cấp phát credit_accounts (owner_type). Reserve/Consume/Release + webhook (P2/P4/P5/P6) = task sau.
builder.Services.AddScoped<ICreditAccountService, CreditAccountService>();
// P7: sinh order_code time+random, unique + retry (dùng trong P2 CreateOrderAsync).
builder.Services.AddScoped<IOrderCodeGenerator, OrderCodeGenerator>();
// P2: xử lý webhook PayOS đã verify → cộng credit idempotent theo payos_order_code (PAY-8).
builder.Services.AddScoped<IWebhookService, WebhookService>();
// P3: active-polling đối soát. PayOsQueryClient bọc SDK getPaymentLinkInformation (mockable);
// OrderStatusService reuse WebhookService để cộng credit khi PayOS Paid (một đường cộng credit).
builder.Services.AddScoped<IPayOsQueryClient, PayOsQueryClient>();
builder.Services.AddScoped<IOrderStatusService, OrderStatusService>();
// E7: Payment phản ứng event Interview — consume (SessionScored) / release (SessionAbandoned).
// Handler scoped (dùng DbContext qua CreditAccountService); consumer là BackgroundService bind
// queue payment.credit ↔ exchange interview.events (E2/E3).
builder.Services.AddScoped<ICreditEventHandler, CreditEventHandler>();
builder.Services.AddHostedService<InterviewEventConsumer>();
// DB4: đối soát định kỳ credit_accounts.reserved_credits == count(reservations status=Reserved) cho
// cùng owner → sửa drift (crash giữa reserve/consume/release, bút toán lệch). Core Payment-DB thuần.
builder.Services.AddHostedService<CreditReservationReconciler>();
builder.Services.AddHostedService<SubscriptionMeterReconciler>();
// T9/A3: paid subscription orders without a created entitlement are money-ambiguous. Log for manual
// reconciliation only; never auto-grant or auto-refund (PAY-10).
builder.Services.AddHostedService<SubscriptionSettlementReconciler>();
// DB18 (DB4b): release reservation Reserved mà session Interview KHÔNG BAO GIỜ được tạo (crash giữa
// reserve↔insert lúc Start). Xác minh dương qua Interview `/internal/sessions/exists`; Interview down →
// skip vòng (KHÔNG release oan). Compensation-reconciler nhẹ (không saga).
builder.Services.AddHostedService<OrphanReservationReconciler>();
// PAY-10: đóng đơn Pending quá hạn sang Expired — trước đó KHÔNG sweeper nào gán Expired (e2e 2026-07-18:
// 16/16 đơn Pending quá hạn, 0 đơn từng Expired). Hỏi PayOS trước: Paid → cộng credit (cứu webhook rơi),
// Underpaid/PayOS-lỗi → giữ Pending; chỉ link chết mới đóng (không đóng mù → không chôn tiền đã trả).
builder.Services.AddHostedService<OrderExpiryReconciler>();
// F8: đóng dấu Active→Expired cho kỳ hạn thuê bao quá hạn. THUẦN báo cáo — luật vào bài tự so ngày
// (ISubscriptionService.HasActiveAsync), nên job này chết cũng KHÔNG cho ai thi miễn phí.
builder.Services.AddHostedService<SubscriptionExpiryReconciler>();

// Theo tiếp lệnh chi hoàn tiền đang bay tới khi có kết luận (chuyển khoản liên ngân hàng không xong
// trong một nhịp HTTP), đồng thời là lưới cứu ca timeout: gọi lại bằng ĐÚNG khoá idempotency đã ghi.
builder.Services.AddHostedService<RefundPayoutReconciler>();
builder.Services.AddHostedService<InvoiceOverdueReconciler>();
builder.Services.AddHostedService<HttpTrafficPurger>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference(options =>
    {
        options.Title = "Payment API";
        options.WithDefaultHttpClient(ScalarTarget.JavaScript, ScalarClient.Fetch);
    });
}

app.UseServiceCors();
app.UseAuthentication();
app.UseAuthorization();

app.MapHealthChecks("/health");

app.MapControllers();

app.Run();
