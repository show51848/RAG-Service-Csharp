using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using RagService.Api.Settings;

namespace RagService.Api.Services;

// 對應 Python 版 app/services/auth_service.py
// Python 用 passlib CryptContext（自動管理 salt）；
// C# 的 Konscious.Argon2 需要手動管理 salt，所以 HashPassword 回傳 hash + salt 兩個值

public interface IAuthService
{
    Task<(string Hash, string Salt)> HashPasswordAsync(string password);
    Task<bool> VerifyPasswordAsync(string password, string hash, string salt);
    string CreateJwt(int userId, string username);
    (int? UserId, string? Username) DecodeJwt(string token);
}

public class AuthService(IOptions<AppSettings> opts) : IAuthService
{
    private readonly AppSettings _settings = opts.Value;

    public async Task<(string Hash, string Salt)> HashPasswordAsync(string password)
    {
        // 每次產生新的隨機 salt（16 bytes = 128 bits）
        var saltBytes = RandomNumberGenerator.GetBytes(16);

        var argon2 = new Argon2id(Encoding.UTF8.GetBytes(password))
        {
            Salt = saltBytes,
            DegreeOfParallelism = 4,  // 使用 4 個 CPU 核心
            MemorySize = 65536,        // 64 MB 記憶體，增加 GPU 暴力破解成本
            Iterations = 3,
        };

        var hashBytes = await argon2.GetBytesAsync(32);  // 256-bit hash

        // 儲存為 Base64 字串，方便存入資料庫
        return (Convert.ToBase64String(hashBytes), Convert.ToBase64String(saltBytes));
    }

    public async Task<bool> VerifyPasswordAsync(string password, string hash, string salt)
    {
        var saltBytes = Convert.FromBase64String(salt);

        var argon2 = new Argon2id(Encoding.UTF8.GetBytes(password))
        {
            Salt = saltBytes,
            DegreeOfParallelism = 4,
            MemorySize = 65536,
            Iterations = 3,
        };

        var hashBytes = await argon2.GetBytesAsync(32);
        var computedHash = Convert.ToBase64String(hashBytes);

        // CryptographicOperations.FixedTimeEquals 是 constant-time 比較
        // 防止 timing attack（攻擊者透過比較時間差推測密碼）
        // 對應 Python passlib 的 verify()，它內部也用 constant-time 比較
        var expectedBytes = Convert.FromBase64String(hash);
        var computedBytes = Convert.FromBase64String(computedHash);
        return CryptographicOperations.FixedTimeEquals(expectedBytes, computedBytes);
    }

    public string CreateJwt(int userId, string username)
    {
        // 對應 Python 版的 create_jwt()
        // Python 用 python-jose；C# 用 System.IdentityModel.Tokens.Jwt（Microsoft 官方）
        var key = new SymmetricSecurityKey(Encoding.ASCII.GetBytes(_settings.SecretKey));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var tokenDescriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(
            [
                new Claim(JwtRegisteredClaimNames.Sub, userId.ToString()),
                new Claim("username", username),
            ]),
            Expires = DateTime.UtcNow.AddMinutes(_settings.AccessTokenExpireMinutes),
            SigningCredentials = credentials,
        };

        var handler = new JwtSecurityTokenHandler();
        var token = handler.CreateToken(tokenDescriptor);
        return handler.WriteToken(token);
    }

    public (int? UserId, string? Username) DecodeJwt(string token)
    {
        // 對應 Python 版的 decode_jwt()
        // Python 遇到錯誤回傳空 TokenData；C# 回傳 (null, null)，讓 Controller 決定如何處理
        try
        {
            var key = new SymmetricSecurityKey(Encoding.ASCII.GetBytes(_settings.SecretKey));
            var handler = new JwtSecurityTokenHandler();

            handler.ValidateToken(token, new TokenValidationParameters
            {
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = key,
                ValidateIssuer = false,
                ValidateAudience = false,
                ClockSkew = TimeSpan.Zero,  // 不允許時間偏差，token 過期就是過期
            }, out var validatedToken);

            var jwt = (JwtSecurityToken)validatedToken;
            var userId = int.Parse(jwt.Subject);
            var username = jwt.Claims.FirstOrDefault(c => c.Type == "username")?.Value;

            return (userId, username);
        }
        catch
        {
            return (null, null);
        }
    }
}
