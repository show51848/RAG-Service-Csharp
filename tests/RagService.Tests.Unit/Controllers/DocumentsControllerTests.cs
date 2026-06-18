using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Moq;
using RagService.Api.Controllers;
using RagService.Api.Data;
using RagService.Api.DTOs.Documents;
using RagService.Api.Models;
using RagService.Api.Services;
using RagService.Api.Settings;
using System.Security.Claims;

namespace RagService.Tests.Unit.Controllers;

// DocumentsController 測試：驗證上傳去重、刪除所有權驗證等業務邏輯
// Mock：IIngestionService、IQdrantService（外部依賴）
// InMemory EF Core：取代真實 SQLite

public class DocumentsControllerTests
{
    private static AppDbContext CreateInMemoryDb()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }

    // 讓 Controller 知道「目前登入使用者是誰」
    // ASP.NET Core 中，Controller 透過 HttpContext.User.Claims 取得身份資訊
    // 這裡模擬 JWT 認證成功後的 Claims，對應 Python 版的 current_user 物件
    private static DocumentsController CreateController(
        AppDbContext db,
        int userId,
        IIngestionService? ingestion = null,
        IQdrantService? qdrant = null)
    {
        var controller = new DocumentsController(
            db,
            ingestion ?? Mock.Of<IIngestionService>(),
            qdrant ?? Mock.Of<IQdrantService>(),
            Options.Create(new AppSettings { UploadDir = Path.GetTempPath() })
        );

        // 模擬已認證的使用者（模擬 JWT Bearer 驗證通過後的狀態）
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

    // ── DELETE /docs/{docId} ─────────────────────────────────────────────────

    [Fact]
    public async Task Delete_DocumentOwnedByUser_Returns204()
    {
        using var db = CreateInMemoryDb();
        var doc = new Document
        {
            UserId = 1, OriginalName = "test.txt", Filename = "test.txt",
            FilePath = "/nonexistent/path.txt",  // 不存在沒關係，測試不需要實際刪檔
            Status = DocumentStatus.Indexed,
        };
        db.Documents.Add(doc);
        await db.SaveChangesAsync();

        var qdrantMock = new Mock<IQdrantService>();
        qdrantMock.Setup(q => q.DeleteByDocIdAsync(doc.Id)).Returns(Task.CompletedTask);

        var controller = CreateController(db, userId: 1, qdrant: qdrantMock.Object);

        var result = await controller.Delete(doc.Id);

        result.Should().BeOfType<NoContentResult>();
    }

    [Fact]
    public async Task Delete_DocumentBelongsToOtherUser_Returns404()
    {
        // 對應 Python 版「刻意不區分不存在和不屬於你，都回 404」的設計決策
        // 防止攻擊者用 delete 探測哪些 doc ID 存在（enumeration attack）
        using var db = CreateInMemoryDb();
        var doc = new Document
        {
            UserId = 2,  // 文件屬於 user 2
            OriginalName = "other.txt", Filename = "other.txt",
            FilePath = "/path.txt", Status = DocumentStatus.Indexed,
        };
        db.Documents.Add(doc);
        await db.SaveChangesAsync();

        var controller = CreateController(db, userId: 1);  // user 1 嘗試刪除

        var result = await controller.Delete(doc.Id);

        result.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task Delete_NonExistentDocument_Returns404()
    {
        using var db = CreateInMemoryDb();
        var controller = CreateController(db, userId: 1);

        var result = await controller.Delete(docId: 999);

        result.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task Delete_CallsQdrantDeleteBeforeDbDelete()
    {
        // 驗證刪除順序：先刪向量 → 再刪 DB
        // 若順序反過來且中途失敗，就會有孤兒向量資料
        using var db = CreateInMemoryDb();
        var doc = new Document
        {
            UserId = 1, OriginalName = "test.txt", Filename = "test.txt",
            FilePath = "/path.txt", Status = DocumentStatus.Indexed,
        };
        db.Documents.Add(doc);
        await db.SaveChangesAsync();

        var callOrder = new List<string>();
        var qdrantMock = new Mock<IQdrantService>();
        qdrantMock.Setup(q => q.DeleteByDocIdAsync(It.IsAny<int>()))
            .Callback(() => callOrder.Add("qdrant"))
            .Returns(Task.CompletedTask);

        var controller = CreateController(db, userId: 1, qdrant: qdrantMock.Object);
        await controller.Delete(doc.Id);

        // Qdrant 必須在 DB 記錄還存在時就被呼叫
        callOrder.Should().ContainSingle().Which.Should().Be("qdrant");
        // DB 記錄應已被刪除
        (await db.Documents.FindAsync(doc.Id)).Should().BeNull();
    }

    // ── GET /docs ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task List_ReturnsOnlyCurrentUserDocuments()
    {
        // 使用者只能看到自己的文件（user_id 過濾）
        using var db = CreateInMemoryDb();
        db.Documents.AddRange(
            new Document { UserId = 1, OriginalName = "mine.txt", Filename = "mine.txt", FilePath = "/p", Status = DocumentStatus.Indexed },
            new Document { UserId = 2, OriginalName = "theirs.txt", Filename = "theirs.txt", FilePath = "/p2", Status = DocumentStatus.Indexed }
        );
        await db.SaveChangesAsync();

        var controller = CreateController(db, userId: 1);

        var result = await controller.List();

        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        var docs = okResult.Value.Should().BeAssignableTo<List<DocumentOut>>().Subject;
        docs.Should().HaveCount(1);
        docs[0].OriginalName.Should().Be("mine.txt");
    }
}
