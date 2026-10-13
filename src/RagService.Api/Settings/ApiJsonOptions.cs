using System.Text.Json;
using System.Text.Json.Serialization;

namespace RagService.Api.Settings;

// 讓 C# 版 API 的 JSON 格式跟 Python 版完全一致，同一份前端可以接兩個後端
// - 欄位名稱：Python/Pydantic 預設 snake_case（access_token）；ASP.NET Core 預設 camelCase（accessToken）
// - Enum：Python 的 str Enum 序列化成字串（"indexed"）；C# enum 預設序列化成數字（2）
// 抽成共用方法，讓 Program.cs 跟整合測試的 HttpClient 用同一套規則
public static class ApiJsonOptions
{
    public static void Apply(JsonSerializerOptions options)
    {
        options.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
    }
}
