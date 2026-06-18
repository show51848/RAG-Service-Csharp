using RagService.Api.DTOs.Chat;
using RagService.Api.Services;

namespace RagService.Tests.Integration.Stubs;

// 整合測試用的假 LlmService：不需要 Anthropic API key
// 固定回傳「測試回答」，讓整合測試可以驗證 citations 有被正確傳遞
public class StubLlmService : ILlmService
{
    public const string FixedAnswer = "這是整合測試的固定回答。";

    public Task<(string Answer, List<Citation> Citations)> GenerateAnswerAsync(
        string question,
        List<Citation> citations)
        => Task.FromResult((FixedAnswer, citations));
}
