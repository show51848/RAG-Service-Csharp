namespace RagService.Api.DTOs.Chat;

// 對應 Python 版 app/schemas/chat.py 的 Citation
public record Citation(int DocId, string Filename, string Chunk, float Score);
