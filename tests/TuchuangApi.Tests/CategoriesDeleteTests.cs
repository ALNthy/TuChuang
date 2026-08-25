using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TuchuangApi.Controllers;
using TuchuangApi.Models;

namespace TuchuangApi.Tests;

/// <summary>
/// CategoriesController.Delete 的删除保护测试。
/// 直接调用控制器方法，绕过 [Authorize]；非空分类应返回 400 + count。
/// </summary>
public class CategoriesDeleteTests : DbTestBase
{
    private readonly CategoriesController _controller;

    public CategoriesDeleteTests()
    {
        _controller = new CategoriesController(Db);
    }

    [Fact]
    public async Task Delete_CategoryWithImages_Returns400WithCount()
    {
        // Arrange：取默认分类 "风景"，并往里塞 3 张图
        var cat = Db.Categories.Single(c => c.Name == "风景");
        for (int i = 0; i < 3; i++)
        {
            Db.Images.Add(new Image
            {
                FileName = $"img{i}.png",
                StoredName = $"s{i}.png",
                Url = $"/uploads/s{i}.png",
                FileSize = 1,
                ContentType = "image/png",
                Category = "风景",
                UploadedAt = DateTime.UtcNow
            });
        }
        await Db.SaveChangesAsync();

        // Act：尝试删除带图片的分类
        var result = await _controller.Delete(cat.Id);

        // Assert：返回 400 + count=3
        var bad = Assert.IsType<BadRequestObjectResult>(result);
        var json = bad.AsJsonElement();
        Assert.Equal(3, json.GetProperty("count").GetInt32());
        Assert.Contains("3", json.GetProperty("error").GetString()!);
        Assert.Contains("风景", json.GetProperty("error").GetString()!);

        // 分类仍然存在
        Assert.True(await Db.Categories.AnyAsync(c => c.Id == cat.Id));
    }

    [Fact]
    public async Task Delete_EmptyCategory_ReturnsNoContent()
    {
        // Arrange：取默认分类 "动物"，没有图片
        var cat = Db.Categories.Single(c => c.Name == "动物");

        // Act
        var result = await _controller.Delete(cat.Id);

        // Assert：返回 204
        Assert.IsType<NoContentResult>(result);
        Assert.False(await Db.Categories.AnyAsync(c => c.Id == cat.Id));
    }

    [Fact]
    public async Task Delete_NonExistentCategory_Returns404()
    {
        var result = await _controller.Delete(id: 99999);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task Delete_CategoryWithOneImage_Returns400WithCount1()
    {
        var cat = Db.Categories.Single(c => c.Name == "动漫");
        Db.Images.Add(new Image
        {
            FileName = "one.png",
            StoredName = "s.png",
            Url = "/uploads/s.png",
            FileSize = 1,
            ContentType = "image/png",
            Category = "动漫",
            UploadedAt = DateTime.UtcNow
        });
        await Db.SaveChangesAsync();

        var result = await _controller.Delete(cat.Id);

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        var json = bad.AsJsonElement();
        Assert.Equal(1, json.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task Delete_AfterMovingImagesAway_CanDelete()
    {
        // Arrange：先给 "美食" 加 2 张图，再全部移走
        var cat = Db.Categories.Single(c => c.Name == "美食");
        Db.Images.Add(new Image
        {
            FileName = "a.png", StoredName = "s1.png", Url = "", FileSize = 1,
            ContentType = "image/png", Category = "美食", UploadedAt = DateTime.UtcNow
        });
        Db.Images.Add(new Image
        {
            FileName = "b.png", StoredName = "s2.png", Url = "", FileSize = 1,
            ContentType = "image/png", Category = "美食", UploadedAt = DateTime.UtcNow
        });
        await Db.SaveChangesAsync();

        // 移走所有图片到 "风景"
        foreach (var img in Db.Images.Where(i => i.Category == "美食").ToList())
            img.Category = "风景";
        await Db.SaveChangesAsync();

        // Act：现在 "美食" 没图片了，应该能删
        var result = await _controller.Delete(cat.Id);

        Assert.IsType<NoContentResult>(result);
        Assert.False(await Db.Categories.AnyAsync(c => c.Id == cat.Id));
    }
}
