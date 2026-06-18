using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using RagService.Api.DTOs.Documents;
using RagService.Api.Models;
using RagService.Tests.Integration.Helpers;

namespace RagService.Tests.Integration;

// 文件上傳 / 列表 / 刪除的 E2E 整合測試
// 驗證：HTTP → Controller → IngestionService → Qdrant 的完整 pipeline
// 使用真實 Qdrant（Testcontainers）和真實 SQLite

public class DocumentsIntegrationTests(RagServiceFactory factory)
    : IClassFixture<RagServiceFactory>
{
    // 每個測試建立自己的 HttpClient，避免 JWT token 共用
    private HttpClient NewAuthClient() => factory.CreateClient();

    // ── Upload ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Upload_ValidTxtFile_Returns201WithIndexedStatus()
    {
        // 這個測試驗證整個 ingestion pipeline：
        // HTTP multipart → DocumentsController → IngestionService
        //   → parse text → chunk → StubEmbeddingService → Qdrant.Upsert
        //   → update DB status to Indexed
        var client = NewAuthClient();
        var token = await TestAuthHelper.RegisterAndGetTokenAsync(client);
        client.WithToken(token);

        using var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(
            "這是第一段落的文字內容。\n\n這是第二段落，有一些不同的內容。"u8.ToArray());
        fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain");
        content.Add(fileContent, "file", "test-document.txt");

        var response = await client.PostAsync("/docs/upload", content);

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var body = await response.Content.ReadFromJsonAsync<UploadResponse>();
        body!.DocId.Should().BeGreaterThan(0);
        body.Filename.Should().Be("test-document.txt");
        body.Status.Should().Be(DocumentStatus.Indexed);  // 同步處理，上傳後立即 indexed
    }

    [Fact]
    public async Task Upload_UnsupportedFileType_Returns415()
    {
        var client = NewAuthClient();
        var token = await TestAuthHelper.RegisterAndGetTokenAsync(client);
        client.WithToken(token);

        using var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent("content"u8.ToArray());
        content.Add(fileContent, "file", "script.exe");

        var response = await client.PostAsync("/docs/upload", content);

        response.StatusCode.Should().Be(HttpStatusCode.UnsupportedMediaType);
    }

    [Fact]
    public async Task Upload_DuplicateFile_Returns409()
    {
        var client = NewAuthClient();
        var token = await TestAuthHelper.RegisterAndGetTokenAsync(client);
        client.WithToken(token);

        // 上傳同一份內容兩次，第二次應該回 409
        var fileBytes = "重複文件的內容。"u8.ToArray();

        using var content1 = new MultipartFormDataContent();
        var fc1 = new ByteArrayContent(fileBytes);
        fc1.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain");
        content1.Add(fc1, "file", "doc.txt");
        await client.PostAsync("/docs/upload", content1);

        using var content2 = new MultipartFormDataContent();
        var fc2 = new ByteArrayContent(fileBytes);  // 相同內容 → 相同 MD5
        fc2.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain");
        content2.Add(fc2, "file", "doc.txt");
        var response = await client.PostAsync("/docs/upload", content2);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    // ── List ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task List_AfterUpload_ReturnsUploadedDocument()
    {
        var client = NewAuthClient();
        var token = await TestAuthHelper.RegisterAndGetTokenAsync(client);
        client.WithToken(token);

        // 上傳文件
        using var content = new MultipartFormDataContent();
        var fc = new ByteArrayContent("列表測試文件內容。"u8.ToArray());
        fc.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain");
        content.Add(fc, "file", "list-test.txt");
        await client.PostAsync("/docs/upload", content);

        var response = await client.GetAsync("/docs");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var docs = await response.Content.ReadFromJsonAsync<List<DocumentOut>>();
        docs!.Should().ContainSingle(d => d.OriginalName == "list-test.txt");
    }

    [Fact]
    public async Task List_UserCannotSeeOtherUsersDocuments()
    {
        // 建立兩個不同的使用者
        var client1 = NewAuthClient();
        var token1 = await TestAuthHelper.RegisterAndGetTokenAsync(client1);
        client1.WithToken(token1);

        var client2 = NewAuthClient();
        var token2 = await TestAuthHelper.RegisterAndGetTokenAsync(client2);
        client2.WithToken(token2);

        // user1 上傳文件
        using var content = new MultipartFormDataContent();
        var fc = new ByteArrayContent("使用者隔離測試。"u8.ToArray());
        fc.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain");
        content.Add(fc, "file", "user1-doc.txt");
        await client1.PostAsync("/docs/upload", content);

        // user2 的列表應該是空的（看不到 user1 的文件）
        var response = await client2.GetAsync("/docs");
        var docs = await response.Content.ReadFromJsonAsync<List<DocumentOut>>();
        docs!.Should().BeEmpty();
    }

    // ── Delete ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Delete_OwnDocument_Returns204AndRemovedFromList()
    {
        var client = NewAuthClient();
        var token = await TestAuthHelper.RegisterAndGetTokenAsync(client);
        client.WithToken(token);

        // 先上傳
        using var content = new MultipartFormDataContent();
        var fc = new ByteArrayContent("要被刪除的文件。"u8.ToArray());
        fc.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain");
        content.Add(fc, "file", "to-delete.txt");
        var uploadResp = await client.PostAsync("/docs/upload", content);
        var uploaded = await uploadResp.Content.ReadFromJsonAsync<UploadResponse>();

        // 刪除
        var deleteResp = await client.DeleteAsync($"/docs/{uploaded!.DocId}");
        deleteResp.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // 確認已從列表中消失
        var listResp = await client.GetAsync("/docs");
        var docs = await listResp.Content.ReadFromJsonAsync<List<DocumentOut>>();
        docs!.Should().NotContain(d => d.Id == uploaded.DocId);
    }

    [Fact]
    public async Task Delete_OtherUsersDocument_Returns404()
    {
        // user1 上傳，user2 嘗試刪除
        var client1 = NewAuthClient();
        var token1 = await TestAuthHelper.RegisterAndGetTokenAsync(client1);
        client1.WithToken(token1);

        using var content = new MultipartFormDataContent();
        var fc = new ByteArrayContent("私人文件。"u8.ToArray());
        fc.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain");
        content.Add(fc, "file", "private.txt");
        var uploadResp = await client1.PostAsync("/docs/upload", content);
        var uploaded = await uploadResp.Content.ReadFromJsonAsync<UploadResponse>();

        var client2 = NewAuthClient();
        var token2 = await TestAuthHelper.RegisterAndGetTokenAsync(client2);
        client2.WithToken(token2);

        var deleteResp = await client2.DeleteAsync($"/docs/{uploaded!.DocId}");
        deleteResp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
