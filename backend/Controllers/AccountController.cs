using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
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

    [HttpPost("login")]
    public IActionResult Login([FromBody] LoginRequest? req)
    {
        var u = (req?.Username ?? string.Empty).Trim();
        var p = req?.Password ?? string.Empty;
        if (string.IsNullOrEmpty(u) || string.IsNullOrEmpty(p))
            return BadRequest(new { error = "请输入账号和密码" });

        var expectUser = _config["AdminCredentials:Username"];
        var expectPass = _config["AdminCredentials:Password"];
        if (!string.Equals(u, expectUser, StringComparison.Ordinal) ||
            !string.Equals(p, expectPass, StringComparison.Ordinal))
            return Unauthorized(new { error = "账号或密码错误" });

        var jwt = _config.GetSection("Jwt");
        var signingKey = Encoding.UTF8.GetBytes(jwt["SigningKey"] ?? throw new InvalidOperationException("Jwt:SigningKey 未配置"));
        var expireMinutes = int.TryParse(jwt["ExpireMinutes"], out var m) ? m : 1440;
        var expires = DateTime.UtcNow.AddMinutes(expireMinutes);

        var claims = new[]
        {
            new Claim(ClaimTypes.Name, u),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N"))
        };
        var key = new SymmetricSecurityKey(signingKey);
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
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
