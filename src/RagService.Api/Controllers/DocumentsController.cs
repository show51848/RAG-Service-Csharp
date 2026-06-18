using System.Security.Cryptography;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RagService.Api.Data;
using RagService.Api.DTOs.Documents;
using RagService.Api.Models;
using RagService.Api.Services;
using RagService.Api.Settings;

namespace RagService.Api.Controllers;

// 對應 Python 版 app/routers/docs.py
// [Authorize] 對應 Python 的 Depends(get_current_user)
// C# 從 JWT claims 取 user_id；Python 從 dependency injection 取 current_user 物件

[ApiController]
[Route("docs")]
[Authorize]
public class DocumentsController(
    AppDbContext db,
    IIngestionService ingestionService,
    IQdrantService qdrantService,
    IOptions<AppSettings> opts
) : ControllerBase
{
    private static readonly HashSet<string> AllowedExtensions = [".pdf", ".txt"];
    private const long MaxUploadBytes = 50 * 1024 * 1024;  // 50 MB

    private int CurrentUserId =>
        int.Parse(User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
            ?? User.FindFirst("sub")?.Value
            ?? throw new UnauthorizedAccessException());

    [HttpPost("upload")]
    [ProducesResponseType<UploadResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status413RequestEntityTooLarge)]
    [ProducesResponseType(StatusCodes.Status415UnsupportedMediaType)]
    public async Task<IActionResult> Upload(IFormFile file)
    {
        var originalName = file.FileName;
        var ext = Path.GetExtension(originalName).ToLowerInvariant();

        if (!AllowedExtensions.Contains(ext))
            return StatusCode(415, new { detail = $"File type '{ext}' is not supported. Allowed: .pdf, .txt" });

        if (file.Length > MaxUploadBytes)
            return StatusCode(413, new { detail = "File too large. Maximum allowed size is 50 MB." });

        // 一次讀入記憶體，計算 hash（對應 Python 版讀 content = file.file.read()）
        using var ms = new MemoryStream();
        await file.CopyToAsync(ms);
        var content = ms.ToArray();

        var fileHash = Convert.ToHexString(MD5.HashData(content)).ToLowerInvariant();
        var contentHash = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

        var userId = CurrentUserId;

        // 去重：同一使用者上傳相同檔案（MD5 相同且已成功索引）
        var existing = await db.Documents.FirstOrDefaultAsync(d =>
            d.UserId == userId &&
            d.FileHash == fileHash &&
            d.Status == DocumentStatus.Indexed);

        if (existing is not null)
            return Conflict(new { detail = "文件已存在", doc_id = existing.Id });

        // 每個使用者的檔案存在獨立子目錄
        var userDir = Path.Combine(opts.Value.UploadDir, userId.ToString());
        Directory.CreateDirectory(userDir);

        var uniqueFilename = $"{Guid.NewGuid():N}_{originalName}";
        var filePath = Path.Combine(userDir, uniqueFilename);
        await System.IO.File.WriteAllBytesAsync(filePath, content);

        var doc = new Document
        {
            UserId = userId,
            Filename = uniqueFilename,
            OriginalName = originalName,
            FilePath = filePath,
            ContentHash = contentHash,
            FileHash = fileHash,
            Status = DocumentStatus.Pending,
        };

        db.Documents.Add(doc);
        await db.SaveChangesAsync();

        // 同步執行 ingestion（對應 Python 版 MVP 的同步做法，不用 BackgroundTasks/Celery）
        await ingestionService.IngestAsync(doc.Id, db);
        await db.Entry(doc).ReloadAsync();  // 重新讀取 ingest 後更新的狀態

        return CreatedAtAction(
            nameof(Upload),
            new UploadResponse(doc.Id, doc.OriginalName, doc.Status)
        );
    }

    [HttpGet]
    [ProducesResponseType<List<DocumentOut>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List()
    {
        var userId = CurrentUserId;
        var docs = await db.Documents
            .Where(d => d.UserId == userId)
            .OrderByDescending(d => d.CreatedAt)
            .ToListAsync();

        return Ok(docs.Select(DocumentOut.FromModel).ToList());
    }

    [HttpDelete("{docId:int}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(int docId)
    {
        var userId = CurrentUserId;
        var doc = await db.Documents.FindAsync(docId);

        // 同時檢查存在且屬於當前使用者，兩種失敗都回 404
        // 防止攻擊者得知哪些 ID 存在（對應 Python 版相同的設計決策）
        if (doc is null || doc.UserId != userId)
            return NotFound(new { detail = $"Document {docId} not found" });

        // 刪除順序：先刪向量 → 再刪磁碟檔案 → 最後刪 DB 記錄
        // 若先刪 DB 後中途失敗，會留下孤兒向量資料
        await qdrantService.DeleteByDocIdAsync(docId);

        if (System.IO.File.Exists(doc.FilePath))
            System.IO.File.Delete(doc.FilePath);

        db.Documents.Remove(doc);
        await db.SaveChangesAsync();

        return NoContent();
    }
}
