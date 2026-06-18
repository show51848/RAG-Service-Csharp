using Microsoft.Extensions.Options;
using Qdrant.Client;
using Qdrant.Client.Grpc;
using RagService.Api.DTOs.Chat;
using RagService.Api.Settings;

namespace RagService.Api.Services;

// 對應 Python 版 app/vectorstore/chroma_client.py
// Python 用 ChromaDB（無官方 C# SDK）；C# 換用 Qdrant（有官方 .NET SDK）
// 資料隔離邏輯相同：所有 chunk 存同一個 collection，用 user_id metadata 過濾

public interface IQdrantService
{
    Task EnsureCollectionAsync();
    Task UpsertChunksAsync(List<string> chunks, List<float[]> embeddings, int docId, int userId, string filename);
    Task<List<QdrantSearchResult>> SearchAsync(float[] queryEmbedding, int userId, List<int>? docIds, int topK);
    Task DeleteByDocIdAsync(int docId);
}

public record QdrantSearchResult(string Text, int DocId, string Filename, int ChunkIndex, float Score);

public class QdrantService : IQdrantService
{
    private const string CollectionName = "documents";

    private readonly QdrantClient _client;
    private readonly int _embeddingDim;

    public QdrantService(IOptions<AppSettings> opts)
    {
        var s = opts.Value;
        // 對應 Python 的 chromadb.PersistentClient(path=...)
        // Qdrant 是獨立服務，C# SDK 透過 gRPC 連線
        _client = new QdrantClient(s.QdrantHost, s.QdrantPort);
        _embeddingDim = s.EmbeddingDimension;
    }

    public async Task EnsureCollectionAsync()
    {
        // get_or_create_collection 對應邏輯：先檢查是否存在，不存在才建立
        var exists = await _client.CollectionExistsAsync(CollectionName);
        if (!exists)
        {
            await _client.CreateCollectionAsync(
                CollectionName,
                new VectorParams
                {
                    Size = (ulong)_embeddingDim,
                    Distance = Distance.Cosine,  // 對應 Python 版的 hnsw:space=cosine
                }
            );
        }
    }

    public async Task UpsertChunksAsync(
        List<string> chunks,
        List<float[]> embeddings,
        int docId,
        int userId,
        string filename)
    {
        // 對應 Python 版的 upsert_chunks()
        // ID 格式 doc{docId}_chunk{i} → 轉為數字：docId * 1_000_000 + chunkIndex
        // Qdrant Point ID 只支援 uint64 或 UUID；這裡用數字組合確保唯一性
        var points = chunks.Select((chunk, i) => new PointStruct
        {
            Id = (ulong)(docId * 1_000_000 + i),
            Vectors = embeddings[i],  // implicit float[] → Vectors conversion
            Payload =
            {
                ["user_id"] = userId,
                ["doc_id"] = docId,
                ["chunk_index"] = i,
                ["filename"] = filename,
                ["text"] = chunk,  // Qdrant 不像 ChromaDB 有獨立的 documents 欄位，存在 payload
            },
        }).ToList();

        await _client.UpsertAsync(CollectionName, points);
    }

    public async Task<List<QdrantSearchResult>> SearchAsync(
        float[] queryEmbedding,
        int userId,
        List<int>? docIds,
        int topK)
    {
        // 對應 Python 版的 query_chunks()，資料隔離邏輯相同
        var mustConditions = new List<Condition>
        {
            // user_id 過濾是資料隔離的關鍵
            new()
            {
                Field = new FieldCondition
                {
                    Key = "user_id",
                    Match = new Match { Integer = userId },
                },
            },
        };

        // 若有指定 doc_ids，加入額外的 doc_id 過濾
        if (docIds is { Count: 1 })
        {
            mustConditions.Add(new Condition
            {
                Field = new FieldCondition
                {
                    Key = "doc_id",
                    Match = new Match { Integer = docIds[0] },
                },
            });
        }
        else if (docIds is { Count: > 1 })
        {
            // 多個 doc_id：用 $in 等價的 Qdrant Filter
            // Qdrant 用 ValuesCount 或 HasId；對 payload int array 需要 Should (OR) 條件
            var docIdConditions = docIds.Select(id => new Condition
            {
                Field = new FieldCondition
                {
                    Key = "doc_id",
                    Match = new Match { Integer = id },
                },
            }).ToList();

            mustConditions.Add(new Condition
            {
                Filter = new Filter { Should = { docIdConditions } },
            });
        }

        var filter = new Filter { Must = { mustConditions } };

        var results = await _client.SearchAsync(
            CollectionName,
            queryEmbedding,
            filter: filter,
            limit: (ulong)topK,
            payloadSelector: new Qdrant.Client.Grpc.WithPayloadSelector { Enable = true }
        );

        return results.Select(r => new QdrantSearchResult(
            Text: r.Payload["text"].StringValue,
            DocId: (int)r.Payload["doc_id"].IntegerValue,
            Filename: r.Payload["filename"].StringValue,
            ChunkIndex: (int)r.Payload["chunk_index"].IntegerValue,
            Score: r.Score
        )).ToList();
    }

    public async Task DeleteByDocIdAsync(int docId)
    {
        // 對應 Python 版的 delete_by_doc_id()
        await _client.DeleteAsync(
            CollectionName,
            filter: new Filter
            {
                Must =
                {
                    new Condition
                    {
                        Field = new FieldCondition
                        {
                            Key = "doc_id",
                            Match = new Match { Integer = docId },
                        },
                    },
                },
            }
        );
    }
}
