namespace RagService.Api.Settings;

// 對應 Python 版的 app/config.py 的 Settings class
// Python 用 pydantic-settings 自動從環境變數讀取；C# 這裡用 IOptions<AppSettings>
// 在 Program.cs 綁定 builder.Configuration.GetSection("AppSettings")
// 環境變數覆寫格式：AppSettings__SecretKey=xxx（雙底線代表巢狀鍵）
public class AppSettings
{
    public string AppEnv { get; init; } = "development";

    // SQLite 路徑；EF Core 用 "Data Source=./rag.db" 格式
    public string DatabasePath { get; init; } = "./rag.db";

    // JWT 設定
    public string SecretKey { get; init; } = "";
    public string Algorithm { get; init; } = "HS256";
    public int AccessTokenExpireMinutes { get; init; } = 60;

    // Anthropic Claude API
    public string AnthropicApiKey { get; init; } = "";

    // 檔案上傳目錄
    public string UploadDir { get; init; } = "./uploads";

    // RAG 向量搜尋回傳數量
    public int TopK { get; init; } = 5;

    // CORS 允許的來源
    public string[] AllowedOrigins { get; init; } =
    [
        "http://localhost:3000",
        "http://localhost:5173",
        "http://127.0.0.1:3000",
        "http://127.0.0.1:5173",
    ];

    // Qdrant 連線設定（Docker 內部用 qdrant:6334，本機開發用 localhost:6334）
    public string QdrantHost { get; init; } = "localhost";
    public int QdrantPort { get; init; } = 6334;

    // ONNX 模型路徑（需手動下載 all-MiniLM-L6-v2.onnx + vocab.txt 放到此路徑）
    public string EmbeddingModelPath { get; init; } = "./models/all-MiniLM-L6-v2.onnx";
    public string EmbeddingVocabPath { get; init; } = "./models/vocab.txt";

    // Embedding 維度；all-MiniLM-L6-v2 固定 384 維
    public int EmbeddingDimension { get; init; } = 384;
}
