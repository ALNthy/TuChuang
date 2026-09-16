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
// 回写配置，保证 AccountController 签发 token 与这里的验证使用同一把密钥
builder.Configuration["Jwt:SigningKey"] = signingKeyStr;
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

// 跨域白名单：仅允许配置的来源（默认本地开发前端）；
// 生产环境用环境变量 Cors__AllowedOrigins__0 / __1 ... 覆盖。Docker 下同源反代，无需跨域
var corsPolicy = "DevCors";
var corsOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
    ?? new[] { "http://localhost:5173", "http://127.0.0.1:5173" };
builder.Services.AddCors(o =>
{
    o.AddPolicy(corsPolicy, p => p
        .WithOrigins(corsOrigins)
        .AllowAnyHeader()
        .AllowAnyMethod());
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

// 启动时清理遗留的分片上传临时目录（上传中断/进程退出残留）
CleanupTempSessions(uploadsDir, maxAgeHours: 24, app.Logger);

// 全局异常处理：捕获未处理异常，记录日志并返回统一 JSON，避免生产环境空 500
app.UseExceptionHandler(errApp => errApp.Run(async ctx =>
{
    var feature = ctx.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>();
    var ex = feature?.Error;
    if (ex is not null)
    {
        ctx.RequestServices.GetRequiredService<ILogger<Program>>()
            .LogError(ex, "未处理的异常: {Method} {Path}", ctx.Request.Method, ctx.Request.Path);
    }
    ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
    ctx.Response.ContentType = "application/json; charset=utf-8";
    await ctx.Response.WriteAsync("{\"error\":\"服务器内部错误\"}");
}));

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

// ================================================================================
// 分片临时目录清理：应用启动时调用一次
// 扫描 uploads/temp/，删除 mtime 超过 maxAgeHours 小时的会话目录（含其中残留分片）
// 正常流程会在 merge 时清理，这里兜底处理上传中断/进程异常的残留
// ================================================================================
static void CleanupTempSessions(string uploadsDir, int maxAgeHours, ILogger logger)
{
    var tempDir = Path.Combine(uploadsDir, "temp");
    if (!Directory.Exists(tempDir)) return;

    var cutoff = DateTime.UtcNow.AddHours(-maxAgeHours);
    var deleted = 0;
    long freedBytes = 0;
    foreach (var dir in Directory.EnumerateDirectories(tempDir))
    {
        try
        {
            if (Directory.GetLastWriteTimeUtc(dir) < cutoff)
            {
                try
                {
                    foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                        freedBytes += new FileInfo(f).Length;
                }
                catch { /* 统计大小失败不阻断删除 */ }
                Directory.Delete(dir, true);
                deleted++;
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "清理分片临时目录失败: {Dir}", dir);
        }
    }
    if (deleted > 0)
    {
        logger.LogInformation("已清理 {Count} 个过期分片临时目录（{Bytes:N0} 字节）", deleted, freedBytes);
    }
}