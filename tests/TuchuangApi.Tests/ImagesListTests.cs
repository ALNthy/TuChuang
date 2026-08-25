using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using TuchuangApi.Controllers;
using TuchuangApi.Models;

namespace TuchuangApi.Tests;

/// <summary>
/// ImagesController.List 的集成测试：分页 / 搜索 / 分类筛选。
/// 直接实例化控制器，避免对 [Authorize] JWT 鉴权的依赖，专注测试 List 业务逻辑。
/// </summary>
public class ImagesListTests : DbTestBase
{
    private readonly ImagesController _controller;

    public ImagesListTests()
    {
        _controller = new ImagesController(Db, Env, NullLogger<ImagesController>.Instance);
    }

    private Image NewImage(string name, string category, int ageSeconds = 0) => new()
    {
        FileName = name,
        StoredName = Guid.NewGuid().ToString("N") + Path.GetExtension(name),
        Url = "/uploads/x",
        FileSize = 100,
        ContentType = "image/png",
        Category = category,
        UploadedAt = DateTime.UtcNow.AddSeconds(-ageSeconds)
    };

    [Fact]
    public async Task List_Paging_ReturnsCorrectItemsAndHasMore()
    {
        // Arrange：插入 50 张图，按 UploadedAt 递减（每张晚 1 秒）
        for (int i = 0; i < 50; i++)
            Db.Images.Add(NewImage($"img{i:D2}.png", "其他", ageSeconds: i));
        await Db.SaveChangesAsync();

        // Act：第 1 页，pageSize=40
        var result = await _controller.List(category: null, keyword: null, page: 1, pageSize: 40);

        // Assert：返回 40 条，total=50，hasMore=true
        var json = result.AsJsonElement();
        Assert.Equal(50, json.GetProperty("total").GetInt32());
        Assert.Equal(40, json.GetProperty("items").GetArrayLength());
        Assert.True(json.GetProperty("hasMore").GetBoolean());
        Assert.Equal(1, json.GetProperty("page").GetInt32());
        Assert.Equal(40, json.GetProperty("pageSize").GetInt32());

        // Act：第 2 页，pageSize=40
        var result2 = await _controller.List(category: null, keyword: null, page: 2, pageSize: 40);

        // Assert：返回剩余 10 条，hasMore=false
        var json2 = result2.AsJsonElement();
        Assert.Equal(50, json2.GetProperty("total").GetInt32());
        Assert.Equal(10, json2.GetProperty("items").GetArrayLength());
        Assert.False(json2.GetProperty("hasMore").GetBoolean());

        // 第 1 页第 1 条应该是最新上传的（img0），第 2 页最后一条应该是最早的（img49）
        var firstItem = json.GetProperty("items")[0];
        Assert.Equal("img00.png", firstItem.GetProperty("fileName").GetString());
        var lastItem = json2.GetProperty("items")[9];
        Assert.Equal("img49.png", lastItem.GetProperty("fileName").GetString());
    }

    [Fact]
    public async Task List_PageSizeClamped_WhenTooLarge()
    {
        // Arrange：插入 5 张图
        for (int i = 0; i < 5; i++)
            Db.Images.Add(NewImage($"img{i:D2}.png", "其他"));
        await Db.SaveChangesAsync();

        // Act：传入超大 pageSize，应被钳到 200
        var result = await _controller.List(category: null, keyword: null, page: 1, pageSize: 9999);

        var json = result.AsJsonElement();
        Assert.Equal(200, json.GetProperty("pageSize").GetInt32());
        Assert.Equal(5, json.GetProperty("items").GetArrayLength());
        Assert.False(json.GetProperty("hasMore").GetBoolean());
    }

    [Fact]
    public async Task List_PageClamped_WhenLessThanOne()
    {
        for (int i = 0; i < 3; i++)
            Db.Images.Add(NewImage($"img{i:D2}.png", "其他"));
        await Db.SaveChangesAsync();

        // page=0 应被钳为 1
        var result = await _controller.List(category: null, keyword: null, page: 0, pageSize: 10);

        var json = result.AsJsonElement();
        Assert.Equal(1, json.GetProperty("page").GetInt32());
        Assert.Equal(3, json.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task List_Keyword_FiltersByFileNameSubstring()
    {
        // Arrange
        Db.Images.Add(NewImage("sunset-beach.png", "风景"));
        Db.Images.Add(NewImage("mountain-snow.png", "风景"));
        Db.Images.Add(NewImage("sunset-city.jpg", "人物"));
        Db.Images.Add(NewImage("portrait.png", "人物"));
        await Db.SaveChangesAsync();

        // Act：搜索关键词 "sunset"
        var result = await _controller.List(category: null, keyword: "sunset", page: 1, pageSize: 40);

        // Assert：应返回 2 条
        var json = result.AsJsonElement();
        Assert.Equal(2, json.GetProperty("total").GetInt32());
        Assert.Equal(2, json.GetProperty("items").GetArrayLength());
        var names = json.GetProperty("items")
            .EnumerateArray()
            .Select(i => i.GetProperty("fileName").GetString())
            .OrderBy(n => n)
            .ToArray();
        Assert.Equal(new[] { "sunset-beach.png", "sunset-city.jpg" }, names);
    }

    [Fact]
    public async Task List_Keyword_CaseInsensitive()
    {
        Db.Images.Add(NewImage("Sunset-Beach.PNG", "风景"));
        await Db.SaveChangesAsync();

        var result = await _controller.List(category: null, keyword: "SUNSET", page: 1, pageSize: 40);

        var json = result.AsJsonElement();
        Assert.Equal(1, json.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task List_CategoryFilter_OnlyReturnsThatCategory()
    {
        // Arrange
        Db.Images.Add(NewImage("a.png", "风景"));
        Db.Images.Add(NewImage("b.png", "人物"));
        Db.Images.Add(NewImage("c.png", "风景"));
        Db.Images.Add(NewImage("d.png", "美食"));
        await Db.SaveChangesAsync();

        // Act：筛选 "风景"
        var result = await _controller.List(category: "风景", keyword: null, page: 1, pageSize: 40);

        // Assert：返回 2 条，全部是风景分类
        var json = result.AsJsonElement();
        Assert.Equal(2, json.GetProperty("total").GetInt32());
        Assert.Equal(2, json.GetProperty("items").GetArrayLength());
        foreach (var item in json.GetProperty("items").EnumerateArray())
            Assert.Equal("风景", item.GetProperty("category").GetString());
    }

    [Fact]
    public async Task List_CategoryAll_ReturnsAllCategories()
    {
        Db.Images.Add(NewImage("a.png", "风景"));
        Db.Images.Add(NewImage("b.png", "人物"));
        await Db.SaveChangesAsync();

        // category="全部" 应被忽略，返回所有图片
        var result = await _controller.List(category: "全部", keyword: null, page: 1, pageSize: 40);

        var json = result.AsJsonElement();
        Assert.Equal(2, json.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task List_CategoryAndKeyword_Combined()
    {
        Db.Images.Add(NewImage("sunset-1.png", "风景"));
        Db.Images.Add(NewImage("sunset-2.png", "人物"));
        Db.Images.Add(NewImage("mountain.png", "风景"));
        await Db.SaveChangesAsync();

        // 同时筛选分类 "风景" + 关键词 "sunset"
        var result = await _controller.List(category: "风景", keyword: "sunset", page: 1, pageSize: 40);

        var json = result.AsJsonElement();
        Assert.Equal(1, json.GetProperty("total").GetInt32());
        Assert.Equal("sunset-1.png", json.GetProperty("items")[0].GetProperty("fileName").GetString());
    }

    [Fact]
    public async Task List_Empty_ReturnsEmptyArray()
    {
        var result = await _controller.List(category: null, keyword: null, page: 1, pageSize: 40);

        var json = result.AsJsonElement();
        Assert.Equal(0, json.GetProperty("total").GetInt32());
        Assert.Equal(0, json.GetProperty("items").GetArrayLength());
        Assert.False(json.GetProperty("hasMore").GetBoolean());
    }
}
