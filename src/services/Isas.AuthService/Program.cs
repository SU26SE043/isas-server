using Isas.AuthService.Models;
using Isas.AuthService.Services;
using Isas.Shared.Extensions;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.UI.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi;
using Scalar.AspNetCore;
using System.Security.Claims;
using System.Text;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);

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

builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.AddScoped<IJwtService, JwtService>();
builder.Services.AddTransient<IEmailSender, EmailSender>();
builder.Services.AddSingleton<IGoogleLoginRedirects, GoogleLoginRedirects>();
// Mã dùng-một-lần của đăng nhập Google giữ trong BỘ NHỚ TIẾN TRÌNH (không có bảng DB).
// ⚠ Hệ quả phải biết khi vận hành: mã phát ở instance nào chỉ đổi được ở ĐÚNG instance đó, và
// restart/deploy làm mất mã đang bay (người dùng bấm đăng nhập Google lại là xong). Deploy hiện
// tại single-instance nên chấp nhận được; scale ra nhiều instance thì phải bật sticky session
// hoặc chuyển kho này sang Redis/bảng DB. Xem docs/services/auth.md §Đăng nhập Google.
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<IGoogleAuthCodeStore, GoogleAuthCodeStore>();

builder.Services.AddControllers().AddJsonOptions(options =>
{
    options.JsonSerializerOptions.ReferenceHandler = ReferenceHandler.IgnoreCycles;
    options.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.Never;
});

builder.Services.AddHttpContextAccessor();

builder.Services
    .AddIdentityCore<User>(options =>
    {
        // password
        options.Password.RequireDigit = true;
        options.Password.RequiredLength = 6;
        options.Password.RequireUppercase = false;
        options.Password.RequireLowercase = false;
        options.Password.RequireNonAlphanumeric = false;

        // lockout
        options.Lockout.MaxFailedAccessAttempts = 5;
        options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(5);
        options.Lockout.AllowedForNewUsers = true;

        // user
        options.User.RequireUniqueEmail = true;
    })
    .AddRoles<Role>()
    .AddEntityFrameworkStores<AuthDbContext>()
    .AddSignInManager()
    .AddDefaultTokenProviders();

var jwtKey = builder.Configuration["Jwt:Key"]
             ?? throw new InvalidOperationException("Jwt:Key is missing in configuration.");

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
            RoleClaimType = ClaimTypes.Role,
            ValidIssuer = builder.Configuration["Jwt:Issuer"],
            ValidAudience = builder.Configuration["Jwt:Audience"],
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey))
        };
    });

var google = builder.Configuration.GetSection("Authentication:Google");
builder.Services.AddAuthentication()
    // Cookie TẠM giữ kết quả xác thực Google giữa 2 chặng (handler callback → action MVC).
    // AddIdentityCore KHÔNG đăng ký scheme này, mà SignInManager.GetExternalLoginInfoAsync()
    // đọc đúng nó → thiếu là cả luồng Google không bao giờ chạy được.
    .AddCookie(IdentityConstants.ExternalScheme, options =>
    {
        options.Cookie.Name = IdentityConstants.ExternalScheme;
        options.Cookie.HttpOnly = true;
        options.Cookie.IsEssential = true;
        // Path "/" tường minh: response đặt cookie đi qua gateway (PathBase = /api/v1), để mặc định
        // cookie có thể bị giới hạn path và không quay lại được chặng sau.
        options.Cookie.Path = "/";
        // Lax (không Strict/None): Google trả về bằng ĐIỀU HƯỚNG GET cấp cao nhất → Lax vẫn gửi
        // cookie, đồng thời không đòi Secure nên chạy được cả dev http://localhost.
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.ExpireTimeSpan = TimeSpan.FromMinutes(10); // chỉ sống trong 1 vòng OAuth
    })
    .AddGoogle(options =>
    {
        options.ClientId = google["ClientId"] ?? string.Empty;
        options.ClientSecret = google["ClientSecret"] ?? string.Empty;
        options.SignInScheme = IdentityConstants.ExternalScheme;
        // KHÁC route action MVC: middleware remote-auth chạy trước MVC và short-circuit path nó giữ,
        // trùng nhau thì GoogleLoginCallback không bao giờ được gọi (bug cũ).
        options.CallbackPath = GoogleLoginRedirects.OAuthCallbackPath;
        options.CorrelationCookie.Path = "/";
        options.CorrelationCookie.SameSite = SameSiteMode.Lax;
        options.CorrelationCookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    });



builder.Services.AddAuthorization();

builder.Services.AddDbContext<AuthDbContext>(options =>
    options.UseNpgsql(
        builder.Configuration.GetConnectionString("DefaultConnection"),
        npgsql => npgsql.EnableRetryOnFailure()));

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference(options =>
    {
        options.Title = "Auth API";
        options.WithDefaultHttpClient(ScalarTarget.JavaScript, ScalarClient.Fetch);
    });
}


// Gateway route /api/v1/auth/** rồi STRIP tiền tố → service chỉ thấy /auth/**. Handler Google lại tự
// dựng redirect_uri từ Scheme/Host/PathBase của request đang chạy → sẽ ra host NỘI BỘ và thiếu
// /api/v1 ⇒ URI không khớp cái đăng ký trên Google Console và 404 ở edge.
// Cách sửa: ép request context về ORIGIN CÔNG KHAI (lấy từ config server, KHÔNG từ header client —
// header do client kiểm soát sẽ thành lỗ redirect) cho đúng 2 path OAuth, để redirect_uri lúc
// challenge và lúc đổi code hoàn toàn khớp nhau. Phải chạy TRƯỚC UseAuthentication.
var publicBaseUrl = builder.Configuration["Gateway:PublicBaseUrl"];
if (Uri.TryCreate(publicBaseUrl, UriKind.Absolute, out var publicBase))
{
    var publicPathBase = publicBase.AbsolutePath.TrimEnd('/');
    app.Use(async (context, next) =>
    {
        var path = context.Request.Path.Value ?? string.Empty;
        if (string.Equals(path, GoogleLoginRedirects.LoginPath, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(path, GoogleLoginRedirects.OAuthCallbackPath, StringComparison.OrdinalIgnoreCase))
        {
            context.Request.Scheme = publicBase.Scheme;
            context.Request.Host = new HostString(publicBase.Authority);
            context.Request.PathBase = publicPathBase;
        }

        await next();
    });
}

app.UseAuthentication();
app.UseAuthorization();

app.MapHealthChecks("/health");

app.MapControllers();


app.Run();