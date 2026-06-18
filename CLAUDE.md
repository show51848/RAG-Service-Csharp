# RAG-Service C# 版本（ASP.NET Core 重建專案）

## 專案目標

把現有的 Python RAG-Service（FastAPI + ChromaDB，repo: `show51848/RAG-Service`也就是my-todo/rag-service這個資料夾的內容）用 C# / ASP.NET Core 重新實作一遍。

**核心目的不是做出更好的產品，是學習 C# 並能在面試中解釋兩種語言/框架的架構差異。** 所以每個技術決策都要能說出「為什麼這樣選」，不是只求跑得動。

對應的 Python 原版功能：

- 使用者註冊/登入（JWT 認證）
- 文件上傳 → 中文標題感知 chunking（上限 1000 字）→ embedding → 存入向量庫
- 自然語言查詢 → 向量檢索 → 組 prompt → 呼叫 LLM → 回傳 answer + citations
- 多租戶資料隔離（user_id metadata filter）

------

## 技術選型（已確定，除非我明確說要改，不要自行更換）

| 項目         | Python 版                                | C# 版                                                   | 原因                                  |
| ------------ | ---------------------------------------- | ------------------------------------------------------- | ------------------------------------- |
| 框架         | FastAPI                                  | ASP.NET Core (.NET 8 LTS)                               | 8 是偶數版號 = LTS，支援到 2026/11    |
| 向量庫       | ChromaDB                                 | **Qdrant**（Docker + 官方 .NET SDK）                    | ChromaDB 沒有官方 C# SDK              |
| Embedding    | sentence-transformers (all-MiniLM-L6-v2) | **ONNX Runtime** 載入同一顆 all-MiniLM-L6-v2            | 保持兩版向量維度/行為一致，可直接比較 |
| 關聯式 DB    | SQLite + SQLAlchemy                      | SQLite + **Entity Framework Core**                      | EF Core 是 .NET 的標準 ORM            |
| 測試（單元） | pytest + mock/patch                      | xUnit + **Moq**                                         | mock 掉外部依賴，測商業邏輯           |
| 測試（整合） | pytest 直接打 TestClient                 | xUnit + **Testcontainers**（真實啟動 Qdrant container） | 驗證跟真實服務的互動                  |
| 斷言套件     | 無特別套件                               | **FluentAssertions 7.x**（不可用 8.x）                  | 8.x 起改商業授權，7.x 仍是 MIT 免費   |
| 容器化       | Docker Compose                           | Docker Compose（API + Qdrant）                          | 延續同一套部署邏輯                    |
| 認證         | JWT (python-jose)                        | JWT + **Argon2** 密碼雜湊                               | ASP.NET 標準做法                      |

**Qdrant 啟動方式：一律用 Docker，不要建議裝 binary。** 理由：版本鎖定、團隊環境一致、CI 友善、資料用 volume 管理乾淨。

------

## 開發習慣與規則

1. **先規劃再動手**：任何架構級的決定（換套件、改資料夾結構、調整測試策略）先跟我確認，不要自己默默做了再告知。
2. **commit 要有意義的歷史**：每完成一個小功能就 commit 一次，不要一次性塞一個巨大 commit。單一 commit 的 repo 對面試官來說會有「這是不是 AI 一次生成」的觀感問題。
3. **解釋優先於產出**：寫完一段程式碼後，簡短說明這段在做什麼、為什麼這樣寫，特別是跟 Python 版邏輯對應的地方。我需要能在面試中複述這些設計決策。
4. **不要用我不懂的術語不解釋**：我是剛畢業的新手，C# / .NET 生態還在學，遇到 Python 沒有對應概念的東西（例如 DI container、Middleware pipeline）要主動講清楚對應到 Python 的什麼東西，或者老實說「Python 沒有這個概念」。
5. **任務拆解用 TodoWrite 之類的工具列出清單**，讓我隨時知道目前進度跟剩下什麼。

------

## 目前進度 Checklist

> Claude 在新對話開始時可以先檢查這份清單對不對，過期的話幫我更新。

- [x] Solution 骨架建立（API 專案 + 兩個測試專案）
- [x] NuGet 套件安裝（含 FluentAssertions 降版至 7.x）
- [x] Models + DbContext + Migrations（InitialCreate，2026-06-18）
- [x] 設定與環境變數（AppSettings.cs + appsettings.json）
- [x] Auth：JWT + Argon2 密碼雜湊（AuthService + AuthController）
- [x] Embedding Service（ONNX Runtime + BertUncasedBaseTokenizer + mean pooling + L2 norm）
- [x] Qdrant 整合（collection 建立、upsert、search with filter、delete by doc_id）
- [x] 文件上傳 + chunking 邏輯（PDF/TXT parse + 中文標題偵測 + 1000 字上限）
- [x] 查詢 API（檢索 → 組 prompt → 呼叫 LLM → 回傳 citations）
- [x] Docker Compose（API + Qdrant）+ Dockerfile
- [x] 單元測試（37 個測試，xUnit + Moq，2026-06-18）
- [ ] 整合測試（Testcontainers 跑真實 Qdrant）
- [ ] GitHub Actions CI（push 時自動跑測試）
- [ ] README（架構圖、技術選型說明、跟 Python 版的差異對照）

------

## 跟 Python 原版的已知差異點（面試時要能解釋）

- 向量資料庫換成 Qdrant，因為生態相容性（C# 沒有 ChromaDB SDK）
- Embedding 改用 ONNX Runtime 本機推論，而非雲端 API（保持離線、零成本、跟原版行為一致）
- 強型別語言帶來的差異：DTO/Model 需要明確定義 schema，不像 Python 用 dict 那麼隨意