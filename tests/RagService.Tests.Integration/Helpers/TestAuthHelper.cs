using System.Net.Http.Headers;
using System.Net.Http.Json;
using RagService.Api.DTOs.Auth;

namespace RagService.Tests.Integration.Helpers;

// 整合測試的 auth 共用輔助方法
// 對應 Python 版 conftest.py 的 @pytest.fixture def auth_client(...)
public static class TestAuthHelper
{
    public static async Task<string> RegisterAndGetTokenAsync(HttpClient client)
    {
        var id = Guid.NewGuid().ToString("N")[..8];
        var username = $"user_{id}";
        var email = $"user_{id}@test.com";
        const string password = "test-password-123!";

        await client.PostAsJsonAsync("/auth/register", new { username, email, password });

        var loginResp = await client.PostAsJsonAsync("/auth/login", new { username, password });
        var login = await loginResp.Content.ReadFromJsonAsync<LoginResponse>();
        return login!.AccessToken;
    }

    // 回傳帶有 Authorization: Bearer <token> header 的 HttpClient
    public static HttpClient WithToken(this HttpClient client, string token)
    {
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token);
        return client;
    }
}
