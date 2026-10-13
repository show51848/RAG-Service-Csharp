using System.Text.Json;
using RagService.Api.Settings;

namespace RagService.Tests.Integration.Helpers;

// 測試端讀寫 JSON 要跟 API 用同一套規則（snake_case + 字串 enum），
// 否則 ReadFromJsonAsync 預設的 camelCase 會對不上 access_token 之類的欄位
public static class TestJson
{
    public static readonly JsonSerializerOptions Options = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        ApiJsonOptions.Apply(options);
        return options;
    }
}
