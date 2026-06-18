using RagService.Api.Services;

namespace RagService.Tests.Integration.Stubs;

// 整合測試用的假 EmbeddingService：不需要 ONNX 模型檔案
// 回傳固定的單位向量（所有文字的向量相同），讓 Qdrant 查詢永遠能回傳結果
// 這不是語意上有意義的 embedding，但足以驗證整個 HTTP pipeline 是否正確串接
public class StubEmbeddingService : IEmbeddingService
{
    private const int Dim = 384;

    // 固定向量：第 0 維 = 1.0，其餘 = 0（單位向量，cosine similarity 對自己 = 1.0）
    private static readonly float[] _fixedVector = CreateUnitVector();

    private static float[] CreateUnitVector()
    {
        var v = new float[Dim];
        v[0] = 1.0f;
        return v;
    }

    public float[] EmbedText(string text) => _fixedVector;

    public List<float[]> EmbedTexts(IEnumerable<string> texts)
        => texts.Select(_ => _fixedVector).ToList();
}
