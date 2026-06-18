using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RagService.Api.Data;
using RagService.Api.Services;
using RagService.Tests.Integration.Stubs;

namespace RagService.Tests.Integration;

// WebApplicationFactory 讓測試直接對 ASP.NET Core 的 middleware pipeline 送 HTTP 請求
// 對應 Python 版 pytest 的 TestClient：from fastapi.testclient import TestClient
//
// IAsyncLifetime：確保 Qdrant container 在測試 app 建立之前就已啟動
// （因為 app 啟動時會呼叫 qdrant.EnsureCollectionAsync()）

public class RagServiceFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    // Qdrant container：使用 Testcontainers 啟動真實的 Qdrant Docker image
    // 對應 Python 版整合測試若有的話就是啟動一個本地 ChromaDB 實例
    private readonly IContainer _qdrantContainer = new ContainerBuilder()
        .WithImage("qdrant/qdrant:v1.13.6")
        .WithPortBinding(6334, true)  // gRPC port（.NET SDK 使用這個）
        .WithPortBinding(6333, true)  // REST port（healthz 使用）
        .WithWaitStrategy(
            Wait.ForUnixContainer()
                .UntilPortIsAvailable(6334))  // gRPC 可用後才繼續
        .Build();

    // 每次測試使用獨立的 SQLite 檔案，確保測試之間不互相影響
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"rag_test_{Guid.NewGuid():N}.db");

    public async Task InitializeAsync()
    {
        // 必須在 WebApplicationFactory 建立 app 之前啟動容器
        // 因為 Program.cs 的啟動邏輯會呼叫 EnsureCollectionAsync()
        await _qdrantContainer.StartAsync();
    }

    public new async Task DisposeAsync()
    {
        await base.DisposeAsync();
        await _qdrantContainer.DisposeAsync();
        // EF Core / Microsoft.Data.Sqlite 保有連線池，直接 Delete 會報 IOException
        // ClearAllPools() 確保所有 SQLite 連線都已關閉，才能安全刪除測試用的暫存 DB 檔案
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        var qdrantPort = _qdrantContainer.GetMappedPublicPort(6334);

        // ConfigureAppConfiguration：在 Program.cs 讀取設定之前注入測試用設定值
        // 對應 Python 版的 monkeypatch.setenv() 或 test settings override
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                // 提供合法 SecretKey，繞過 Program.cs 的 fail-fast 驗證
                ["AppSettings:SecretKey"] = "integration-test-secret-key-32-chars!!",
                // LLM 服務已被 Stub 替換，這個值不會被真正使用
                ["AppSettings:AnthropicApiKey"] = "test-placeholder-not-actually-used",
                // 指向 Testcontainers 啟動的 Qdrant
                ["AppSettings:QdrantHost"] = "localhost",
                ["AppSettings:QdrantPort"] = qdrantPort.ToString(),
                // 使用獨立的 SQLite 檔案（支援真實 Migration）
                ["AppSettings:DatabasePath"] = _dbPath,
                ["AppSettings:UploadDir"] = Path.GetTempPath(),
                ["AppSettings:AppEnv"] = "development",
            });
        });

        // ConfigureServices：替換有外部依賴的 services（ONNX 模型、Anthropic API）
        builder.ConfigureServices(services =>
        {
            // 移除真實的 EmbeddingService（需要 ONNX 模型檔案），換成 Stub
            var embeddingDesc = services.SingleOrDefault(d =>
                d.ServiceType == typeof(IEmbeddingService));
            if (embeddingDesc is not null) services.Remove(embeddingDesc);
            services.AddSingleton<IEmbeddingService, StubEmbeddingService>();

            // 移除真實的 LlmService（需要 Anthropic API key），換成 Stub
            var llmDescs = services
                .Where(d => d.ServiceType == typeof(ILlmService))
                .ToList();
            foreach (var d in llmDescs) services.Remove(d);
            services.AddScoped<ILlmService, StubLlmService>();
        });
    }
}
