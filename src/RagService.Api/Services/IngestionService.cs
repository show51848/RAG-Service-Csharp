using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using RagService.Api.Data;
using RagService.Api.Models;
using UglyToad.PdfPig;

namespace RagService.Api.Services;

// 對應 Python 版 app/services/ingestion_service.py
// 邏輯完全對應：parse → chunk → embed → upsert → update status

public interface IIngestionService
{
    string ParseFile(string filePath);
    List<string> ChunkText(string text);
    Task IngestAsync(int docId, AppDbContext db);
}

public partial class IngestionService(
    IEmbeddingService embeddingService,
    IQdrantService qdrantService,
    ILogger<IngestionService> logger
) : IIngestionService
{
    private const int MaxChunkChars = 1000;

    // [GeneratedRegex] 是 C# source generator，編譯期就生成最佳化的 regex 程式碼
    // 對應 Python 版的 re.compile() 預編譯，原理相同，只是 C# 在編譯期做
    [GeneratedRegex(@"^[一二三四五六七八九十百]+[、．.]")]
    private static partial Regex ChineseHeadingRegex();

    public string ParseFile(string filePath)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        logger.LogInformation("Parsing file: {FilePath} (ext={Ext})", filePath, ext);

        return ext switch
        {
            // PdfPig 對應 Python 版的 PyMuPDF (fitz)，都是本機 PDF 解析，不需要外部服務
            ".pdf" => ParsePdf(filePath),
            ".txt" => File.ReadAllText(filePath, System.Text.Encoding.UTF8),
            _ => throw new InvalidOperationException($"Unsupported file type: {ext}"),
        };
    }

    private static string ParsePdf(string filePath)
    {
        using var document = PdfDocument.Open(filePath);
        var sb = new StringBuilder();
        foreach (var page in document.GetPages())
        {
            // GetWords() 按閱讀順序回傳每個字詞，比逐字母組合更符合自然段落
            // 對應 Python PyMuPDF 的 page.get_text()
            var pageText = string.Join(" ", page.GetWords().Select(w => w.Text));
            sb.AppendLine(pageText);
        }
        return sb.ToString();
    }

    public List<string> ChunkText(string text)
    {
        // 對應 Python 版的 chunk_text()，邏輯完全相同：
        // 1. 空白行觸發切段
        // 2. 中文序號標題觸發切段（即使前面沒有空白行）
        // 3. 單一 chunk 超過 MaxChunkChars 時截斷

        if (string.IsNullOrWhiteSpace(text)) return [];

        var lines = text.Split('\n');
        var chunks = new List<string>();
        var currentLines = new List<string>();

        void Flush()
        {
            var block = string.Join("\n", currentLines).Trim();
            if (!string.IsNullOrEmpty(block))
            {
                if (block.Length > MaxChunkChars)
                    block = block[..MaxChunkChars];
                chunks.Add(block);
            }
            currentLines.Clear();
        }

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                Flush();
                continue;
            }

            if (ChineseHeadingRegex().IsMatch(line.Trim()))
            {
                Flush();
                currentLines.Add(line);
                continue;
            }

            currentLines.Add(line);
        }

        Flush();  // 處理最後一段

        logger.LogInformation("Created {Count} chunks (paragraph-based)", chunks.Count);
        return chunks;
    }

    public async Task IngestAsync(int docId, AppDbContext db)
    {
        // 對應 Python 版的 ingest()，狀態機邏輯相同：
        // pending → processing → indexed (或 failed)
        var doc = await db.Documents.FindAsync(docId);
        if (doc is null)
        {
            logger.LogError("Document id={DocId} not found", docId);
            return;
        }

        logger.LogInformation("Starting ingestion for doc_id={DocId} ({Name})", docId, doc.OriginalName);

        doc.Status = DocumentStatus.Processing;
        doc.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        try
        {
            var text = ParseFile(doc.FilePath);
            var chunks = ChunkText(text);

            if (chunks.Count == 0)
                throw new InvalidOperationException("No text content could be extracted from the file");

            // 批次 embed 再 upsert，對應 Python 版的 embed_and_index()
            // 批次處理比逐個呼叫快，ONNX batch 可充分利用 SIMD
            var embeddings = embeddingService.EmbedTexts(chunks);
            await qdrantService.UpsertChunksAsync(chunks, embeddings, docId, doc.UserId, doc.OriginalName);

            doc.Status = DocumentStatus.Indexed;
            doc.ChunkCount = chunks.Count;
            doc.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();

            logger.LogInformation("Ingestion complete for doc_id={DocId}: {Count} chunks indexed", docId, chunks.Count);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Ingestion failed for doc_id={DocId}", docId);
            doc.Status = DocumentStatus.Failed;
            doc.ErrorMessage = ex.Message;
            doc.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }
    }
}
