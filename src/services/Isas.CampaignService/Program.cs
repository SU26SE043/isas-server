using Amazon.Runtime;
using Amazon.S3;
using Isas.CampaignService.Models;
using Isas.CampaignService.Services;
using Isas.Shared.Extensions;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi;
using Scalar.AspNetCore;
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
builder.Services.AddScoped<IParserService, ParserService>();
// C8: gọi AIService đề xuất tiêu chí (đồng bộ qua AiService:BaseUrl; có fallback)
builder.Services.AddHttpClient<ICriteriaSuggester, AiServiceCriteriaSuggester>(c =>
    c.BaseAddress = new Uri(builder.Configuration["AiService:BaseUrl"] ?? "http://localhost:8000"));
// D1: đẩy job email mời (magic-link) vào RabbitMQ (cùng pattern InterviewService.ScoringJobPublisher)
builder.Services.AddSingleton<IInvitationEmailPublisher, InvitationEmailPublisher>();
// C14: sàng CV async — đẩy job AI chấm khớp (cv_screening_queue) + xử lý callback/shortlist/PATCH
builder.Services.AddSingleton<ICvScreeningPublisher, CvScreeningPublisher>();
builder.Services.AddScoped<ICvScreeningService, CvScreeningService>();
// C15: quét CV sàng kẹt mỗi 2' → đẩy lại cv_screening_queue (publish hụt / worker mất tích)
builder.Services.AddHostedService<StuckScreeningRepublisher>();
// E4: nghe event SessionScored (RabbitMQ) → upsert campaign_rankings (ranking read-model, D10)
builder.Services.AddScoped<IRankingEventHandler, RankingEventHandler>();
builder.Services.AddHostedService<SessionScoredConsumer>();

builder.Services.AddControllers().AddJsonOptions(options =>
{
    options.JsonSerializerOptions.ReferenceHandler = ReferenceHandler.IgnoreCycles;
    options.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.Never;
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
        UseHttp = true
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