using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TuchuangApi.Data;
using TuchuangApi.Models;

namespace TuchuangApi.Tests;

/// <summary>
/// 测试基类：为每个测试用例创建独立的 SQLite 内存数据库 + 临时目录，
/// 不污染生产数据。xUnit 默认每个测试方法创建一个新实例，因此每条用例互不影响。
/// </summary>
public abstract class DbTestBase : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly string _tempDir;

    protected AppDbContext Db { get; }
    protected IWebHostEnvironment Env { get; }

    protected DbTestBase()
    {
        // 用 :memory: SQLite：连接必须保持打开，整个数据库生命周期内有效
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;
        Db = new AppDbContext(options);
        Db.Database.EnsureCreated();

        // 复刻 Program.cs 启动时的默认分类初始化
        if (!Db.Categories.Any())
        {
            var defaults = new[] { "风景", "人物", "美食", "动物", "动漫", "截图", "其他" };
            Db.Categories.AddRange(defaults.Select(n => new Category { Name = n, CreatedAt = DateTime.UtcNow }));
            Db.SaveChanges();
        }

        // 临时目录给 ImagesController 作为 ContentRootPath，避免污染生产 uploads/
        _tempDir = Path.Combine(Path.GetTempPath(), "tuchuang-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        Env = new TestHostEnvironment(_tempDir);
    }

    public void Dispose()
    {
        Db.Dispose();
        _connection.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* ignore */ }
        GC.SuppressFinalize(this);
    }
}
