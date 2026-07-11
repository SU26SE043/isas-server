using Isas.PaymentService.Models;
using Isas.PaymentService.Services;
using Isas.Shared.Extensions;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi;
using PaymentService.Models;
using PayOS;
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

builder.Services.AddDbContext<PaymentDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection"))
        .UseSnakeCaseNamingConvention());

builder.Services.Configure<PayOSSettings>(
    builder.Configuration.GetSection("PayOS"));

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

builder.Services.AddScoped<IOrderService, OrderService>();
builder.Services.AddScoped<IPackageService, PackageService>();
builder.Services.AddScoped<ISubscriptionService, SubscriptionService>();
// P1: cấp phát credit_accounts (owner_type). Reserve/Consume/Release + webhook (P2/P4/P5/P6) = task sau.
builder.Services.AddScoped<ICreditAccountService, CreditAccountService>();
// P7: sinh order_code time+random, unique + retry (dep của P2 — Order/webhook chưa đổi ở task này).
builder.Services.AddScoped<IOrderCodeGenerator, OrderCodeGenerator>();
// E7: Payment phản ứng event Interview — consume (SessionScored) / release (SessionAbandoned).
// Handler scoped (dùng DbContext qua CreditAccountService); consumer là BackgroundService bind
// queue payment.credit ↔ exchange interview.events (E2/E3).
builder.Services.AddScoped<ICreditEventHandler, CreditEventHandler>();
builder.Services.AddHostedService<InterviewEventConsumer>();

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