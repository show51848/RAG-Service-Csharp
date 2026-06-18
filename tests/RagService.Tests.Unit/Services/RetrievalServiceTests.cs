using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using RagService.Api.Services;
using RagService.Api.Settings;

namespace RagService.Tests.Unit.Services;

// RetrievalService 測試：驗證 RAG 的 R（Retrieval）邏輯
// 核心邏輯：每個 doc_id 只保留分數最高的 chunk（避免同一文件占滿 LLM context）

public class RetrievalServiceTests
{
    private readonly Mock<IEmbeddingService> _embeddingMock = new();
    private readonly Mock<IQdrantService> _qdrantMock = new();

    private RetrievalService CreateSut() => new(
        _embeddingMock.Object,
        _qdrantMock.Object,
        Options.Create(new AppSettings { TopK = 5 }),
        NullLogger<RetrievalService>.Instance
    );

    [Fact]
    public async Task RetrieveAsync_CallsEmbedTextWithQuestion()
    {
        // 驗證問題文字確實被向量化（而非拿問題直接搜尋）
        _embeddingMock.Setup(e => e.EmbedText("什麼是RAG？")).Returns(new float[384]);
        _qdrantMock.Setup(q => q.SearchAsync(It.IsAny<float[]>(), It.IsAny<int>(),
                It.IsAny<List<int>?>(), It.IsAny<int>()))
            .ReturnsAsync([]);

        var sut = CreateSut();
        await sut.RetrieveAsync(userId: 1, question: "什麼是RAG？", docIds: null);

        _embeddingMock.Verify(e => e.EmbedText("什麼是RAG？"), Times.Once);
    }

    [Fact]
    public async Task RetrieveAsync_DeduplicatesByDocId_KeepsHighestScore()
    {
        // 核心業務邏輯測試：同一份文件的多個 chunk，只保留分數最高的那個
        // 這確保 LLM 收到的 context 不會被同一份文件占滿

        _embeddingMock.Setup(e => e.EmbedText(It.IsAny<string>())).Returns(new float[384]);
        _qdrantMock.Setup(q => q.SearchAsync(It.IsAny<float[]>(), It.IsAny<int>(),
                It.IsAny<List<int>?>(), It.IsAny<int>()))
            .ReturnsAsync([
                new QdrantSearchResult("低分 chunk", DocId: 1, "doc1.txt", ChunkIndex: 0, Score: 0.6f),
                new QdrantSearchResult("高分 chunk", DocId: 1, "doc1.txt", ChunkIndex: 1, Score: 0.9f),
                new QdrantSearchResult("另一份文件", DocId: 2, "doc2.txt", ChunkIndex: 0, Score: 0.7f),
            ]);

        var sut = CreateSut();
        var results = await sut.RetrieveAsync(userId: 1, question: "問題", docIds: null);

        // 兩份文件各自回傳一個 chunk
        results.Should().HaveCount(2);

        // doc_id=1 應該選到分數 0.9 的 chunk
        var docOneResult = results.First(c => c.DocId == 1);
        docOneResult.Chunk.Should().Be("高分 chunk");
        docOneResult.Score.Should().Be(0.9f);
    }

    [Fact]
    public async Task RetrieveAsync_SortsByScoreDescending()
    {
        // 回傳結果應按相似度從高到低排序，讓 LLM 先看到最相關的資料
        _embeddingMock.Setup(e => e.EmbedText(It.IsAny<string>())).Returns(new float[384]);
        _qdrantMock.Setup(q => q.SearchAsync(It.IsAny<float[]>(), It.IsAny<int>(),
                It.IsAny<List<int>?>(), It.IsAny<int>()))
            .ReturnsAsync([
                new QdrantSearchResult("doc A", DocId: 1, "a.txt", 0, Score: 0.5f),
                new QdrantSearchResult("doc B", DocId: 2, "b.txt", 0, Score: 0.9f),
                new QdrantSearchResult("doc C", DocId: 3, "c.txt", 0, Score: 0.7f),
            ]);

        var sut = CreateSut();
        var results = await sut.RetrieveAsync(userId: 1, question: "問題", docIds: null);

        results.Should().BeInDescendingOrder(c => c.Score);
    }

    [Fact]
    public async Task RetrieveAsync_PassesUserIdToQdrant()
    {
        // 驗證 user_id 確實傳給 Qdrant 做資料隔離過濾
        const int userId = 42;
        _embeddingMock.Setup(e => e.EmbedText(It.IsAny<string>())).Returns(new float[384]);
        _qdrantMock.Setup(q => q.SearchAsync(It.IsAny<float[]>(), userId,
                It.IsAny<List<int>?>(), It.IsAny<int>()))
            .ReturnsAsync([]);

        var sut = CreateSut();
        await sut.RetrieveAsync(userId: userId, question: "問題", docIds: null);

        _qdrantMock.Verify(q => q.SearchAsync(
            It.IsAny<float[]>(), userId, It.IsAny<List<int>?>(), It.IsAny<int>()), Times.Once);
    }

    [Fact]
    public async Task RetrieveAsync_EmptyResults_ReturnsEmptyList()
    {
        // 沒有相關文件時應回傳空 list（不拋例外）
        _embeddingMock.Setup(e => e.EmbedText(It.IsAny<string>())).Returns(new float[384]);
        _qdrantMock.Setup(q => q.SearchAsync(It.IsAny<float[]>(), It.IsAny<int>(),
                It.IsAny<List<int>?>(), It.IsAny<int>()))
            .ReturnsAsync([]);

        var sut = CreateSut();
        var results = await sut.RetrieveAsync(userId: 1, question: "問題", docIds: null);

        results.Should().BeEmpty();
    }

    [Fact]
    public async Task RetrieveAsync_UsesSettingsTopK_WhenTopKNotProvided()
    {
        // 驗證 top_k 預設值從 AppSettings 取，而非寫死在程式碼裡
        _embeddingMock.Setup(e => e.EmbedText(It.IsAny<string>())).Returns(new float[384]);
        _qdrantMock.Setup(q => q.SearchAsync(
                It.IsAny<float[]>(), It.IsAny<int>(),
                It.IsAny<List<int>?>(),
                5))  // 驗證傳入的 topK = 5（settings 預設值）
            .ReturnsAsync([]);

        var sut = CreateSut();
        await sut.RetrieveAsync(userId: 1, question: "問題", docIds: null, topK: null);

        _qdrantMock.Verify(q => q.SearchAsync(
            It.IsAny<float[]>(), It.IsAny<int>(), It.IsAny<List<int>?>(), 5), Times.Once);
    }
}
