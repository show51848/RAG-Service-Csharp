using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using RagService.Api.DTOs.Chat;
using RagService.Api.DTOs.Documents;
using RagService.Tests.Integration.Helpers;
using RagService.Tests.Integration.Stubs;

namespace RagService.Tests.Integration;

// 查詢 API 的 E2E 整合測試
// 驗證完整 RAG pipeline：HTTP → Controller → Retrieval → Qdrant.Search → StubLLM → Response
// StubEmbeddingService 讓所有文字產生相同向量，StubLlmService 回傳固定答案

public class ChatIntegrationTests(RagServiceFactory factory)
    : IClassFixture<RagServiceFactory>
{
    private HttpClient NewAuthClient() => factory.CreateClient();

    // 輔助方法：上傳文件並回傳 doc_id
    private async Task<int> UploadDocumentAsync(HttpClient client, string content, string filename = "test.txt")
    {
        using var formContent = new MultipartFormDataContent();
        var fc = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(content));
        fc.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain");
        formContent.Add(fc, "file", filename);

        var resp = await client.PostAsync("/docs/upload", formContent);
        var uploaded = await resp.Content.ReadFromJsonAsync<UploadResponse>();
        return uploaded!.DocId;
    }

    // ── /chat/query ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Query_AfterUpload_ReturnsAnswerAndCitations()
    {
        // 完整 RAG pipeline 測試：
        // 1. 上傳文件 → Qdrant 存入向量
        // 2. 查詢 → 向量搜尋 → 找到相關 chunk → StubLLM 回答
        var client = NewAuthClient();
        var token = await TestAuthHelper.RegisterAndGetTokenAsync(client);
        client.WithToken(token);

        await UploadDocumentAsync(client, "RAG 是一種結合向量搜尋和大型語言模型的技術。\n\n這個技術可以用來回答關於文件的問題。");

        var response = await client.PostAsJsonAsync("/chat/query", new
        {
            question = "什麼是 RAG？"
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<QueryResponse>();
        body!.Answer.Should().Be(StubLlmService.FixedAnswer);
        body.Citations.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Query_WithSpecificDocId_OnlySearchesThatDocument()
    {
        var client = NewAuthClient();
        var token = await TestAuthHelper.RegisterAndGetTokenAsync(client);
        client.WithToken(token);

        var docId1 = await UploadDocumentAsync(client, "文件一的內容", "doc1.txt");
        var docId2 = await UploadDocumentAsync(client, "文件二的內容", "doc2.txt");

        // 指定只查詢 docId1
        var response = await client.PostAsJsonAsync("/chat/query", new
        {
            question = "問題",
            docIds = new[] { docId1 }
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<QueryResponse>();

        // 回傳的 citations 應該只包含 docId1 的文件
        if (body!.Citations.Count > 0)
            body.Citations.Should().OnlyContain(c => c.DocId == docId1);
    }

    [Fact]
    public async Task Query_WithOtherUsersDocId_Returns404()
    {
        // 使用者不能查詢別人的文件
        var client1 = NewAuthClient();
        var token1 = await TestAuthHelper.RegisterAndGetTokenAsync(client1);
        client1.WithToken(token1);
        var docId = await UploadDocumentAsync(client1, "user1 的私人文件");

        var client2 = NewAuthClient();
        var token2 = await TestAuthHelper.RegisterAndGetTokenAsync(client2);
        client2.WithToken(token2);

        var response = await client2.PostAsJsonAsync("/chat/query", new
        {
            question = "問題",
            docIds = new[] { docId }
        });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Query_WithoutUploadedDocs_ReturnsEmptyCitations()
    {
        // 沒有文件時，citations 應為空，answer 應包含說明訊息
        var client = NewAuthClient();
        var token = await TestAuthHelper.RegisterAndGetTokenAsync(client);
        client.WithToken(token);

        var response = await client.PostAsJsonAsync("/chat/query", new
        {
            question = "沒有文件的問題"
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<QueryResponse>();
        // 沒有文件 → RetrievalService 回傳空 list → LlmService 回傳 no-doc 訊息
        body!.Citations.Should().BeEmpty();
    }

    [Fact]
    public async Task Query_WithoutToken_Returns401()
    {
        var client = NewAuthClient();  // 不帶 token

        var response = await client.PostAsJsonAsync("/chat/query", new
        {
            question = "未認證的查詢"
        });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
