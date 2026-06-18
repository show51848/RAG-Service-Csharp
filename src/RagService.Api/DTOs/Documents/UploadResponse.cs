using RagService.Api.Models;

namespace RagService.Api.DTOs.Documents;

public record UploadResponse(int DocId, string Filename, DocumentStatus Status);
