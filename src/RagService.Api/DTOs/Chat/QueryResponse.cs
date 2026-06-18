namespace RagService.Api.DTOs.Chat;

public record QueryResponse(string Answer, List<Citation> Citations);
