using Amazon.Runtime;
using Amazon.S3;
using Isas.CampaignService.Models;
using Isas.CampaignService.Services;
using Isas.Shared.Extensions;
using Isas.Shared.Files;
using Isas.Shared.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi;
using Scalar.AspNetCore;
using System.Security.Claims;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using System.Globalization;

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

builder.Services.AddScoped<ICampaignService, CampaignService>();
builder.Services.AddMemoryCache();
builder.Services.AddScoped<IFileService, FileService>();
builder.Services.AddSingleton<IPdfTextExtractor, PdfTextExtractor>();   // DB17: shared PDF extractor
builder.Services.AddScoped<IParserService, ParserService>();
// C8: gọi AIService đề xuất tiêu chí (đồng bộ qua AiService:BaseUrl; có fallback)
builder.Services.AddHttpClient<ICriteriaSuggester, AiServiceCriteriaSuggester>(c =>
    c.BaseAddress = new Uri(builder.Configuration["AiService:BaseUrl"] ?? "http://localhost:8000"));
// F9: gọi AIService sinh câu hỏi từ JD cho campaign B2B (đồng bộ; lỗi → 502 ném lên controller)
builder.Services.AddHttpClient<IQuestionGenerator, AiServiceQuestionGenerator>(c =>
    c.BaseAddress = new Uri(builder.Configuration["AiService:BaseUrl"] ?? "http://localhost:8000"));
// SEC-2: gọi AIService so khớp khuôn mặt (đồng bộ qua AiService:BaseUrl; lỗi → 502 ném lên controller)
builder.Services.AddHttpClient<IAiServiceFaceVerifyClient, AiServiceFaceVerifyClient>(c =>
    c.BaseAddress = new Uri(builder.Configuration["AiService:BaseUrl"] ?? "http://localhost:8000"));
// D1: đẩy job email mời (magic-link) vào RabbitMQ (cùng pattern InterviewService.ScoringJobPublisher)
builder.Services.AddSingleton<IInvitationEmailPublisher, InvitationEmailPublisher>();
// D1 (consumer): tiêu thụ campaign_invitation_email_queue → gửi email mời qua SMTP.
builder.Services.AddScoped<ICampaignEmailSender, CampaignEmailSender>();
builder.Services.AddHostedService<InvitationEmailConsumer>();
// DB2b: Transactional Outbox — dispatcher quét outbox_messages → publish invitation-email at-least-once.
builder.Services.Configure<OutboxSettings>(builder.Configuration.GetSection(OutboxSettings.SectionName));
// DB23: hạn mặc định token magic-link khi campaign không có deadline (không để token sống vĩnh viễn).
builder.Services.Configure<InvitationSettings>(builder.Configuration.GetSection(InvitationSettings.SectionName));
builder.Services.Configure<TieringSettings>(builder.Configuration.GetSection(TieringSettings.SectionName));
builder.Services.AddHostedService<OutboxDispatcher>();
// DB28: retention — dọn outbox-row ĐÃ publish quá hạn giữ (bảng vốn phình vô hạn). Chỉ đụng row
// published_at IS NOT NULL + quá hạn, có trần mỗi vòng; tắt bằng `Outbox:PurgeEnabled=false`.
builder.Services.AddHostedService<OutboxPurger>();
// BK25/DATA-3: retention ảnh sinh trắc — xoá object SeaweedFS + dòng face_images quá hạn giữ
// (CAMP-13: 90 ngày). 🔴 MẶC ĐỊNH TẮT (thứ bị xoá là bằng chứng buổi thi, không phải rác thuần như
// outbox) → bật bằng `FaceImageRetention__Enabled=true` sau khi quan sát 1 chu kỳ.
builder.Services.Configure<FaceImageRetentionSettings>(
    builder.Configuration.GetSection(FaceImageRetentionSettings.SectionName));
builder.Services.AddHostedService<FaceImagePurger>();
// C14: sàng CV async — đẩy job AI chấm khớp (cv_screening_queue) + xử lý callback/shortlist/PATCH
builder.Services.AddSingleton<ICvScreeningPublisher, CvScreeningPublisher>();
builder.Services.AddScoped<ICvScreeningService, CvScreeningService>();
// C15: quét CV sàng kẹt mỗi 2' → đẩy lại cv_screening_queue (publish hụt / worker mất tích)
builder.Services.AddHostedService<StuckScreeningRepublisher>();
// E4: nghe event SessionScored (RabbitMQ) → upsert campaign_rankings (ranking read-model, D10)
builder.Services.AddScoped<IRankingEventHandler, RankingEventHandler>();
builder.Services.AddHostedService<SessionScoredConsumer>();

// D2: orchestrator luồng ứng viên (invitation→join→membership→my-campaigns→start).
// 2 typed HttpClient nội bộ (X-Internal-Token gắn trong client, KHÔNG qua gateway): Auth provision + Interview session.
builder.Services.AddHttpClient<IAuthProvisionClient, AuthProvisionClient>(c =>
    c.BaseAddress = new Uri(
        string.IsNullOrWhiteSpace(builder.Configuration["Auth:BaseUrl"])
            ? "http://localhost:5001" : builder.Configuration["Auth:BaseUrl"]!));
builder.Services.AddHttpClient<ICampaignSessionClient, CampaignSessionClient>(c =>
    c.BaseAddress = new Uri(
        string.IsNullOrWhiteSpace(builder.Configuration["Interview:BaseUrl"])
            ? "http://localhost:5002" : builder.Configuration["Interview:BaseUrl"]!));
builder.Services.AddHttpClient<IEntitlementClient, EntitlementClient>(c =>
{
    c.BaseAddress = new Uri(string.IsNullOrWhiteSpace(builder.Configuration["Payment:BaseUrl"])
        ? "http://localhost:5004" : builder.Configuration["Payment:BaseUrl"]!);
    c.Timeout = TimeSpan.FromSeconds(5);
});
builder.Services.AddScoped<IParticipationService, ParticipationService>();

builder.Services.AddControllers().AddJsonOptions(options =>
{
    options.JsonSerializerOptions.ReferenceHandler = ReferenceHandler.IgnoreCycles;
    options.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.Never;
    // BK20: enum-as-string như Interview — questions[].source nhận "CustomHr" (không chỉ 0/1).
    options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
    // Chuẩn hoá DateTime nhận vào về UTC: chuỗi có offset SỐ (+00:00) bị parse thành Kind=Local,
    // Npgsql từ chối ghi vào timestamptz → 500. Xem UtcDateTimeConverter.
    options.JsonSerializerOptions.Converters.Add(new UtcDateTimeConverter());
});

builder.Services.AddHttpContextAccessor();

// F17: vòng đời API key bên thứ ba (tạo/liệt kê/thu hồi) + xác thực key cho Public API.
builder.Services.Configure<ApiKeySettings>(builder.Configuration.GetSection(ApiKeySettings.SectionName));
builder.Services.AddScoped<IApiKeyService, ApiKeyService>();

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    // F17 — scheme "ApiKey" ĐỨNG RIÊNG cạnh Bearer. Mặc định vẫn là Bearer nên API key KHÔNG mở
    // được endpoint JWT nào; ngược lại Public API khai tường minh AuthenticationSchemes=ApiKey nên
    // JWT không mở được nó. Ranh giới là cấu trúc, không phải kỷ luật viết code.
    .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(
        ApiKeyDefaults.Scheme, _ => { })
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

// F17 — rate-limit Public API, phân vùng theo API KEY ID.
//
// VÌ SAO cần: không có nó thì một key rò rỉ = rút toàn bộ dữ liệu ứng viên của org ở tốc độ tối đa,
// và org không có cửa sổ nào để kịp phát hiện + revoke. Rate-limit không ngăn được rò rỉ nhưng biến
// "rút sạch trong vài giây" thành "mất nhiều giờ", đủ để last_used_at + log lộ ra bất thường.
//
// VÌ SAO phân vùng theo key id chứ không theo header thô: partition theo header thì kẻ tấn công gửi
// key ngẫu nhiên mỗi request sẽ đẻ partition vô hạn trong bộ nhớ = DoS đổi chiều. Sau khi
// UseAuthentication() chạy, request KHÔNG hợp lệ đã bị 401 và ta chỉ phân vùng cho key THẬT
// (số lượng bị chặn bởi MaxActiveKeysPerOrg); phần còn lại dồn vào 1 partition "anonymous" chung.
//
// ⚠ GIỚI HẠN đã biết: limiter này IN-PROCESS → chạy N replica thì trần thực tế là N×. Deploy hiện
// tại là single-instance (cùng lý do DB7 leader-election được hoãn). Muốn đúng khi scale ngang thì
// cần backend chia sẻ (Redis) — ngoài phạm vi F17, đã ghi vào docs/services/campaign.md §F17.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    // R2 — 429 trước đây không kèm Retry-After/X-RateLimit-Remaining, client ATS không biết chờ
    // bao lâu để retry đúng cách (dễ retry-storm ngay lập tức, càng làm nặng thêm chỗ đang nghẽn).
    options.OnRejected = async (ctx, ct) =>
    {
        var settings = ctx.HttpContext.RequestServices
            .GetRequiredService<IOptions<ApiKeySettings>>().Value;

        var retryAfterSeconds = Math.Max(1, settings.RateLimitWindowSeconds);
        if (ctx.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
            retryAfterSeconds = (int)Math.Ceiling(retryAfter.TotalSeconds);

        ctx.HttpContext.Response.Headers.RetryAfter =
            retryAfterSeconds.ToString(CultureInfo.InvariantCulture);
        ctx.HttpContext.Response.Headers["X-RateLimit-Remaining"] = "0";
    };

    options.AddPolicy(ApiKeyDefaults.RateLimitPolicy, httpContext =>
    {
        var settings = httpContext.RequestServices
            .GetRequiredService<IOptions<ApiKeySettings>>().Value;

        // Trần ≤ 0 = tắt rate-limit (kill-switch vận hành, mẫu Billing:CvAnalysisCredits=0).
        // Lưu ý: đây CHỈ tắt trần per-key; bucket anonymous có kill-switch riêng qua giá trị của nó
        // (Resolve() đã kẹp tối thiểu 1 nên không tắt được — cố ý, xem ApiKeySettings).
        if (settings.RateLimitPermitsPerWindow <= 0)
            return RateLimitPartition.GetNoLimiter("disabled");

        // R2 — TRƯỚC: đọc claim trực tiếp ở đây và fallback "anonymous" khi thiếu claim — ĐÚNG Ý
        // TƯỞNG nhưng SAI THỰC TẾ, vì tại middleware này chạy (UseRateLimiter, tức TRƯỚC
        // UseAuthorization) claim api_key_id CHƯA TỪNG tồn tại cho bất kỳ request nào (xem middleware
        // pre-authenticate mới thêm ở app.Use(...) phía trên UseRateLimiter). Nay claim đã có sẵn khi
        // key hợp lệ → Resolve() phân vùng đúng theo key; ngược lại rơi về "anonymous" (bucket RIÊNG,
        // CHẶT hơn — không còn dùng chung 1 trần 60 với key thật).
        var decision = ApiKeyRateLimit.Resolve(httpContext.User, settings);
        return RateLimitPartition.GetFixedWindowLimiter(decision.PartitionKey, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = decision.PermitLimit,
            Window = TimeSpan.FromSeconds(Math.Max(1, settings.RateLimitWindowSeconds)),
            QueueLimit = 0,   // vượt trần → 429 ngay, không xếp hàng giữ kết nối
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst
        });
    });
});

// DB25 — retry transient (blip mạng / deadlock Postgres) thay vì để nổi lên thành 500.
// AN TOÀN Ở ĐÂY vì CampaignService KHÔNG có site `BeginTransactionAsync` nào: khi bật
// EnableRetryOnFailure, transaction do người dùng tự mở sẽ ném InvalidOperationException
// ("execution strategy does not support user-initiated transactions") trừ khi bọc trong
// CreateExecutionStrategy(). Interview (1 site) + Payment (5 site) vì thế CHƯA bật — xem
// ghi chú DB25 trong docs/tasks.md.
builder.Services.AddDbContext<CampaignDbContext>(options =>
    options.UseNpgsql(
            builder.Configuration.GetConnectionString("DefaultConnection"),
            npgsql => npgsql.EnableRetryOnFailure())
        .UseSnakeCaseNamingConvention());

builder.Services.Configure<FileStorageOptions>(
    builder.Configuration.GetSection("SeaweedFS"));

builder.Services.AddSingleton<IAmazonS3>(sp => {

    var opts = sp.GetRequiredService<IOptions<FileStorageOptions>>().Value;

    return new AmazonS3Client(
    opts.AccessKey,
    opts.SecretKey,
    new AmazonS3Config
    {
        ServiceURL = opts.ServiceURL,
        ForcePathStyle = opts.ForcePathStyle,
        AuthenticationRegion = "us-east-1",
        UseHttp = true,
        // SeaweedFS: SDK v4 default checksum (CRC32 + streaming trailer) → "signature does not match".
        RequestChecksumCalculation = Amazon.Runtime.RequestChecksumCalculation.WHEN_REQUIRED,
        ResponseChecksumValidation = Amazon.Runtime.ResponseChecksumValidation.WHEN_REQUIRED
    });
});

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference(options =>
    {
        options.Title = "Campaign API";
        options.WithDefaultHttpClient(ScalarTarget.JavaScript, ScalarClient.Fetch);
    });
}

app.UseServiceCors();
app.UseAuthentication();

// R2 — SỬA F17: scheme mặc định của AddAuthentication() là JwtBearer (dòng khai báo phía trên),
// nên UseAuthentication() KHÔNG chạy handler "ApiKey" — handler đó chỉ chạy khi có
// [Authorize(AuthenticationSchemes = ApiKeyDefaults.Scheme)], tức là BÊN TRONG UseAuthorization(),
// tức là SAU UseRateLimiter(). Hệ quả trước khi sửa: mọi request tới /campaign/public — kể cả với
// X-Api-Key hợp lệ — đều CHƯA có claim api_key_id lúc limiter chạy → toàn bộ rơi vào bucket
// "anonymous" DÙNG CHUNG. ~60 request vô danh (hoặc key sai) trong 60s là khoá luôn mọi org khác.
//
// Sửa: xác thực scheme ApiKey TƯỜNG MINH ở đây, chỉ cho path Public API, TRƯỚC UseRateLimiter().
// AuthenticateAsync gọi lại ở UseAuthorization() phía dưới dùng lại AuthenticateResult đã cache
// (AuthenticationHandler.HandleAuthenticateOnceAsync) — không tốn thêm lượt tra DB/hash.
app.Use(async (ctx, next) =>
{
    if (ctx.Request.Path.StartsWithSegments("/campaign/public") && ctx.User.Identity?.IsAuthenticated != true)
    {
        var result = await ctx.AuthenticateAsync(ApiKeyDefaults.Scheme);
        if (result.Succeeded && result.Principal is not null)
            ctx.User = result.Principal;
        // Không hợp lệ/thiếu header → giữ nguyên User rỗng → Resolve() rơi về "anonymous" (bucket
        // chặt hơn) → UseAuthorization() phía dưới sẽ tự 401 như cũ, KHÔNG đổi hành vi auth.
    }
    await next();
});

app.UseRateLimiter();
app.UseAuthorization();

app.MapHealthChecks("/health");

app.MapControllers();

app.Run();
