namespace RagService.Api.DTOs.Auth;

// 刻意不包含 HashedPassword / PasswordSalt，確保密碼相關資料不會出現在 API 回應
public record RegisterResponse(int Id, string Username, string Email);
