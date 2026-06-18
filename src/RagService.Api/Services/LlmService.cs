using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using RagService.Api.DTOs.Chat;
using RagService.Api.Settings;

namespace RagService.Api.Services;

// 對應 Python 版 app/services/llm_service.py
// Python 用 anthropic SDK；C# 直接用 HttpClient 呼叫 Anthropic REST API
// 為什麼不用第三方 Anthropic C# SDK：目前官方沒有 C# SDK，社群套件版本不穩定

public interface ILlmService
{
    Task<(string Answer, List<Citation> Citations)> GenerateAnswerAsync(string question, List<Citation> citations);
}

public class LlmService(
    IHttpClientFactory httpClientFactory,
    ILogger<LlmService> logger
) : ILlmService
{
    private const string AnthropicApiUrl = "https://api.anthropic.com/v1/messages";
    private const string ModelId = "claude-haiku-4-5-20251001";

    // 對應 Python 版的 SYSTEM_PROMPT，角色設定和指示完全相同
    private const string SystemPrompt =
        "你是一個知識庫助手。根據以下文件片段回答問題。\n" +
        "若資訊不足，請說明無法從提供的文件中找到答案。\n" +
        "請用繁體中文回答。\n" +
        "回答時請引用來源，格式為 [數字]，例如 [1]、[2]。";

    public async Task<(string Answer, List<Citation> Citations)> GenerateAnswerAsync(
        string question,
        List<Citation> citations)
    {
        if (citations.Count == 0)
        {
            // 沒有找到相關文件時直接回傳說明，不呼叫 LLM（節省 API 費用）
            logger.LogInformation("No citations available; returning empty-context answer");
            return (
                "目前沒有找到相關文件片段，無法回答您的問題。\n請先上傳相關文件，或確認問題是否與已上傳的文件相關。",
                []
            );
        }

        // 把 citations 格式化成帶編號的段落，讓 LLM 可以引用 [1]、[2]
        var contextLines = citations
            .Select((c, i) => $"[{i + 1}] 來源：{c.Filename}\n{c.Chunk}");
        var context = string.Join("\n\n---\n\n", contextLines);
        var userMessage = $"文件片段：\n\n{context}\n\n問題：{question}";

        logger.LogInformation("Calling {Model} with {Count} citations", ModelId, citations.Count);

        // 建立 Anthropic API 請求
        var requestBody = new
        {
            model = ModelId,
            max_tokens = 1024,
            system = SystemPrompt,
            messages = new[] { new { role = "user", content = userMessage } },
        };

        var httpClient = httpClientFactory.CreateClient("Anthropic");
        var json = JsonSerializer.Serialize(requestBody);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        using var response = await httpClient.PostAsync(AnthropicApiUrl, content);
        response.EnsureSuccessStatusCode();

        var responseJson = await response.Content.ReadAsStringAsync();
        var apiResponse = JsonSerializer.Deserialize<AnthropicResponse>(responseJson)
            ?? throw new InvalidOperationException("Failed to deserialize Anthropic response");

        var answer = apiResponse.Content
            .FirstOrDefault(c => c.Type == "text")?.Text ?? "";

        logger.LogInformation("LLM answer length: {Length} chars", answer.Length);
        return (answer, citations);
    }
}

// Anthropic API 回應的 JSON 結構
// Python 版用 SDK 物件；C# 用手寫的 record 做 JSON 反序列化
file record AnthropicResponse(
    [property: JsonPropertyName("content")] List<AnthropicContent> Content
);

file record AnthropicContent(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("text")] string? Text
);
