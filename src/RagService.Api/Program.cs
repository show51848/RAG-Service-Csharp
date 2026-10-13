using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using RagService.Api.Data;
using RagService.Api.Services;
using RagService.Api.Settings;

// 對應 Python 版 main.py 的啟動邏輯
// Python 用 FastAPI lifespan context manager；C# 用 builder/app 兩段式配置

var builder = WebApplication.CreateBuilder(args);

// ── 設定綁定 ─────────────────────────────────────────────────────────────────
// 對應 Python 版的 pydantic-settings Settings class（fail-fast 驗證）
// C# 在應用啟動時就把 appsettings.json 的 AppSettings section 綁定為強型別物件
var appSettings = builder.Configuration.GetSection("AppSettings").Get<AppSettings>()
    ?? throw new InvalidOperationException("AppSettings section is missing from configuration.");

// Fail-fast 驗證：對應 Python 版 validator 在啟動時報錯
if (string.IsNullOrWhiteSpace(appSettings.SecretKey) || appSettings.SecretKey.Length < 32)
    throw new InvalidOperationException("AppSettings:SecretKey must be at least 32 characters. Generate one with: openssl rand -hex 32");

if (string.IsNullOrWhiteSpace(appSettings.AnthropicApiKey))
    throw new InvalidOperationException("AppSettings:AnthropicApiKey must not be empty.");

builder.Services.Configure<AppSettings>(builder.Configuration.GetSection("AppSettings"));

// ── 資料庫（EF Core + SQLite）────────────────────────────────────────────────
// 對應 Python 版的 SQLAlchemy create_engine()
// EF Core 的優勢：schema 變更用 Migration 管理，有版本歷史
// 使用 service provider factory 讀取 IOptions<AppSettings>，確保 WebApplicationFactory
// 的 ConfigureAppConfiguration 覆寫（測試用 DatabasePath）能正確反映到這裡
builder.Services.AddDbContext<AppDbContext>((sp, options) =>
{
    var settings = sp.GetRequiredService<IOptions<AppSettings>>().Value;
    options.UseSqlite($"Data Source={settings.DatabasePath}");
});

// ── JWT 認證 ──────────────────────────────────────────────────────────────────
// 對應 Python 版的 python-jose + OAuth2PasswordBearer
// ASP.NET Core 的 [Authorize] 屬性會自動驗證這裡設定的 JWT
//
// 重要：不能用頂部捕獲的 appSettings 變數來設定 IssuerSigningKey！
// WebApplicationFactory.ConfigureAppConfiguration 覆寫在 Program.cs 讀取 builder.Configuration
// 之後才套用，導致 AddJwtBearer 用舊 SecretKey 但 AuthService（IOptions）用新 SecretKey → 401。
// 解法：用 AddOptions + IOptions<AppSettings> 讓 TokenValidationParameters 在 runtime 解析。
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer();

builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
    .Configure<IOptions<AppSettings>>((options, settings) =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(
                Encoding.ASCII.GetBytes(settings.Value.SecretKey)),
            ValidateIssuer = false,
            ValidateAudience = false,
            ClockSkew = TimeSpan.Zero,
        };
    });

builder.Services.AddAuthorization();

// ── CORS ──────────────────────────────────────────────────────────────────────
// 對應 Python 版的 CORSMiddleware，dev/prod 策略分開
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        var origins = appSettings.AllowedOrigins.ToList();
        if (appSettings.AppEnv == "development")
            origins.Add("null");  // 允許 file:// 開啟的頁面，對應 Python 版相同邏輯

        policy.WithOrigins([.. origins])
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials();
    });
});

// ── HTTP Client（Anthropic API）───────────────────────────────────────────────
// IHttpClientFactory 的優點：自動管理連線池、避免 socket exhaustion
// Python 版用模組層級的 anthropic.Anthropic() 單例，C# 用 named client 達到同樣效果
builder.Services.AddHttpClient("Anthropic")
    .ConfigureHttpClient((sp, client) =>
    {
        var settings = sp.GetRequiredService<IOptions<AppSettings>>().Value;
        client.DefaultRequestHeaders.Add("x-api-key", settings.AnthropicApiKey);
        client.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");
    });

// ── 應用服務 DI 注冊 ──────────────────────────────────────────────────────────
// 對應 Python 版的 module-level 單例 + Depends()
// AddSingleton = 整個 App 生命週期只建立一次（對應 Python 的全域單例，例如 InferenceSession）
// AddScoped = 每個 HTTP request 建立一次（對應 Python 的 Depends(get_db) per-request Session）
builder.Services.AddSingleton<IAuthService, AuthService>();
builder.Services.AddSingleton<IEmbeddingService, EmbeddingService>();
builder.Services.AddSingleton<IQdrantService, QdrantService>();
builder.Services.AddScoped<IIngestionService, IngestionService>();
builder.Services.AddScoped<IRetrievalService, RetrievalService>();
builder.Services.AddScoped<ILlmService, LlmService>();

// ── Controllers + Swagger ────────────────────────────────────────────────────
// JSON 格式對齊 Python 版（snake_case 欄位 + 字串 enum），細節見 ApiJsonOptions
builder.Services.AddControllers()
    .AddJsonOptions(o => ApiJsonOptions.Apply(o.JsonSerializerOptions));
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "RAG Service", Version = "v1" });

    // Swagger UI 支援 Bearer Token 認證，對應 Python 版 docs_url="/api/docs"
    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        Description = "Enter your JWT token",
    });
    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" }
            },
            []
        }
    });
});

var app = builder.Build();

// ── 啟動初始化（對應 Python 版 lifespan yield 前的邏輯）───────────────────────
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    // EF Core 自動套用 Migrations（對應 Python 版的 create_tables()）
    // MigrateAsync 比 EnsureCreated 更好：可以套用 schema 變更而不只是建立
    await db.Database.MigrateAsync();

    // Qdrant collection 初始化
    var qdrant = scope.ServiceProvider.GetRequiredService<IQdrantService>();
    await qdrant.EnsureCollectionAsync();
}

// 確保上傳目錄存在
Directory.CreateDirectory(appSettings.UploadDir);

// ── Middleware Pipeline ───────────────────────────────────────────────────────
// 對應 Python 版 app.add_middleware() 的順序
// ASP.NET Core 的 middleware pipeline 概念類似 FastAPI，但配置方式不同

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "RAG Service v1"));
}

// 提供 wwwroot/ 底下的前端頁面，對應 FastAPI 的 app.mount("/", StaticFiles(...))
// UseDefaultFiles 讓 "/" 自動對應到 index.html，必須放在 UseStaticFiles 之前
app.UseDefaultFiles();
app.UseStaticFiles();

app.UseCors();
app.UseAuthentication();  // 必須在 UseAuthorization 之前
app.UseAuthorization();
app.MapControllers();

// 健康檢查端點，對應 Python 版的 /health
app.MapGet("/health", () => new { status = "ok", version = "1.0.0" })
   .WithTags("system");

app.Run();

// 讓 Integration Test 能存取 WebApplicationFactory
public partial class Program { }
