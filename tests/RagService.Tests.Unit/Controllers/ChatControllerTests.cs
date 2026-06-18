using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using RagService.Api.Controllers;
using RagService.Api.Data;
using RagService.Api.DTOs.Chat;
using RagService.Api.Models;
using RagService.Api.Services;
using System.Security.Claims;

namespace RagService.Tests.Unit.Controllers;

// ChatController 測試：驗證 doc_ids 所有權檢查 + RAG pipeline 的串接邏輯
// 為什麼在 Controller 層做所有權驗證（而非 RetrievalService）：
// RetrievalService 只做向量搜尋，不應依賴 DbContext；保持服務層純粹

public class ChatControllerTests
{
    private static AppDbContext CreateInMemoryDb()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }

    private static ChatController CreateController(
        AppDbContext db,
        int userId,
        IRetrievalService? retrieval = null,
        ILlmService? llm = null)
    {
        var controller = new ChatController(
            db,
            retrieval ?? Mock.Of<IRetrievalService>(),
            llm ?? Mock.Of<ILlmService>()
        );

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
                ]))
            }
        };

        return controller;
    }

    // ── POST /chat/query ─────────────────────────────────────────────────────

    [Fact]
    public async Task Query_WithoutDocIds_CallsRetrievalServiceWithNullDocIds()
    {
        // 不指定 doc_ids → 搜尋使用者所有文件
        var retrievalMock = new Mock<IRetrievalService>();
        retrievalMock.Setup(r => r.RetrieveAsync(It.IsAny<int>(), It.IsAny<string>(),
                null, It.IsAny<int?>()))
            .ReturnsAsync([]);

        var llmMock = new Mock<ILlmService>();
        llmMock.Setup(l => l.GenerateAnswerAsync(It.IsAny<string>(), It.IsAny<List<Citation>>()))
            .ReturnsAsync(("回答", []));

        using var db = CreateInMemoryDb();
        var controller = CreateController(db, userId: 1, retrieval: retrievalMock.Object, llm: llmMock.Object);

        var result = await controller.Query(new QueryRequest("什麼是機器學習？"));

        result.Should().BeOfType<OkObjectResult>();
        retrievalMock.Verify(r => r.RetrieveAsync(1, "什麼是機器學習？", null, null), Times.Once);
    }

    [Fact]
    public async Task Query_WithDocIdsOwnedByUser_ReturnsOk()
    {
        using var db = CreateInMemoryDb();
        db.Documents.Add(new Document
        {
            UserId = 1, OriginalName = "doc.txt", Filename = "doc.txt",
            FilePath = "/p", Status = DocumentStatus.Indexed,
        });
        await db.SaveChangesAsync();
        var docId = (await db.Documents.FirstAsync()).Id;

        var retrievalMock = new Mock<IRetrievalService>();
        retrievalMock.Setup(r => r.RetrieveAsync(It.IsAny<int>(), It.IsAny<string>(),
                It.IsAny<List<int>?>(), It.IsAny<int?>()))
            .ReturnsAsync([]);

        var llmMock = new Mock<ILlmService>();
        llmMock.Setup(l => l.GenerateAnswerAsync(It.IsAny<string>(), It.IsAny<List<Citation>>()))
            .ReturnsAsync(("答案", []));

        var controller = CreateController(db, userId: 1, retrieval: retrievalMock.Object, llm: llmMock.Object);

        var result = await controller.Query(new QueryRequest("問題", [docId]));

        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task Query_DocIdBelongsToOtherUser_Returns404()
    {
        // 驗證：使用者不能用別人的文件 ID 進行查詢
        using var db = CreateInMemoryDb();
        db.Documents.Add(new Document
        {
            UserId = 2,  // 屬於 user 2
            OriginalName = "other.txt", Filename = "other.txt",
            FilePath = "/p", Status = DocumentStatus.Indexed,
        });
        await db.SaveChangesAsync();
        var docId = (await db.Documents.FirstAsync()).Id;

        var controller = CreateController(db, userId: 1);  // user 1 嘗試查詢 user 2 的文件

        var result = await controller.Query(new QueryRequest("問題", [docId]));

        result.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task Query_ReturnsAnswerAndCitations()
    {
        // 驗證 Controller 正確地把 LLM 回傳的 answer 和 citations 組成 QueryResponse
        var citations = new List<Citation>
        {
            new(DocId: 1, Filename: "test.txt", Chunk: "相關段落", Score: 0.9f)
        };

        var retrievalMock = new Mock<IRetrievalService>();
        retrievalMock.Setup(r => r.RetrieveAsync(It.IsAny<int>(), It.IsAny<string>(),
                null, It.IsAny<int?>()))
            .ReturnsAsync(citations);

        var llmMock = new Mock<ILlmService>();
        llmMock.Setup(l => l.GenerateAnswerAsync("問題", citations))
            .ReturnsAsync(("根據文件，答案是 X。", citations));

        using var db = CreateInMemoryDb();
        var controller = CreateController(db, userId: 1, retrieval: retrievalMock.Object, llm: llmMock.Object);

        var result = await controller.Query(new QueryRequest("問題"));

        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        var response = okResult.Value.Should().BeOfType<QueryResponse>().Subject;
        response.Answer.Should().Be("根據文件，答案是 X。");
        response.Citations.Should().HaveCount(1);
    }
}
