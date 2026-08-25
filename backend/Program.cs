using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using TuchuangApi.Data;
using TuchuangApi.Models;

var builder = WebApplication.CreateBuilder(args);

// EF Core + SQLite
builder.Services.AddDbContext<AppDbContext>(opt =>
    opt.UseSqlite(builder.Configuration.GetConnectionString("Default")));

builder.Services.AddControllers();

// JWT Bearer 鉴权
var jwtSection = builder.Configuration.GetSection("Jwt");
// 优先从环境变量 JWT_SIGNING_KEY 读取（生产环境必须用环境变量），
// 回退到 appsettings.json 的 Jwt:SigningKey（仅开发环境用）
var signingKeyStr = Environment.GetEnvironmentVariable("JWT_SIGNING_KEY")
    ?? jwtSection["SigningKey"]
    ?? throw new InvalidOperationException("Jwt:SigningKey 未配置：请设置环境变量 JWT_SIGNING_KEY 或在 appsettings.json 中配置 Jwt:SigningKey");
if (signingKeyStr.Length < 32)
    throw new InvalidOperationException(
        $"Jwt:SigningKey 长度不足（当前 {signingKeyStr.Length} 字符），至少需要 32 字符以保证安全。" +
        "请设置环境变量 JWT_SIGNING_KEY 为一段足够长的随机字符串。");
var signingKey = Encoding.UTF8.GetBytes(signingKeyStr);
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(opt =>
    {
        opt.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwtSection["Issuer"],
            ValidateAudience = true,
            ValidAudience = jwtSection["Audience"],
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(signingKey),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.Zero
        };
    });
builder.Services.AddAuthorization();

// 允许前端开发服务器跨域访问
var corsPolicy = "DevCors";
builder.Services.AddCors(o =>
{
    o.AddPolicy(corsPolicy, p => p
        .SetIsOriginAllowed(_ => true)
        .AllowAnyHeader()
        .AllowAnyMethod()
        .AllowCredentials());
});

var app = builder.Build();

// 启动时创建数据库、表，并初始化默认分类
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();

    if (!db.Categories.Any())
    {
        var defaults = new[] { "风景", "人物", "美食", "动物", "动漫", "截图", "其他" };
        db.Categories.AddRange(defaults.Select(n => new Category { Name = n, CreatedAt = DateTime.UtcNow }));
        db.SaveChanges();
    }
}

// 上传文件目录
var uploadsDir = Path.Combine(app.Environment.ContentRootPath, "uploads");
Directory.CreateDirectory(uploadsDir);

// 启动时清理预览缓存：删除超过 30 天未修改的缓存文件（含 .fail 负缓存标记）
// 缓存按需重新生成，删除不影响功能；定期清理避免长期累积占空间
CleanupPreviewCache(uploadsDir, maxAgeDays: 30, app.Logger);

app.UseCors(corsPolicy);
app.UseStaticFiles();
// 注意：这里不再把 uploads 目录作为静态文件公开，避免原图被枚举/批量下载；
// 所有图片访问必须通过 /api/images/{id}/preview 或 /api/images/{id}/raw 按需按 ID 获取。

// 请求日志中间件：记录每个请求的方法、路径、状态码、耗时
app.Use(async (ctx, next) =>
{
    var sw = System.Diagnostics.Stopwatch.StartNew();
    await next();
    sw.Stop();
    if (ctx.Request.Path.StartsWithSegments("/api"))
    {
        var logger = ctx.RequestServices.GetRequiredService<ILogger<Program>>();
        logger.LogInformation("{Method} {Path} → {Status} ({ElapsedMs}ms)",
            ctx.Request.Method, ctx.Request.Path, ctx.Response.StatusCode, sw.ElapsedMilliseconds);
    }
});

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapGet("/", () => Results.Json(new { service = "tuchuang-api", status = "ok" }));

app.Run();

// ================================================================================
// 预览缓存清理：应用启动时调用一次
// 扫描 uploads/preview-cache/，删除 mtime 超过 maxAgeDays 天的缓存文件
// 缓存按需重新生成（见 ImagesController.Preview），删除不影响功能
// 同时清理 .fail 负缓存标记（Preview 端点用 25 分钟 TTL，超过即作废但磁盘上残留）
// ================================================================================
static void CleanupPreviewCache(string uploadsDir, int maxAgeDays, ILogger logger)
{
    var cacheDir = Path.Combine(uploadsDir, "preview-cache");
    if (!Directory.Exists(cacheDir)) return;

    var cutoff = DateTime.UtcNow.AddDays(-maxAgeDays);
    var deleted = 0;
    long freedBytes = 0;
    foreach (var file in Directory.EnumerateFiles(cacheDir, "*", SearchOption.TopDirectoryOnly))
    {
        try
        {
            if (File.GetLastWriteTimeUtc(file) < cutoff)
            {
                freedBytes += new FileInfo(file).Length;
                File.Delete(file);
                deleted++;
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "清理预览缓存文件失败: {File}", file);
        }
    }
    if (deleted > 0)
    {
        logger.LogInformation("已清理 {Count} 个超期预览缓存文件（{Bytes:N0} 字节）", deleted, freedBytes);
    }
}
