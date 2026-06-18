using BERTTokenizers;
using Microsoft.Extensions.Options;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using RagService.Api.Settings;

namespace RagService.Api.Services;

// 對應 Python 版 app/services/embedding_service.py
// Python 版用 ChromaDB 的 DefaultEmbeddingFunction（內部用 ONNX + all-MiniLM-L6-v2）
// C# 版直接操作 ONNX Runtime，更透明，面試時可以解釋完整的推論流程

public interface IEmbeddingService
{
    float[] EmbedText(string text);
    List<float[]> EmbedTexts(IEnumerable<string> texts);
}

public class EmbeddingService : IEmbeddingService, IDisposable
{
    private const int MaxSequenceLength = 128;
    private const int EmbeddingDim = 384;  // all-MiniLM-L6-v2 固定 384 維

    // Lazy<T> 讓 InferenceSession 在第一次 Embed 呼叫時才載入模型
    // 對應 Python 版的模組層級單例 _ef = None 判斷
    private readonly Lazy<(InferenceSession Session, BertUncasedBaseTokenizer Tokenizer)> _model;

    public EmbeddingService(IOptions<AppSettings> opts)
    {
        var settings = opts.Value;
        _model = new Lazy<(InferenceSession, BertUncasedBaseTokenizer)>(() =>
        {
            if (!File.Exists(settings.EmbeddingModelPath))
                throw new FileNotFoundException(
                    $"ONNX model not found at {settings.EmbeddingModelPath}. " +
                    "Download all-MiniLM-L6-v2.onnx from HuggingFace.");

            var session = new InferenceSession(settings.EmbeddingModelPath);
            // BertUncasedBaseTokenizer 用 0-arg constructor，自動讀取 package bundled 的
            // Vocabularies/base_uncased.txt（BERT Base Uncased vocab，30,522 tokens）
            // all-MiniLM-L6-v2 使用相同的 WordPiece vocab，完全相容
            var tokenizer = new BertUncasedBaseTokenizer();
            return (session, tokenizer);
        });
    }

    public float[] EmbedText(string text) => EmbedTexts([text])[0];

    public List<float[]> EmbedTexts(IEnumerable<string> texts)
    {
        var (session, tokenizer) = _model.Value;
        var results = new List<float[]>();

        foreach (var text in texts)
        {
            // Step 1: 分詞（Tokenize）
            // BERTTokenizers.Encode 回傳每個 token 的 (InputIds, TokenTypeIds, AttentionMask)
            var encoded = tokenizer.Encode(MaxSequenceLength, text);

            var inputIds = encoded.Select(e => e.InputIds).ToArray();
            var tokenTypeIds = encoded.Select(e => e.TokenTypeIds).ToArray();
            var attentionMask = encoded.Select(e => e.AttentionMask).ToArray();

            int seqLen = inputIds.Length;

            // Step 2: 建立 ONNX 輸入 tensor（形狀 [1, seqLen]）
            var inputIdsTensor = new DenseTensor<long>(inputIds, [1, seqLen]);
            var tokenTypeTensor = new DenseTensor<long>(tokenTypeIds, [1, seqLen]);
            var attentionTensor = new DenseTensor<long>(attentionMask, [1, seqLen]);

            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor("input_ids", inputIdsTensor),
                NamedOnnxValue.CreateFromTensor("token_type_ids", tokenTypeTensor),
                NamedOnnxValue.CreateFromTensor("attention_mask", attentionTensor),
            };

            // Step 3: 執行推論，取得 last_hidden_state [1, seqLen, 384]
            using var outputs = session.Run(inputs);
            var lastHiddenState = outputs.First().AsTensor<float>();

            // Step 4: Mean Pooling（只對 attention_mask=1 的 token 平均）
            // 對應 Python 版 sentence-transformers 的 mean_pooling 邏輯
            var embedding = new float[EmbeddingDim];
            float tokenCount = 0f;

            for (int t = 0; t < seqLen; t++)
            {
                if (attentionMask[t] != 1) continue;
                tokenCount++;
                for (int d = 0; d < EmbeddingDim; d++)
                    embedding[d] += lastHiddenState[0, t, d];
            }

            for (int d = 0; d < EmbeddingDim; d++)
                embedding[d] /= tokenCount;

            // Step 5: L2 正規化（讓向量長度為 1，適合 cosine 相似度計算）
            var magnitude = MathF.Sqrt(embedding.Sum(x => x * x));
            for (int d = 0; d < EmbeddingDim; d++)
                embedding[d] /= magnitude;

            results.Add(embedding);
        }

        return results;
    }

    public void Dispose()
    {
        if (_model.IsValueCreated)
            _model.Value.Session.Dispose();
        GC.SuppressFinalize(this);
    }
}
