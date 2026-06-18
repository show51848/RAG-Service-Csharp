using Microsoft.Extensions.Options;
using RagService.Api.DTOs.Chat;
using RagService.Api.Settings;

namespace RagService.Api.Services;

// 對應 Python 版 app/services/retrieval_service.py
// RAG 的 R（Retrieval）：問題向量化 → 搜尋最相關 chunk → 去重（每個 doc 只保留最佳 chunk）

public interface IRetrievalService
{
    Task<List<Citation>> RetrieveAsync(int userId, string question, List<int>? docIds, int? topK = null);
}

public class RetrievalService(
    IEmbeddingService embeddingService,
    IQdrantService qdrantService,
    IOptions<AppSettings> opts,
    ILogger<RetrievalService> logger
) : IRetrievalService
{
    private readonly AppSettings _settings = opts.Value;

    public async Task<List<Citation>> RetrieveAsync(int userId, string question, List<int>? docIds, int? topK = null)
    {
        var k = topK ?? _settings.TopK;
        logger.LogInformation("Retrieving top-{K} chunks for user_id={UserId}, doc_ids={DocIds}", k, userId, docIds);

        // 把問題文字轉成向量
        var queryEmbedding = embeddingService.EmbedText(question);

        var rawResults = await qdrantService.SearchAsync(queryEmbedding, userId, docIds, k);

        // 對應 Python 版的去重邏輯：每個 doc_id 只保留分數最高的一個 chunk
        // 原因：避免同一份文件的多個 chunk 占滿 LLM context
        var best = new Dictionary<int, Citation>();
        foreach (var r in rawResults)
        {
            // Qdrant cosine 分數範圍：0（完全相反）到 1（完全相同）
            // Python 版的 ChromaDB 用 distance（0 到 2），需要轉換；Qdrant 直接是 similarity score
            var score = MathF.Round(r.Score, 4);

            if (!best.TryGetValue(r.DocId, out var existing) || score > existing.Score)
                best[r.DocId] = new Citation(r.DocId, r.Filename, r.Text, score);
        }

        var citations = best.Values.OrderByDescending(c => c.Score).ToList();
        logger.LogInformation("Retrieved {Count} citations (after doc_id dedup)", citations.Count);
        return citations;
    }
}
