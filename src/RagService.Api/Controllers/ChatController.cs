using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RagService.Api.Data;
using RagService.Api.DTOs.Chat;
using RagService.Api.Models;
using RagService.Api.Services;

namespace RagService.Api.Controllers;

// 對應 Python 版 app/routers/chat.py
// RAG 完整查詢流程：驗證 doc_ids 所有權 → Retrieval → Generation

[ApiController]
[Route("chat")]
[Authorize]
public class ChatController(
    AppDbContext db,
    IRetrievalService retrievalService,
    ILlmService llmService
) : ControllerBase
{
    private int CurrentUserId =>
        int.Parse(User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
            ?? User.FindFirst("sub")?.Value
            ?? throw new UnauthorizedAccessException());

    [HttpPost("query")]
    [ProducesResponseType<QueryResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Query([FromBody] QueryRequest request)
    {
        var userId = CurrentUserId;

        // 若有指定 doc_ids，驗證它們確實屬於當前使用者
        // 在 Controller 層做這個驗證，而非在 RetrievalService，
        // 原因：RetrievalService 只做向量搜尋，不應依賴 EF Core Session（保持服務層純粹）
        if (request.DocIds is { Count: > 0 })
        {
            var ownedIds = await db.Documents
                .Where(d => request.DocIds.Contains(d.Id) && d.UserId == userId)
                .Select(d => d.Id)
                .ToListAsync();

            var missing = request.DocIds.Except(ownedIds).FirstOrDefault();
            if (missing != 0 && !ownedIds.Contains(missing))
                // 僅當真的有 missing 才回 404
                if (request.DocIds.Except(ownedIds).Any())
                    return NotFound(new { detail = $"Document {request.DocIds.Except(ownedIds).First()} not found" });
        }

        // Retrieval：問題向量化 + 向量搜尋
        var citations = await retrievalService.RetrieveAsync(userId, request.Question, request.DocIds);

        // Generation：組 prompt + 呼叫 LLM
        var (answer, usedCitations) = await llmService.GenerateAnswerAsync(request.Question, citations);

        return Ok(new QueryResponse(answer, usedCitations));
    }
}
