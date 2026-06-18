using Microsoft.EntityFrameworkCore;
using RagService.Api.Models;

namespace RagService.Api.Data;

// 對應 Python 版 app/database.py
// Python 用 SQLAlchemy 的 DeclarativeBase + create_tables()
// C# 用 EF Core DbContext + Migrations（更結構化，schema 變更有版本記錄）
//
// Primary constructor syntax（C# 12 新語法，等同於寫一個 constructor(options) : base(options)）
public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Document> Documents => Set<Document>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>(entity =>
        {
            // 對應 Python SQLAlchemy 的 unique=True
            entity.HasIndex(u => u.Username).IsUnique();
            entity.HasIndex(u => u.Email).IsUnique();
        });

        modelBuilder.Entity<Document>(entity =>
        {
            // 對應 Python 的 index=True
            entity.HasIndex(d => d.FileHash);
            entity.HasIndex(d => d.ContentHash);

            // 將 enum 存為字串（Python 版的 str 繼承 Enum 效果）
            // 讓資料庫裡看到 "Indexed" 而非 "2"，更易讀、更安全（不受 enum 順序影響）
            entity.Property(d => d.Status).HasConversion<string>();

            entity.HasOne(d => d.User)
                  .WithMany(u => u.Documents)
                  .HasForeignKey(d => d.UserId)
                  .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
