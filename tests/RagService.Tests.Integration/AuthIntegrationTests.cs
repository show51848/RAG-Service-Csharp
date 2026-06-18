using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using RagService.Api.DTOs.Auth;

namespace RagService.Tests.Integration;

// 整合測試：Auth 流程（Register → Login）
// 這是 E2E 測試，HTTP 請求會走完整個 middleware pipeline
// 對應 Python 版直接用 requests.post() 或 httpx 打 TestClient

public class AuthIntegrationTests(RagServiceFactory factory)
    : IClassFixture<RagServiceFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    // 每個測試用不同的 username/email，避免相互干擾
    private static (string Username, string Email, string Password) NewUser()
    {
        var id = Guid.NewGuid().ToString("N")[..8];
        return ($"user_{id}", $"user_{id}@test.com", "password-123!");
    }

    // ── Register ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Register_ValidData_Returns201WithUserInfo()
    {
        var (username, email, password) = NewUser();

        var response = await _client.PostAsJsonAsync("/auth/register", new
        {
            username,
            email,
            password
        });

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var body = await response.Content.ReadFromJsonAsync<RegisterResponse>();
        body!.Username.Should().Be(username);
        body.Email.Should().Be(email);
        body.Id.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Register_DuplicateUsername_Returns409()
    {
        var (username, email, password) = NewUser();

        // 第一次成功
        await _client.PostAsJsonAsync("/auth/register", new { username, email, password });

        // 第二次用相同 username 但不同 email
        var response = await _client.PostAsJsonAsync("/auth/register", new
        {
            username,
            email = $"other_{email}",
            password
        });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Register_DuplicateEmail_Returns409()
    {
        var (username, email, password) = NewUser();
        await _client.PostAsJsonAsync("/auth/register", new { username, email, password });

        var response = await _client.PostAsJsonAsync("/auth/register", new
        {
            username = $"other_{username}",
            email,
            password
        });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    // ── Login ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Login_ValidCredentials_ReturnsJwtToken()
    {
        var (username, email, password) = NewUser();
        await _client.PostAsJsonAsync("/auth/register", new { username, email, password });

        var response = await _client.PostAsJsonAsync("/auth/login", new
        {
            username,
            password
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<LoginResponse>();
        body!.AccessToken.Should().NotBeNullOrEmpty();
        body.TokenType.Should().Be("bearer");
        // JWT 是三段 base64 用 . 分隔
        body.AccessToken.Split('.').Should().HaveCount(3);
    }

    [Fact]
    public async Task Login_WrongPassword_Returns401()
    {
        var (username, email, password) = NewUser();
        await _client.PostAsJsonAsync("/auth/register", new { username, email, password });

        var response = await _client.PostAsJsonAsync("/auth/login", new
        {
            username,
            password = "wrong-password!"
        });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Login_NonExistentUser_Returns401()
    {
        // 對應 Python 版「不存在」和「密碼錯誤」都回 401 的防 enumeration 設計
        var response = await _client.PostAsJsonAsync("/auth/login", new
        {
            username = "nobody",
            password = "irrelevant"
        });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── Endpoints require auth ────────────────────────────────────────────────

    [Fact]
    public async Task GetDocs_WithoutToken_Returns401()
    {
        // 未帶 JWT 的請求應被 ASP.NET Core 的 [Authorize] 擋下來
        var response = await _client.GetAsync("/docs");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
