using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.Tokens;

namespace TuchuangApi.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AccountController : ControllerBase
{
    private readonly IConfiguration _config;
    public AccountController(IConfiguration config) => _config = config;

    // 登录防爆破：按「IP + 用户名」记录连续失败次数，超过阈值后锁定一段时间（内存实现，单实例适用）
    private const int MaxFailedAttempts = 5;
    private static readonly TimeSpan LockoutWindow = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan LockDuration = TimeSpan.FromMinutes(15);
    private static readonly ConcurrentDictionary<string, (int Count, DateTime FirstFail)> Failures = new();

    [HttpPost("login")]
    public IActionResult Login([FromBody] LoginRequest? req)
    {
        var u = (req?.Username ?? string.Empty).Trim();
        var p = req?.Password ?? string.Empty;
        if (string.IsNullOrEmpty(u) || string.IsNullOrEmpty(p))
            return BadRequest(new { error = "请输入账号和密码" });

        var ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var key = $"{ip}|{u}";

        // 锁定检查：连续失败达到阈值且在锁定期内直接拒绝
        if (Failures.TryGetValue(key, out var rec)
            && rec.Count >= MaxFailedAttempts
            && DateTime.UtcNow - rec.FirstFail < LockDuration)
        {
            var remainMinutes = Math.Ceiling((LockDuration - (DateTime.UtcNow - rec.FirstFail)).TotalMinutes);
            return StatusCode(StatusCodes.Status429TooManyRequests,
                new { error = $"登录失败次数过多，请 {remainMinutes} 分钟后重试" });
        }

        var expectUser = _config["AdminCredentials:Username"] ?? string.Empty;
        var expectPass = _config["AdminCredentials:Password"] ?? string.Empty;

        var userOk = string.Equals(u, expectUser, StringComparison.Ordinal);
        // 密码常量时间比较，降低时序侧信道泄露风险
        var passOk = CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(p),
            Encoding.UTF8.GetBytes(expectPass));

        if (!userOk || !passOk)
        {
            var now = DateTime.UtcNow;
            Failures.AddOrUpdate(key,
                _ => (1, now),
                (_, old) => now - old.FirstFail < LockoutWindow
                    ? (old.Count + 1, old.FirstFail)
                    : (1, now));
            return Unauthorized(new { error = "账号或密码错误" });
        }

        // 登录成功，清除失败记录
        Failures.TryRemove(key, out _);

        var jwt = _config.GetSection("Jwt");
        var signingKey = Encoding.UTF8.GetBytes(jwt["SigningKey"] ?? throw new InvalidOperationException("Jwt:SigningKey 未配置"));
        var expireMinutes = int.TryParse(jwt["ExpireMinutes"], out var m) ? m : 1440;
        var expires = DateTime.UtcNow.AddMinutes(expireMinutes);

        var claims = new[]
        {
            new Claim(ClaimTypes.Name, u),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N"))
        };
        var key2 = new SymmetricSecurityKey(signingKey);
        var creds = new SigningCredentials(key2, SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            issuer: jwt["Issuer"],
            audience: jwt["Audience"],
            claims: claims,
            expires: expires,
            signingCredentials: creds);

        var tokenStr = new JwtSecurityTokenHandler().WriteToken(token);
        return Ok(new LoginResponse(tokenStr, u, expireMinutes));
    }

    /// <summary>返回当前鉴权状态（匿名返回 isAuthenticated=false；已登录返回用户名）</summary>
    [HttpGet("me")]
    [Authorize]
    public IActionResult Me()
    {
        var name = User.Identity?.Name ?? "admin";
        return Ok(new MeResponse(true, name));
    }
}

public record LoginRequest(string? Username, string? Password);
public record LoginResponse(string Token, string Username, int ExpiresIn);
public record MeResponse(bool IsAuthenticated, string Username);
