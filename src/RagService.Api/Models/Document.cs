using System.ComponentModel.DataAnnotations;

namespace RagService.Api.Models;

// 對應 Python 版的 DocumentStatus enum（str 繼承讓值是字串）
// C# enum 預設是 int；用 HasConversion<string>() 在 DbContext 設定存字串
public enum DocumentStatus
{
    Pending,
    Processing,
    Indexed,
    Failed
}

// 對應 Python 版 app/models/document.py 的 Document class
public class Document
{
    public int Id { get; set; }

    public int UserId { get; set; }
    public User User { get; set; } = null!;  // null! 告訴編譯器「我保證 EF Core 會填這個」

    // filename：磁碟上的實際檔名（含 uuid 前綴）
    // OriginalName：使用者上傳時的原始檔名
    [Required, MaxLength(500)]
    public string Filename { get; set; } = "";

    [Required, MaxLength(500)]
    public string OriginalName { get; set; } = "";

    [Required]
    public string FilePath { get; set; } = "";

    public DocumentStatus Status { get; set; } = DocumentStatus.Pending;

    // SHA-256，精確內容比對
    [MaxLength(64)]
    public string? ContentHash { get; set; }

    // MD5，快速去重
    [MaxLength(32)]
    public string? FileHash { get; set; }

    public string? ErrorMessage { get; set; }

    public int ChunkCount { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
