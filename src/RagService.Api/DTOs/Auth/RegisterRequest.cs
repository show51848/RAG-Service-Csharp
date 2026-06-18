using System.ComponentModel.DataAnnotations;

namespace RagService.Api.DTOs.Auth;

// 對應 Python 版 app/schemas/auth.py 的 RegisterRequest
// Python 用 Pydantic EmailStr 驗證 email；C# 用 [EmailAddress] DataAnnotation
public record RegisterRequest(
    [Required, MinLength(3), MaxLength(100)] string Username,
    [Required, EmailAddress, MaxLength(255)] string Email,
    [Required, MinLength(8)] string Password
);
