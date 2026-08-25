using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

namespace TuchuangApi.Tests;

/// <summary>
/// 从 IActionResult（OkObjectResult/BadRequestObjectResult 等）的匿名对象 Value
/// 中按属性名取值的辅助方法。控制器返回的是匿名对象（new { items, total, ... }），
/// 这里通过 JSON 序列化保证读取稳定，避免对 dynamic 绑定的依赖。
/// </summary>
internal static class TestResultExtensions
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    /// <summary>把 IActionResult.Value 序列化为 JsonElement，便于按名取值。</summary>
    public static JsonElement AsJsonElement(this IActionResult result)
    {
        // OkObjectResult/BadRequestObjectResult 都派生自 ObjectResult，用 IsAssignableFrom 接受派生类型
        var obj = Assert.IsAssignableFrom<ObjectResult>(result);
        var json = JsonSerializer.Serialize(obj.Value, JsonOpts);
        return JsonDocument.Parse(json).RootElement;
    }
}
