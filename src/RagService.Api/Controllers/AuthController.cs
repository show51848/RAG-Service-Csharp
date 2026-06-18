using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RagService.Api.Data;
using RagService.Api.DTOs.Auth;
using RagService.Api.Models;
using RagService.Api.Services;

namespace RagService.Api.Controllers;

// 對應 Python 版 app/routers/auth.py
// Python 用 FastAPI router + Depends(get_db)；C# 用 [ApiController] + DI constructor injection
// 核心差異：Python 用 db.query(User).filter(...)；C# 用 LINQ + async EF Core

[ApiController]
[Route("auth")]
public class AuthController(AppDbContext db, IAuthService authService) : ControllerBase
{
    [HttpPost("register")]
    [ProducesResponseType<RegisterResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Register([FromBody] RegisterRequest request)
    {
        // 應用層唯一性檢查：給出具體 409 訊息，比直接讓資料庫 unique constraint 爆炸更友善
        if (await db.Users.AnyAsync(u => u.Username == request.Username))
            return Conflict(new { detail = $"Username '{request.Username}' is already taken" });

        if (await db.Users.AnyAsync(u => u.Email == request.Email))
            return Conflict(new { detail = $"Email '{request.Email}' is already registered" });

        var (hash, salt) = await authService.HashPasswordAsync(request.Password);
        var user = new User
        {
            Username = request.Username,
            Email = request.Email,
            HashedPassword = hash,
            PasswordSalt = salt,
        };

        db.Users.Add(user);
        await db.SaveChangesAsync();

        return CreatedAtAction(
            nameof(Register),
            new RegisterResponse(user.Id, user.Username, user.Email)
        );
    }

    [HttpPost("login")]
    [ProducesResponseType<LoginResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Login([FromBody] LoginRequest request)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Username == request.Username);

        // 刻意把「使用者不存在」和「密碼錯誤」合併成同一個訊息
        // 防止 user enumeration attack（攻擊者藉此知道哪些帳號存在）
        if (user is null || !await authService.VerifyPasswordAsync(request.Password, user.HashedPassword, user.PasswordSalt))
            return Unauthorized(new { detail = "Invalid username or password" });

        var token = authService.CreateJwt(user.Id, user.Username);
        return Ok(new LoginResponse(token));
    }
}
