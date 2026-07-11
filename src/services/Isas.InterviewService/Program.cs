using Amazon.S3;
using Isas.InterviewService.Services;
using Isas.Shared.Extensions;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi;
using Scalar.AspNetCore;
using System.Text;
using System.Text.Json.Serialization;
using Isas.InterviewService.ApplicationDbContext;
using Isas.InterviewService.Models;
using Isas.InterviewService.Services.Interfaces;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddServiceCors(builder.Configuration);
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddHealthChecks();

builder.Services.AddScoped<ICVParserService, CVParserService>();
builder.Services.AddScoped<IStorageService, StorageService>();
builder.Services.AddScoped<IAnswerService, AnswerService>();
builder.Services.AddHostedService<BucketInitializer>();
builder.Services.AddSingleton<IScoringJobPublisher, ScoringJobPublisher>();
builder.Services.AddSingleton<ISessionEventPublisher, SessionEventPublisher>();
builder.Services.AddScoped<ISessionScoringNotifier, SessionScoringNotifier>();
builder.Services.AddScoped<IPracticeService, PracticeService>();

builder.Services.AddHttpClient<IAiServiceQuestionGenerator,AiServiceQuestionGenerator>(c =>
{
    c.BaseAddress = new Uri(builder.Configuration["AiService:BaseUrl"]!);
    c.Timeout = TimeSpan.FromSeconds(60);  // LLM có thể chậm
});

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
    options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()); 
});


builder.Services.AddHttpContextAccessor();
builder.Services.AddHostedService<StuckAnswerRepublisher>();
builder.Services.AddHostedService<SessionAbandonSweeper>();


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
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(builder.Configuration["Jwt:Key"]!))
        };
    });
builder.Services.AddAuthorization();

builder.Services.AddDbContext<InterviewDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection"))
        .UseSnakeCaseNamingConvention());

builder.Services.Configure<FileStorageOptions>(
    builder.Configuration.GetSection(FileStorageOptions.SectionName));

builder.Services.AddSingleton<IAmazonS3>(sp =>
{
    var opts = sp.GetRequiredService<IOptions<FileStorageOptions>>().Value;

    return new AmazonS3Client(
        opts.AccessKey,
        opts.SecretKey,
        new AmazonS3Config
        {
            ServiceURL = opts.ServiceURL,
            ForcePathStyle = opts.ForcePathStyle,
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
        options.Title = "Interview API";
        options.WithDefaultHttpClient(ScalarTarget.JavaScript, ScalarClient.Fetch);
    });
}


app.UseAuthentication();
app.UseAuthorization();

app.MapHealthChecks("/health");

app.MapControllers();

app.Run();