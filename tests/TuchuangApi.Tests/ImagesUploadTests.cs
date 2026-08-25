using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using TuchuangApi.Controllers;

namespace TuchuangApi.Tests;

/// <summary>
/// ImagesController.Upload 的文件类型校验测试。
/// 直接调用控制器方法，绕过 [Authorize] JWT 鉴权；不支持的扩展名应返回 400。
/// </summary>
public class ImagesUploadTests : DbTestBase
{
    private readonly ImagesController _controller;

    public ImagesUploadTests()
    {
        _controller = new ImagesController(Db, Env, NullLogger<ImagesController>.Instance);
    }

    private static IFormFileCollection MakeFiles(string fileName, byte[]? content = null)
    {
        content ??= new byte[] { 1, 2, 3, 4 };
        var stream = new MemoryStream(content);
        var file = new FormFile(stream, 0, content.Length, "files", fileName)
        {
            Headers = new HeaderDictionary(),
            ContentType = "application/octet-stream"
        };
        return new FormFileCollection { file };
    }

    [Fact]
    public async Task Upload_UnsupportedExtension_Returns400()
    {
        var files = MakeFiles("readme.txt");

        var result = await _controller.Upload(files, category: "其他");

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        var json = bad.AsJsonElement();
        Assert.Contains("不被支持", json.GetProperty("error").GetString()!);
        Assert.Contains(".txt", json.GetProperty("error").GetString()!);
    }

    [Theory]
    [InlineData(".exe")]
    [InlineData(".pdf")]
    [InlineData(".zip")]
    [InlineData(".docx")]
    [InlineData(".mp4")]
    public async Task Upload_VariousUnsupportedExtensions_Returns400(string ext)
    {
        var files = MakeFiles($"file{ext}");

        var result = await _controller.Upload(files, category: null);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Upload_EmptyFileCollection_Returns400()
    {
        var result = await _controller.Upload(new FormFileCollection(), category: null);

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        var json = bad.AsJsonElement();
        Assert.Equal("未接收到文件", json.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Upload_NullFiles_Returns400()
    {
        // 传 null 模拟未提交 files 表单字段
        var result = await _controller.Upload(null!, category: null);

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        var json = bad.AsJsonElement();
        Assert.Equal("未接收到文件", json.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Upload_SupportedExtension_DoesNotReturn400()
    {
        // 用最小合法 PNG 头：8 字节签名
        var pngHeader = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0 };
        var files = MakeFiles("valid.png", pngHeader);

        var result = await _controller.Upload(files, category: "其他");

        // 不应返回 400（文件类型校验通过；后续可能因解码失败但不在本测试关注范围）
        Assert.IsNotType<BadRequestObjectResult>(result);
        // 应该返回 Ok
        var ok = Assert.IsType<OkObjectResult>(result);
        var json = ok.AsJsonElement();
        Assert.Equal(1, json.GetArrayLength());
        Assert.Equal("valid.png", json[0].GetProperty("fileName").GetString());
    }
}
