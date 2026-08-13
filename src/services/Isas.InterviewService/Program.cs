using Amazon.S3;
using Isas.InterviewService.Services;
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
using Isas.InterviewService.ApplicationDbContext;
using Isas.InterviewService.Models;
using Isas.InterviewService.Services.Interfaces;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddServiceCors(builder.Configuration);
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddHealthChecks();

builder.Services.AddSingleton<IPdfTextExtractor, PdfTextExtractor>();   // DB17: shared PDF extractor
builder.Services.AddScoped<ICVParserService, CVParserService>();
builder.Services.AddScoped<IStorageService, StorageService>();
builder.Services.AddScoped<IAnswerService, AnswerService>();
builder.Services.AddHostedService<BucketInitializer>();
builder.Services.AddSingleton<IScoringJobPublisher, ScoringJobPublisher>();
builder.Services.AddSingleton<ISessionEventPublisher, SessionEventPublisher>();
builder.Services.AddScoped<ISessionResultService, SessionResultService>();   // BC9
builder.Services.AddScoped<ICriterionBenchmarkService, CriterionBenchmarkService>();   // F14
builder.Services.AddScoped<ISessionScoringNotifier, SessionScoringNotifier>();
builder.Services.AddScoped<IPracticeService, PracticeService>();
builder.Services.AddScoped<IQuestionSpeechService, QuestionSpeechService>();   // TTS đọc câu hỏi
builder.Services.AddScoped<ICvAnalysisService, CvAnalysisService>();   // BC7
builder.Services.AddScoped<IRepoAnalysisService, RepoAnalysisService>(); // BC18
builder.Services.AddScoped<IRubricLibraryService, RubricLibraryService>();   // BC16 — rubric cá nhân B2C
builder.Services.AddScoped<IAdminB2CRubricService, AdminB2CRubricService>();   // admin quản bộ chuẩn B2C
builder.Services.AddScoped<IRoadmapService, RoadmapService>();   // BC12
builder.Services.AddScoped<IRoadmapLessonService, RoadmapLessonService>();   // BC14
builder.Services.AddScoped<IRoadmapReportService, RoadmapReportService>();   // BC15
builder.Services.AddScoped<PromptTemplateService>();   // F21 — prompt tuỳ biến (FR17)

// RAG grounding — kho tri thức (Qdrant) + retrieval. IChunker/QdrantVectorStore = singleton (không state
// theo request); KnowledgeService scoped (dùng DbContext). QdrantClient singleton từ Qdrant:Url.
builder.Services.AddSingleton<IChunker, Chunker>();
builder.Services.AddSingleton(sp =>
{
    var url = builder.Configuration["Qdrant:Url"] ?? "http://localhost:6334";
    var apiKey = builder.Configuration["Qdrant:ApiKey"];
    var uri = new Uri(url);
    // QdrantClient(host, port, https, apiKey) — gRPC (mặc định 6334). Uri.Port = -1 nếu không nêu → 6334.
    return new Qdrant.Client.QdrantClient(
        uri.Host,
        uri.Port > 0 ? uri.Port : 6334,
        https: uri.Scheme == Uri.UriSchemeHttps,
        apiKey: string.IsNullOrWhiteSpace(apiKey) ? null : apiKey);
});
builder.Services.AddSingleton<IVectorStore, QdrantVectorStore>();
builder.Services.AddScoped<IKnowledgeService, KnowledgeService>();
builder.Services.AddHostedService<QdrantCollectionInitializer>();   // tạo collection lúc startup (best-effort)

builder.Services.AddHttpClient<IAiServiceQuestionGenerator,AiServiceQuestionGenerator>(c =>
{
    c.BaseAddress = new Uri(builder.Configuration["AiService:BaseUrl"]!);
    c.Timeout = TimeSpan.FromSeconds(90);  // SC3/QV1: quality retry and verification can exceed one generation
});

builder.Services.AddHttpClient<IAiServiceCvAnalyzer, AiServiceCvAnalyzer>(c =>   // BC7
{
    c.BaseAddress = new Uri(builder.Configuration["AiService:BaseUrl"]!);
    c.Timeout = TimeSpan.FromSeconds(60);  // LLM có thể chậm
});
builder.Services.AddHttpClient<IAiServiceRepoAnalyzer, AiServiceRepoAnalyzer>(c =>
{
    c.BaseAddress = new Uri(builder.Configuration["AiService:BaseUrl"]!);
    c.Timeout = TimeSpan.FromSeconds(60);
});
builder.Services.AddHttpClient<IGitHubRepoFetcher, GitHubRepoFetcher>(c =>
{
    c.BaseAddress = new Uri("https://api.github.com/");
    c.Timeout = TimeSpan.FromSeconds(20);
    c.DefaultRequestHeaders.UserAgent.ParseAdd("isas-server");
});

builder.Services.AddHttpClient<IAiServiceRoadmapGenerator, AiServiceRoadmapGenerator>(c =>   // BC12
{
    c.BaseAddress = new Uri(builder.Configuration["AiService:BaseUrl"]!);
    // 120s (các client khác giữ 60s): đường này sinh lý thuyết bài học — đo thật trên deploy
    // 2026-08-03 mất 13,4s / 49,0s / 53,8s, tức 60s đã sát trần TRƯỚC khi có gì thêm. Bài trượt
    // rubric nay được bắt viết lại ngay trong cùng lượt (AIService lesson_theory_max_attempts) nên
    // ca xấu là hai lượt LLM nối nhau.
    c.Timeout = TimeSpan.FromSeconds(120);
});

builder.Services.AddHttpClient<IAiServiceSessionSummarizer, AiServiceSessionSummarizer>(c =>   // BC10
{
    c.BaseAddress = new Uri(builder.Configuration["AiService:BaseUrl"]!);
    c.Timeout = TimeSpan.FromSeconds(60);  // LLM có thể chậm
});

builder.Services.AddHttpClient<IAiServiceInterviewDecider, AiServiceInterviewDecider>(c =>   // phỏng vấn THÍCH ỨNG
{
    c.BaseAddress = new Uri(builder.Configuration["AiService:BaseUrl"]!);
    // 90s: đủ cho transcribe ĐỒNG BỘ (Whisper) + Gemini quyết định câu kế trong 1 request.
    c.Timeout = TimeSpan.FromSeconds(90);
});

builder.Services.AddHttpClient<IAiServiceSpeechSynthesizer, AiServiceSpeechSynthesizer>(c =>   // TTS đọc câu hỏi
{
    c.BaseAddress = new Uri(builder.Configuration["AiService:BaseUrl"]!);
    // 60s: cache hit trả gần như tức thì; miss = 1 lần gọi Gemini TTS + encode mp3.
    c.Timeout = TimeSpan.FromSeconds(60);
});

builder.Services.AddHttpClient<ICreditReservationClient, CreditReservationClient>(c =>   // BC2
{
    // Nội bộ (KHÔNG qua gateway) → gọi thẳng PaymentService. X-Internal-Token gắn trong client.
    c.BaseAddress = new Uri(builder.Configuration["Payment:BaseUrl"]!);
    c.Timeout = TimeSpan.FromSeconds(10);  // reserve nhanh (DB update), không phải LLM
});
builder.Services.AddHttpClient<IEntitlementClient, EntitlementClient>(c =>
{
    c.BaseAddress = new Uri(builder.Configuration["Payment:BaseUrl"]!);
    c.Timeout = TimeSpan.FromSeconds(5);
});

builder.Services.AddHttpClient<IAiServiceEmbedder, AiServiceEmbedder>(c =>   // RAG grounding — /embed
{
    c.BaseAddress = new Uri(builder.Configuration["AiService:BaseUrl"]!);
    // Embed nhanh hơn generate nhưng batch ingest có thể lớn → 60s như các call AI khác.
    c.Timeout = TimeSpan.FromSeconds(60);
});
builder.Services.AddHttpClient<IContext7Client, Context7Client>(c =>   // RAG grounding — Context7 ingest
{
    c.BaseAddress = new Uri(builder.Configuration["Context7:BaseUrl"] ?? "https://context7.com/api/v2/");
    c.Timeout = TimeSpan.FromSeconds(30);
});
builder.Services.AddHttpClient<IUrlContentFetcher, UrlContentFetcher>(c =>   // RAG grounding — tải URL ingest
{
    c.Timeout = TimeSpan.FromSeconds(20);
    c.DefaultRequestHeaders.UserAgent.ParseAdd("isas-server");
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
    // Chuẩn hoá DateTime nhận vào về UTC. Reachable: POST /internal/sessions/campaign nhận
    // expiresAt → practice_sessions.deadline (timestamptz). Hôm nay an toàn chỉ vì Campaign tình cờ
    // gửi 'Z'; client gửi offset SỐ sẽ thành Kind=Local → Npgsql từ chối → 500. Xem UtcDateTimeConverter.
    options.JsonSerializerOptions.Converters.Add(new UtcDateTimeConverter());
});


builder.Services.AddHttpContextAccessor();
builder.Services.AddHostedService<StuckAnswerRepublisher>();
builder.Services.AddHostedService<SessionAbandonSweeper>();
builder.Services.AddHostedService<OutboxDispatcher>();   // DB2: transactional outbox → phát settlement-event
builder.Services.AddHostedService<OutboxPurger>();       // DB28: retention outbox đã phát (mặc định 30 ngày)


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
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(builder.Configuration["Jwt:Key"]!))
        };
    });
builder.Services.AddAuthorization();

// DB25b — retry transient (blip mạng / deadlock Postgres) thay vì để nổi lên thành 500.
// BẬT ĐƯỢC vì cả 2 site `BeginTransactionAsync` của service này đã đi qua `DbRetry.RunAsync`
// (PromptTemplateService · SessionAbandonSweeper). Bật khi CHƯA bọc thì transaction tự mở sẽ ném
// InvalidOperationException ở MỌI request trên Postgres, trong khi test SQLite vẫn xanh 100%.
// Guard chống tái phát: ExecutionStrategyDb25bTests.MoiTransactionTuMo_DeuNamTrongDbRetry.
builder.Services.AddDbContext<InterviewDbContext>(options =>
    options.UseNpgsql(
            builder.Configuration.GetConnectionString("DefaultConnection"),
            npgsql => npgsql.EnableRetryOnFailure())
        .UseSnakeCaseNamingConvention());

builder.Services.Configure<FileStorageOptions>(
    builder.Configuration.GetSection(FileStorageOptions.SectionName));

builder.Services.Configure<ScoringOptions>(
    builder.Configuration.GetSection(ScoringOptions.SectionName));   // BC9
builder.Services.Configure<CapacityOptions>(
    builder.Configuration.GetSection(CapacityOptions.SectionName));
builder.Services.Configure<AdaptiveOptions>(
    builder.Configuration.GetSection(AdaptiveOptions.SectionName));   // phỏng vấn THÍCH ỨNG (B2C)
builder.Services.Configure<RoadmapOptions>(
    builder.Configuration.GetSection(RoadmapOptions.SectionName));   // BC15
builder.Services.Configure<BenchmarkOptions>(
    builder.Configuration.GetSection(BenchmarkOptions.SectionName));   // F14
builder.Services.Configure<OutboxSettings>(
    builder.Configuration.GetSection(OutboxSettings.SectionName));   // DB2
builder.Services.Configure<RepublisherSettings>(
    builder.Configuration.GetSection(RepublisherSettings.SectionName));   // DB29
builder.Services.Configure<GroundingOptions>(
    builder.Configuration.GetSection(GroundingOptions.SectionName));   // RAG grounding — Enabled/TopK/threshold

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
