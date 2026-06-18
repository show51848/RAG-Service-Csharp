using System.ComponentModel.DataAnnotations;

namespace RagService.Api.DTOs.Auth;

// Python 版用 OAuth2PasswordRequestForm（form data）；C# 版改用 JSON body
// 差異原因：ASP.NET Core 的 [FromBody] JSON 更自然，不需要額外設定 form content-type
// 面試時可解釋：Python FastAPI 有內建 OAuth2 form 支援，C# 習慣用 JSON REST
public record LoginRequest(
    [Required] string Username,
    [Required] string Password
);
