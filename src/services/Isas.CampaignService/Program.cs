using Amazon.Runtime;
using Amazon.S3;
using Isas.CampaignService.Models;
using Isas.CampaignService.Services;
using Isas.Shared.Extensions;
using Isas.Shared.Files;
using Isas.Shared.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi;
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

builder.Services.AddScoped<ICampaignService, CampaignService>();
builder.Services.AddScoped<IFileService, FileService>();
builder.Services.AddSingleton<IPdfTextExtractor, PdfTextExtractor>();   // DB17: shared PDF extractor
builder.Services.AddScoped<IParserService, ParserService>();
// C8: gọi AIService đề xuất tiêu chí (đồng bộ qua AiService:BaseUrl; có fallback)
builder.Services.AddHttpClient<ICriteriaSuggester, AiServiceCriteriaSuggester>(c =>
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
builder.Services.AddHostedService<OutboxDispatcher>();
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

builder.Services.AddDbContext<CampaignDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection"))
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
app.UseAuthorization();

app.MapHealthChecks("/health");

app.MapControllers();

app.Run();