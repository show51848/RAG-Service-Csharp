namespace RagService.Api.DTOs.Auth;

public record LoginResponse(string AccessToken, string TokenType = "bearer");
