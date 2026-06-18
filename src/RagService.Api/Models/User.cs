using System.ComponentModel.DataAnnotations;

namespace RagService.Api.Models;

// 對應 Python 版 app/models/user.py 的 User class
// Python 用 SQLAlchemy Mapped[] 宣告欄位；C# EF Core 用一般 C# 屬性 + DataAnnotations
// 差異：Argon2 需要 salt，所以多一個 PasswordSalt 欄位
public class User
{
    public int Id { get; set; }

    [Required, MaxLength(100)]
    public string Username { get; set; } = "";

    [Required, MaxLength(255)]
    public string Email { get; set; } = "";

    // 只儲存雜湊值，永遠不儲存明文
    [Required]
    public string HashedPassword { get; set; } = "";

    // Argon2 每次雜湊需要 salt；Python 版 passlib 會自動把 salt 嵌在雜湊字串裡
    // C# Konscious.Argon2 需要我們自己管理 salt，所以獨立存一欄
    [Required]
    public string PasswordSalt { get; set; } = "";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public List<Document> Documents { get; set; } = [];
}
