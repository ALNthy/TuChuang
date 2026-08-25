using Microsoft.Extensions.FileProviders;
using Microsoft.AspNetCore.Hosting;

namespace TuchuangApi.Tests;

/// <summary>
/// 最小化的 IWebHostEnvironment 实现，仅供 ImagesController 构造时取 ContentRootPath 用。
/// ImagesController 构造时会 CreateDirectory(uploads) 和 CreateDirectory(preview-cache)，
/// 指向临时目录即可避免污染生产目录。
/// </summary>
public sealed class TestHostEnvironment : IWebHostEnvironment
{
    public TestHostEnvironment(string contentRootPath)
    {
        ContentRootPath = contentRootPath;
    }

    public string EnvironmentName { get; set; } = "Testing";
    public string ApplicationName { get; set; } = "TuchuangApi.Tests";
    public string WebRootPath { get; set; } = string.Empty;
    public string ContentRootPath { get; set; }
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
}
