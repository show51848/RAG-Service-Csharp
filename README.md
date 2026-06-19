# RAG Service (C# / ASP.NET Core)

以 ASP.NET Core + Qdrant 為核心的文件問答後端，支援 JWT 認證、多用戶隔離、PDF/TXT 上傳與向量檢索。

這是 [show51848/RAG-Service](https://github.com/show51848/RAG-Service)（Python / FastAPI 版）的 C# 重建專案，**核心目的不是做出更好的產品，是學習 C# / ASP.NET Core，並能解釋兩種語言、兩種框架的架構差異**。每個技術選型都有對應的取捨理由，詳見下方〈設計決策〉與〈跟 Python 版的差異〉。

[![.NET](https://img.shields.io/badge/.NET-8.0%20LTS-512BD4?logo=dotnet)](https://dotnet.microsoft.com/)
[![ASP.NET Core](https://img.shields.io/badge/ASP.NET%20Core-8.0-512BD4?logo=dotnet)](https://learn.microsoft.com/aspnet/core)
[![Qdrant](https://img.shields.io/badge/Qdrant-vector%20db-DC244C)](https://qdrant.tech/)
[![EF Core](https://img.shields.io/badge/EF%20Core-SQLite-512BD4)](https://learn.microsoft.com/ef/core/)
[![Docker](https://img.shields.io/badge/Docker-Compose-2496ED?logo=docker)](https://www.docker.com/)

---

## 技術架構

```
┌─────────────────────────────────────────────────────────────┐
│                      Client (curl / Swagger UI)               │
└────────────────────────────┬──────────────────────────────────┘
                             │ HTTP / JSON
┌────────────────────────────▼──────────────────────────────────┐
│         ASP.NET Core 8  (Kestrel, container port 8080)         │
│                                                                │
│  ┌──────────┐    ┌────────────────┐    ┌───────────────────┐  │
│  │  /auth   │    │     /docs      │    │      /chat        │  │
│  │ register │    │  upload  list   │    │      query         │  │
│  │  login   │    │     delete      │    │                    │  │
│  └────┬─────┘    └───────┬────────┘    └─────────┬──────────┘  │
│       │ JWT              │                       │             │
│  ┌────▼──────────────────▼───────────────────────▼──────────┐  │
│  │            SQLite  (Entity Framework Core)                │  │
│  │         Users · Documents (status / hash / chunks)        │  │
│  └────────────────────────────────────────────────────────────┘  │
│                                                                │
│  ┌────────────────────┐    ┌──────────────────────────────┐   │
│  │   Qdrant (Vectors)  │    │      Anthropic Claude        │   │
│  │  gRPC :6334          │    │   claude-haiku-4-5 (HTTP)    │   │
│  │  ONNX Runtime local  │    │   Generation only            │   │
│  │  all-MiniLM-L6-v2    │    └──────────────────────────────┘   │
│  └────────────────────┘                                        │
└──────────────────────────────────────────────────────────────────┘
              ▲  docker compose up
```

**請求流程（RAG 問答）**

```
上傳文件                                  問答查詢
   │                                        │
   ▼                                        ▼
PdfPig / 純文字讀取                    ONNX 本機推論 Embed 問題
   │                                        │
   ▼                                        ▼
段落分塊（中文標題感知，上限 1000 字）    Qdrant 向量搜尋
   │                                  (user_id + doc_id filter)
   ▼                                        │
ONNX Runtime 本機 Embedding                  ▼
(all-MiniLM-L6-v2, 384 維)              取得 Top-K，每文件去重取最佳 chunk
   │                                        │
   ▼                                        ▼
Qdrant Upsert                          Claude claude-haiku-4-5 生成答案
(payload: user_id / doc_id / text)            │
                                              ▼
                                        回傳 answer + citations
```

---

## 功能列表

| 功能 | 說明 |
| --- | --- |
| **JWT 認證** | Argon2id 密碼雜湊（手動管理 salt）、Bearer Token、60 分鐘過期 |
| **文件上傳** | PDF / TXT，50MB 上限，MD5 去重，同步 ingestion pipeline |
| **中文標題感知分塊** | 偵測「一、二、三…」等中文序號標題觸發切段，單一 chunk 上限 1000 字 |
| **本機 Embedding** | ONNX Runtime 載入 all-MiniLM-L6-v2，mean pooling + L2 正規化，零雲端成本 |
| **RAG 問答** | 向量搜尋 → 每文件去重取最佳 chunk → Claude Haiku 生成含引用編號答案 |
| **多用戶隔離** | Qdrant payload filter 以 `user_id` 嚴格隔離，不可越權讀取 |
| **強型別 DTO** | Request/Response 全部用 C# record 定義，配合 DataAnnotations 驗證 |
| **容器化** | Docker Compose（API + Qdrant），volume 持久化資料 |
| **測試** | xUnit + Moq 單元測試（54 個測試案例），Testcontainers 整合測試 |

---

## 快速啟動

### 前置需求

- Docker Desktop
- Anthropic API Key（[申請](https://console.anthropic.com/)）
- ONNX embedding 模型：`all-MiniLM-L6-v2.onnx` + `vocab.txt`，需自行下載放到 `models/` 目錄

### 1. 設定環境變數

建立 `.env`：

```
SECRET_KEY=your-secret-key          # openssl rand -hex 32，至少 32 字元
ANTHROPIC_API_KEY=sk-ant-xxxxxxxx
ALLOWED_ORIGIN=http://localhost:3000
```

### 2. 啟動服務（API + Qdrant）

```
docker compose up --build
```

啟動時會自動：
- 套用 EF Core Migrations 建立 SQLite schema
- 確認/建立 Qdrant collection（384 維，cosine 距離）

### 3. 開啟 API 文件

瀏覽器前往 <http://localhost:8000/swagger>

Swagger UI 支援直接貼 Bearer Token 測試需認證的端點。

### 4. 本機開發（不用 Docker）

```
cd src/RagService.Api
dotnet restore
dotnet run
```

需另外啟動 Qdrant（或本機跑 `docker run -p 6333:6333 -p 6334:6334 qdrant/qdrant`），並在 `appsettings.Development.json` 設定 `QdrantHost=localhost`。

```
dotnet test          # 在 repo 根目錄執行所有測試
```

---

## API 端點

所有需要認證的端點請帶 Header：`Authorization: Bearer <token>`

### 認證

#### 註冊

```
curl -X POST http://localhost:8000/auth/register \
  -H "Content-Type: application/json" \
  -d '{"username":"alice","email":"alice@example.com","password":"secret123"}'
# 201 {"id":1,"username":"alice","email":"alice@example.com"}
```

#### 登入

```
curl -X POST http://localhost:8000/auth/login \
  -H "Content-Type: application/json" \
  -d '{"username":"alice","password":"secret123"}'
# 200 {"accessToken":"eyJ...","tokenType":"bearer"}
```

> 與 Python 版的差異：Python 版用 `OAuth2PasswordRequestForm`（form data）；C# 版改用 JSON body，這是兩個生態系各自的慣用做法。

### 文件管理

#### 上傳文件

```
curl -X POST http://localhost:8000/docs/upload \
  -H "Authorization: Bearer $TOKEN" \
  -F "file=@report.pdf"
# 201 {"docId":1,"filename":"report.pdf","status":"Pending"}
```

回傳 `409` 表示同一使用者已上傳過相同內容（MD5 比對）；`413` 超過 50MB；`415` 副檔名不支援（僅 `.pdf` / `.txt`）。

#### 列出文件

```
curl http://localhost:8000/docs \
  -H "Authorization: Bearer $TOKEN"
```

#### 刪除文件

```
curl -X DELETE http://localhost:8000/docs/1 \
  -H "Authorization: Bearer $TOKEN"
# 204 No Content
```

刪除順序為「先刪 Qdrant 向量 → 再刪磁碟檔案 → 最後刪 DB 記錄」，避免中途失敗留下孤兒向量資料。文件不存在或不屬於當前使用者都回 `404`（不洩漏 ID 是否存在）。

### RAG 問答

```
# 查詢全部文件
curl -X POST http://localhost:8000/chat/query \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d '{"question":"這份文件的主要結論是什麼？"}'

# 指定特定文件範圍
curl -X POST http://localhost:8000/chat/query \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d '{"question":"請摘要第一章","docIds":[1,2]}'
```

回應範例：

```
{
  "answer": "根據文件 [1]，主要結論是...",
  "citations": [
    {"docId": 1, "filename": "report.pdf", "chunk": "...", "score": 0.89}
  ]
}
```

若指定的 `docIds` 不屬於當前使用者，回傳 `404`。若沒有任何相關 chunk 命中，不會呼叫 LLM（節省 API 費用），直接回傳提示訊息。

### 系統

```
curl http://localhost:8000/health
# {"status":"ok","version":"1.0.0"}
```

---

## 設計決策

### 為何用 Qdrant 取代 ChromaDB？

ChromaDB 沒有官方 C# SDK，而 Qdrant 提供官方 .NET SDK（透過 gRPC 通訊），生態相容性是換用的主要原因。資料隔離邏輯維持一致：所有 chunk 存在同一個 `documents` collection，用 `user_id` payload filter 做隔離，搜尋時用 `Must` 條件強制套用，刪除時用 `doc_id` filter 精確清除。

```csharp
var mustConditions = new List<Condition>
{
    new() { Field = new FieldCondition { Key = "user_id", Match = new Match { Integer = userId } } },
};
var filter = new Filter { Must = { mustConditions } };
```

### 為何 Embedding 改用 ONNX Runtime 本機推論？

Python 版透過 ChromaDB 內建的 `DefaultEmbeddingFunction`（內部也是 ONNX + all-MiniLM-L6-v2）。C# 版直接操作 ONNX Runtime API，流程更透明：分詞（BERTTokenizers）→ 建立 tensor → 推論取得 `last_hidden_state` → mean pooling（只對 `attention_mask=1` 的 token 平均）→ L2 正規化。這樣做的好處是面試時能完整解釋整個推論流程，而不是依賴框架黑盒。兩版用同一顆模型，確保向量維度與行為一致，可以直接比較。

### 為何密碼雜湊用 Argon2 而非 bcrypt？

Python 版用 passlib（自動管理 salt），C# 版用 `Konscious.Security.Cryptography.Argon2`，這個套件需要手動管理 salt，因此 `User` 多了一個 `PasswordSalt` 欄位獨立儲存。雜湊參數刻意調高（64MB 記憶體、4 並行度、3 次迭代）以增加 GPU 暴力破解成本，驗證時用 `CryptographicOperations.FixedTimeEquals` 做 constant-time 比較，防止 timing attack。

### 為何用 EF Core Migrations 而非 EnsureCreated？

Python 版用 SQLAlchemy 的 `create_tables()`，啟動時直接建表。C# 版改用 EF Core Migrations，可以追蹤 schema 變更歷史、套用增量更新，而不只是「建立或不建立」的二元邏輯，更貼近真實團隊協作場景。

### 為何 JWT 設定要透過 `IOptions<AppSettings>` 而非啟動時讀取的變數？

`WebApplicationFactory`（測試用）的 `ConfigureAppConfiguration` 覆寫，會在 `Program.cs` 讀取 `builder.Configuration` **之後**才套用。如果 JWT Bearer 的 `IssuerSigningKey` 在頂層直接讀取設定值，測試環境覆寫的 `SecretKey` 不會反映到驗證邏輯，導致 token 驗證用舊金鑰、簽發用新金鑰，整批測試會出現難以追查的 `401`。解法是讓 `TokenValidationParameters` 透過 `AddOptions<JwtBearerOptions>().Configure<IOptions<AppSettings>>(...)` 在 **runtime** 才解析設定值。

### 為何上傳/刪除文件用同步處理而非背景佇列？

跟 Python 版相同的 MVP 取捨：上傳請求會等 ingestion（parse → chunk → embed → upsert）完成才回應，不用 `BackgroundTasks` 或訊息佇列。優點是錯誤處理直觀（失敗狀態能立即反映在回應中），缺點是大檔案會讓使用者等較久——這是已知的下一步優化方向。

---

## 跟 Python 原版的差異對照

| 項目 | Python 版 | C# 版 | 原因 |
| --- | --- | --- | --- |
| 框架 | FastAPI | ASP.NET Core (.NET 8 LTS) | 8 是偶數版號，LTS 支援到 2026/11 |
| 向量庫 | ChromaDB（嵌入式） | Qdrant（獨立服務，gRPC） | ChromaDB 沒有官方 C# SDK |
| Embedding | sentence-transformers（透過 ChromaDB） | ONNX Runtime 直接操作（同一顆 all-MiniLM-L6-v2） | 保持兩版向量行為一致，且面試可解釋完整推論流程 |
| 關聯式 DB | SQLite + SQLAlchemy | SQLite + Entity Framework Core | EF Core 是 .NET 標準 ORM，搭配 Migrations 管理 schema |
| 密碼雜湊 | passlib（bcrypt，自動管理 salt） | Konscious.Argon2id（手動管理 salt） | ASP.NET 生態常見做法 |
| 單元測試 | pytest + mock/patch | xUnit + Moq | mock 外部依賴，測商業邏輯 |
| 整合測試 | pytest 直接打 TestClient | xUnit + Testcontainers（真實啟動 Qdrant） | 驗證跟真實服務的互動 |
| 登入請求格式 | OAuth2PasswordRequestForm（form data） | JSON body | 各自生態系的慣用做法 |
| 容器化 | Docker Compose（單一容器） | Docker Compose（API + Qdrant 兩個服務） | 向量庫獨立成服務 |

更完整的技術選型表與決策脈絡見 [`CLAUDE.md`](./CLAUDE.md)。

---

## Ingestion Pipeline

```
上傳檔案
  → MD5 去重（同用戶同檔案不重複索引）
  → PdfPig 解析 PDF / 純文字讀取 TXT
  → 段落分塊（空白行 / 中文序號標題觸發切段，單一 chunk 上限 1000 字）
  → ONNX Runtime 批次 Embedding（all-MiniLM-L6-v2，384 維）
  → Qdrant Upsert（payload: user_id / doc_id / chunk_index / filename / text）
  → 更新 Document.Status：Pending → Processing → Indexed（或 Failed）
```

---

## 專案結構

```
RAG-Service-Csharp/
├── src/RagService.Api/
│   ├── Program.cs                 # 應用程式組裝：DI、JWT、CORS、Middleware pipeline
│   ├── Controllers/                # auth / docs / chat
│   ├── DTOs/                       # Request/Response record（Auth / Documents / Chat）
│   ├── Models/                     # EF Core entity：User、Document
│   ├── Data/                       # AppDbContext
│   ├── Migrations/                  # EF Core schema 版本歷史
│   ├── Services/                    # AuthService / EmbeddingService / IngestionService
│   │                                # / LlmService / QdrantService / RetrievalService
│   └── Settings/                    # AppSettings（綁定 appsettings.json）
├── tests/
│   ├── RagService.Tests.Unit/        # xUnit + Moq（54 個測試）
│   └── RagService.Tests.Integration/ # xUnit + Testcontainers（真實 Qdrant）
├── Dockerfile                       # multi-stage build（SDK → ASP.NET runtime）
├── docker-compose.yml                # API + Qdrant
├── RagService.sln
└── CLAUDE.md                         # 技術選型表、開發規則、進度追蹤
```

---

## 設定（appsettings.json / 環境變數）

| 設定鍵 | 預設值 | 說明 |
| --- | --- | --- |
| `AppSettings__SecretKey` | （必填，≥32 字元） | JWT 簽名金鑰，啟動時 fail-fast 驗證，`openssl rand -hex 32` |
| `AppSettings__AnthropicApiKey` | （必填） | Anthropic API 金鑰 |
| `AppSettings__DatabasePath` | `./rag.db` | SQLite 連線路徑 |
| `AppSettings__AccessTokenExpireMinutes` | `60` | JWT 過期時間（分鐘） |
| `AppSettings__UploadDir` | `./uploads` | 文件上傳儲存目錄 |
| `AppSettings__TopK` | `5` | 向量搜尋回傳最大筆數 |
| `AppSettings__QdrantHost` / `QdrantPort` | `qdrant` / `6334` | Qdrant gRPC 連線設定（Docker 內部用服務名） |
| `AppSettings__EmbeddingModelPath` | `./models/all-MiniLM-L6-v2.onnx` | 需手動下載放置 |
| `AppSettings__EmbeddingDimension` | `384` | all-MiniLM-L6-v2 固定維度 |
| `AppSettings__AllowedOrigins` | `localhost:3000` / `:5173` | CORS 允許來源 |

環境變數覆寫格式使用雙底線代表巢狀鍵，例如 `AppSettings__SecretKey=xxx`。

---

## 相關專案

- [show51848/RAG-Service](https://github.com/show51848/RAG-Service) — Python / FastAPI 原版，本專案的對照基準
