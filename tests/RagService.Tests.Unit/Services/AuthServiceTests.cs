using FluentAssertions;
using Microsoft.Extensions.Options;
using RagService.Api.Services;
using RagService.Api.Settings;

namespace RagService.Tests.Unit.Services;

// AuthService 沒有外部依賴（只用 Argon2 和 JWT），所以不需要 Mock
// 對應 Python 版的 test_auth_service.py 測試 hash_password / verify_password / create_jwt

public class AuthServiceTests
{
    private readonly AuthService _sut;  // sut = System Under Test

    public AuthServiceTests()
    {
        var settings = Options.Create(new AppSettings
        {
            SecretKey = "test-secret-key-that-is-at-least-32-chars!",
            AccessTokenExpireMinutes = 60,
        });
        _sut = new AuthService(settings);
    }

    // ── HashPasswordAsync ────────────────────────────────────────────────────

    [Fact]
    public async Task HashPasswordAsync_SamePassword_ProducesDifferentHashes()
    {
        // Argon2id 每次產生新的隨機 salt，所以相同密碼的雜湊值每次都不同
        // 這是密碼雜湊的必要特性，防止 rainbow table 攻擊
        var (hash1, salt1) = await _sut.HashPasswordAsync("my-password");
        var (hash2, salt2) = await _sut.HashPasswordAsync("my-password");

        hash1.Should().NotBe(hash2);  // 雜湊不同（因為 salt 不同）
        salt1.Should().NotBe(salt2);  // salt 每次隨機產生
    }

    [Fact]
    public async Task HashPasswordAsync_ReturnsBase64Strings()
    {
        var (hash, salt) = await _sut.HashPasswordAsync("any-password");

        // 確認是合法的 Base64（可以正確 decode 不拋例外）
        var act1 = () => Convert.FromBase64String(hash);
        var act2 = () => Convert.FromBase64String(salt);

        act1.Should().NotThrow();
        act2.Should().NotThrow();
    }

    // ── VerifyPasswordAsync ──────────────────────────────────────────────────

    [Fact]
    public async Task VerifyPasswordAsync_CorrectPassword_ReturnsTrue()
    {
        var (hash, salt) = await _sut.HashPasswordAsync("correct-password");

        var result = await _sut.VerifyPasswordAsync("correct-password", hash, salt);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task VerifyPasswordAsync_WrongPassword_ReturnsFalse()
    {
        var (hash, salt) = await _sut.HashPasswordAsync("correct-password");

        var result = await _sut.VerifyPasswordAsync("wrong-password", hash, salt);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task VerifyPasswordAsync_WrongSalt_ReturnsFalse()
    {
        // 如果 salt 被竄改，即使密碼相同也應該驗證失敗
        var (hash, _) = await _sut.HashPasswordAsync("password");
        var (_, differentSalt) = await _sut.HashPasswordAsync("password");

        var result = await _sut.VerifyPasswordAsync("password", hash, differentSalt);

        result.Should().BeFalse();
    }

    // ── CreateJwt / DecodeJwt ────────────────────────────────────────────────

    [Fact]
    public void CreateJwt_ValidInput_ReturnsNonEmptyToken()
    {
        var token = _sut.CreateJwt(userId: 42, username: "alice");

        token.Should().NotBeNullOrEmpty();
        // JWT 格式是三段 base64 用 . 分隔
        token.Split('.').Should().HaveCount(3);
    }

    [Fact]
    public void DecodeJwt_ValidToken_ReturnsCorrectClaims()
    {
        // 對應 Python 版 decode_jwt 的核心邏輯：從 payload 取出 user_id 和 username
        var token = _sut.CreateJwt(userId: 42, username: "alice");

        var (userId, username) = _sut.DecodeJwt(token);

        userId.Should().Be(42);
        username.Should().Be("alice");
    }

    [Fact]
    public void DecodeJwt_InvalidToken_ReturnsNulls()
    {
        // 對應 Python 版 decode_jwt 遇到 JWTError 回傳空 TokenData
        var (userId, username) = _sut.DecodeJwt("this.is.invalid");

        userId.Should().BeNull();
        username.Should().BeNull();
    }

    [Fact]
    public void DecodeJwt_TokenSignedWithWrongKey_ReturnsNulls()
    {
        // 如果 token 用不同的 secret key 簽名，驗證應失敗
        var otherSettings = Options.Create(new AppSettings
        {
            SecretKey = "completely-different-secret-key-32chars!!"
        });
        var otherService = new AuthService(otherSettings);
        var foreignToken = otherService.CreateJwt(userId: 99, username: "hacker");

        var (userId, _) = _sut.DecodeJwt(foreignToken);

        userId.Should().BeNull();
    }
}
