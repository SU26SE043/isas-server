using Amazon.S3;
using Isas.InterviewService.Models;
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

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddServiceCors(builder.Configuration);
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddHealthChecks();

builder.Services.AddScoped<ICVParserService, CVParserService>();
builder.Services.AddScoped<IStorageService, StorageService>();
builder.Services.AddHostedService<BucketInitializer>();

builder.Services.AddScoped<FileStorageOptions>();

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

builder.Services.AddDbContext<InterviewDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));

builder.Services.Configure<FileStorageOptions>(
    builder.Configuration.GetSection("SeaweedFS"));

builder.Services.AddSingleton<IAmazonS3>(sp => {

    var opts = sp.GetRequiredService<IOptions<FileStorageOptions>>().Value;

    return new AmazonS3Client(
        //opts.AccessKey,
        //opts.SecretKey,
        new Amazon.Runtime.AnonymousAWSCredentials(),
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
        options.Title = "Interview API";
        options.WithDefaultHttpClient(ScalarTarget.JavaScript, ScalarClient.Fetch);
    });
}

app.UseServiceCors();
app.UseAuthentication();
app.UseAuthorization();

app.MapHealthChecks("/health");

app.MapControllers();

app.Run();