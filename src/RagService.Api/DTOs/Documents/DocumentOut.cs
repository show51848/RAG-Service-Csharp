using RagService.Api.Models;

namespace RagService.Api.DTOs.Documents;

// 對應 Python 版 app/schemas/document.py 的 DocumentOut
// Python 用 model_validate(d) 從 ORM 物件轉換；C# 用靜態 factory method FromModel()
public record DocumentOut(
    int Id,
    string OriginalName,
    DocumentStatus Status,
    int ChunkCount,
    string? ErrorMessage,
    DateTime CreatedAt
)
{
    public static DocumentOut FromModel(Document d) => new(
        d.Id,
        d.OriginalName,
        d.Status,
        d.ChunkCount,
        d.ErrorMessage,
        d.CreatedAt
    );
}
