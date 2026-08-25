using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TuchuangApi.Data;
using TuchuangApi.Models;

namespace TuchuangApi.Controllers;

[ApiController]
[Route("api/[controller]")]
public class CategoriesController : ControllerBase
{
    private readonly AppDbContext _db;
    public CategoriesController(AppDbContext db) => _db = db;

    /// <summary>获取所有分类</summary>
    [HttpGet]
    public async Task<IActionResult> List()
    {
        var list = await _db.Categories
            .OrderBy(c => c.Name)
            .Select(c => new CategoryDto(c.Id, c.Name))
            .ToListAsync();
        return Ok(list);
    }

    /// <summary>新建自定义分类</summary>
    [Authorize]
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateCategoryRequest? req)
    {
        var name = (req?.Name ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(name))
            return BadRequest(new { error = "分类名不能为空" });
        if (name.Length > 64)
            return BadRequest(new { error = "分类名过长（最多 64 字符）" });
        if (await _db.Categories.AnyAsync(c => c.Name == name))
            return BadRequest(new { error = "分类已存在" });

        var cat = new Category { Name = name, CreatedAt = DateTime.UtcNow };
        _db.Categories.Add(cat);
        await _db.SaveChangesAsync();

        return Ok(new CategoryDto(cat.Id, cat.Name));
    }

    /// <summary>删除分类：如果分类下还有图片，阻止删除并返回图片数量</summary>
    [Authorize]
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        var cat = await _db.Categories.FindAsync(id);
        if (cat is null) return NotFound();

        // 检查该分类下是否还有图片
        var count = await _db.Images.CountAsync(i => i.Category == cat.Name);
        if (count > 0)
        {
            return BadRequest(new { error = $"分类「{cat.Name}」下还有 {count} 张图片，请先移动或删除这些图片", count });
        }

        _db.Categories.Remove(cat);
        await _db.SaveChangesAsync();

        return NoContent();
    }

    /// <summary>重命名分类：同步更新所有该分类下图片的 category 字段</summary>
    [Authorize]
    [HttpPatch("{id:int}")]
    public async Task<IActionResult> Rename(int id, [FromBody] RenameCategoryRequest? req)
    {
        var cat = await _db.Categories.FindAsync(id);
        if (cat is null) return NotFound();

        var newName = (req?.Name ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(newName))
            return BadRequest(new { error = "分类名不能为空" });
        if (newName.Length > 64)
            return BadRequest(new { error = "分类名过长（最多 64 字符）" });
        if (newName != cat.Name && await _db.Categories.AnyAsync(c => c.Name == newName))
            return BadRequest(new { error = "分类名已存在" });

        var oldName = cat.Name;
        cat.Name = newName;

        // 同步更新所有该分类下的图片
        var images = await _db.Images.Where(i => i.Category == oldName).ToListAsync();
        foreach (var img in images)
            img.Category = newName;

        await _db.SaveChangesAsync();
        return Ok(new CategoryDto(cat.Id, cat.Name));
    }
}

public record CategoryDto(int Id, string Name);
public record CreateCategoryRequest(string? Name);
public record RenameCategoryRequest(string? Name);
