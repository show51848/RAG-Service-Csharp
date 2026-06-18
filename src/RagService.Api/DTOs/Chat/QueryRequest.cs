using System.ComponentModel.DataAnnotations;

namespace RagService.Api.DTOs.Chat;

public record QueryRequest(
    [Required, MinLength(1)] string Question,
    // null 代表搜尋使用者的所有文件，有值則只搜尋指定文件
    List<int>? DocIds = null
);
