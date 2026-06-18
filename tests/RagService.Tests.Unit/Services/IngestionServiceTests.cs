using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RagService.Api.Data;
using RagService.Api.Models;
using RagService.Api.Services;

namespace RagService.Tests.Unit.Services;

// IngestionService 有兩類測試：
// 1. ChunkText（純函式，不需要 Mock）
// 2. IngestAsync（狀態機測試，需要 Mock IEmbeddingService + IQdrantService）

public class IngestionServiceTests
{
    // ── 輔助方法：建立 InMemory AppDbContext ─────────────────────────────────
    // InMemory EF Core 取代真實 SQLite，讓每個測試有獨立的乾淨資料庫狀態
    // 對應 Python 版 pytest fixture 的 db session
    private static AppDbContext CreateInMemoryDb()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())  // 每次用不同名稱確保隔離
            .Options;
        return new AppDbContext(options);
    }

    // ── ChunkText 測試（純函式，面試時最常被問到）─────────────────────────────

    private static IngestionService CreateSut(
        IEmbeddingService? embeddingService = null,
        IQdrantService? qdrantService = null)
    {
        return new IngestionService(
            embeddingService ?? Mock.Of<IEmbeddingService>(),
            qdrantService ?? Mock.Of<IQdrantService>(),
            NullLogger<IngestionService>.Instance
        );
    }

    [Fact]
    public void ChunkText_EmptyString_ReturnsEmptyList()
    {
        var sut = CreateSut();
        var result = sut.ChunkText("   ");
        result.Should().BeEmpty();
    }

    [Fact]
    public void ChunkText_SingleParagraph_ReturnsOneChunk()
    {
        var sut = CreateSut();
        var result = sut.ChunkText("這是一段文字，沒有空白行。");
        result.Should().HaveCount(1);
        result[0].Should().Be("這是一段文字，沒有空白行。");
    }

    [Fact]
    public void ChunkText_BlankLineSeparated_ReturnsTwoChunks()
    {
        // 對應 Python 版「空白行觸發切段」邏輯
        var sut = CreateSut();
        var text = "第一段落\n\n第二段落";

        var result = sut.ChunkText(text);

        result.Should().HaveCount(2);
        result[0].Should().Be("第一段落");
        result[1].Should().Be("第二段落");
    }

    [Fact]
    public void ChunkText_ChineseHeading_StartsNewChunk()
    {
        // 對應 Python 版「中文序號標題觸發切段」邏輯
        // 即使前面沒有空白行，遇到「一、二、三...」開頭的行也要切段
        var sut = CreateSut();
        var text = "前言文字\n一、第一節\n節的內容";

        var result = sut.ChunkText(text);

        result.Should().HaveCount(2);
        result[0].Should().Be("前言文字");
        result[1].Should().Contain("一、第一節");
    }

    [Theory]
    [InlineData("一、")]   // 逗號式
    [InlineData("二．")]   // 全形句號式
    [InlineData("三.")]    // 半形句號式
    public void ChunkText_VariousChineseHeadingFormats_SplitsCorrectly(string prefix)
    {
        var sut = CreateSut();
        var text = $"前言\n{prefix}標題";

        var result = sut.ChunkText(text);

        result.Should().HaveCount(2);
    }

    [Fact]
    public void ChunkText_LongChunk_TruncatesAt1000Chars()
    {
        // 對應 Python 版的 MAX_CHUNK_CHARS = 1000 截斷邏輯
        var sut = CreateSut();
        var longText = new string('字', 1500);  // 1500 個中文字

        var result = sut.ChunkText(longText);

        result.Should().HaveCount(1);
        result[0].Length.Should().Be(1000);
    }

    [Fact]
    public void ChunkText_TrailingContentWithoutNewline_IsCaptured()
    {
        // 測試「最後一段沒有空白行結尾」的 edge case
        // 對應 Python 版最後的 flush() 呼叫
        var sut = CreateSut();
        var text = "段落一\n\n最後一段不以換行結束";

        var result = sut.ChunkText(text);

        result.Last().Should().Be("最後一段不以換行結束");
    }

    // ── IngestAsync 狀態機測試 ──────────────────────────────────────────────

    [Fact]
    public async Task IngestAsync_SuccessfulIngestion_SetsStatusToIndexed()
    {
        // 測試完整 ingestion 流程的狀態機：pending → processing → indexed
        var embeddingMock = new Mock<IEmbeddingService>();
        embeddingMock.Setup(e => e.EmbedTexts(It.IsAny<IEnumerable<string>>()))
            .Returns(new List<float[]> { new float[384] });

        var qdrantMock = new Mock<IQdrantService>();
        qdrantMock.Setup(q => q.UpsertChunksAsync(
            It.IsAny<List<string>>(), It.IsAny<List<float[]>>(),
            It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>()))
            .Returns(Task.CompletedTask);

        var sut = CreateSut(embeddingMock.Object, qdrantMock.Object);

        // 建立測試用的臨時 txt 檔
        var tempFile = Path.GetTempFileName() + ".txt";
        await File.WriteAllTextAsync(tempFile, "測試文件內容\n\n第二段落");

        using var db = CreateInMemoryDb();
        var doc = new Document
        {
            UserId = 1,
            OriginalName = "test.txt",
            Filename = "test.txt",
            FilePath = tempFile,
            Status = DocumentStatus.Pending,
        };
        db.Documents.Add(doc);
        await db.SaveChangesAsync();

        await sut.IngestAsync(doc.Id, db);

        // 驗證狀態已更新（重新從 DB 讀取）
        var updated = await db.Documents.FindAsync(doc.Id);
        updated!.Status.Should().Be(DocumentStatus.Indexed);
        updated.ChunkCount.Should().BeGreaterThan(0);
        updated.ErrorMessage.Should().BeNull();

        File.Delete(tempFile);
    }

    [Fact]
    public async Task IngestAsync_EmbeddingFails_SetsStatusToFailed()
    {
        // 測試錯誤處理：任何例外都要讓 status → Failed 並記錄錯誤訊息
        // 對應 Python 版的 try/except 捕捉所有 Exception
        var embeddingMock = new Mock<IEmbeddingService>();
        embeddingMock.Setup(e => e.EmbedTexts(It.IsAny<IEnumerable<string>>()))
            .Throws(new InvalidOperationException("ONNX model not loaded"));

        var sut = CreateSut(embeddingMock.Object);

        var tempFile = Path.GetTempFileName() + ".txt";
        await File.WriteAllTextAsync(tempFile, "文件內容");

        using var db = CreateInMemoryDb();
        var doc = new Document
        {
            UserId = 1, OriginalName = "test.txt", Filename = "test.txt",
            FilePath = tempFile, Status = DocumentStatus.Pending,
        };
        db.Documents.Add(doc);
        await db.SaveChangesAsync();

        await sut.IngestAsync(doc.Id, db);

        var updated = await db.Documents.FindAsync(doc.Id);
        updated!.Status.Should().Be(DocumentStatus.Failed);
        updated.ErrorMessage.Should().Contain("ONNX model not loaded");

        File.Delete(tempFile);
    }

    [Fact]
    public async Task IngestAsync_DocumentNotFound_ReturnsWithoutException()
    {
        // 找不到文件時應靜默回傳，不應拋出例外讓 HTTP 層爆炸
        var sut = CreateSut();
        using var db = CreateInMemoryDb();

        var act = async () => await sut.IngestAsync(docId: 999, db: db);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task IngestAsync_SetsChunkCountCorrectly()
    {
        var embeddingMock = new Mock<IEmbeddingService>();

        // 會傳入 3 個 chunk（測試文件有 3 個段落）
        embeddingMock.Setup(e => e.EmbedTexts(It.IsAny<IEnumerable<string>>()))
            .Returns<IEnumerable<string>>(texts =>
                texts.Select(_ => new float[384]).ToList());

        var qdrantMock = new Mock<IQdrantService>();
        qdrantMock.Setup(q => q.UpsertChunksAsync(
            It.IsAny<List<string>>(), It.IsAny<List<float[]>>(),
            It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>()))
            .Returns(Task.CompletedTask);

        var sut = CreateSut(embeddingMock.Object, qdrantMock.Object);

        // 3 個段落 → chunk_count = 3
        var tempFile = Path.GetTempFileName() + ".txt";
        await File.WriteAllTextAsync(tempFile, "段落一\n\n段落二\n\n段落三");

        using var db = CreateInMemoryDb();
        var doc = new Document
        {
            UserId = 1, OriginalName = "test.txt", Filename = "test.txt",
            FilePath = tempFile, Status = DocumentStatus.Pending,
        };
        db.Documents.Add(doc);
        await db.SaveChangesAsync();

        await sut.IngestAsync(doc.Id, db);

        var updated = await db.Documents.FindAsync(doc.Id);
        updated!.ChunkCount.Should().Be(3);

        File.Delete(tempFile);
    }
}
