using ImageMagick;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TuchuangApi.Data;
using TuchuangApi.Models;

namespace TuchuangApi.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ImagesController : ControllerBase
{
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp",
        ".raw", ".arw"
    };

    private static readonly HashSet<string> WebRenderableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp"
    };

    private static readonly HashSet<string> RawExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".raw", ".arw"
    };

    private const long MaxFileSize = 100 * 1024 * 1024; // 100MB
    private const int ThumbMaxWidth = 480;     // 卡片缩略图宽度
    private const int MediumMaxWidth = 1600;   // Lightbox 中等预览宽度（RAW 用）
    private const int ThumbQuality = 80;

    private readonly AppDbContext _db;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<ImagesController> _logger;
    private readonly string _uploadsDir;
    private readonly string _previewCacheDir;
    private readonly string _tempDir;

    public ImagesController(AppDbContext db, IWebHostEnvironment env, ILogger<ImagesController> logger)
    {
        _db = db;
        _env = env;
        _logger = logger;
        _uploadsDir = Path.Combine(env.ContentRootPath, "uploads");
        _previewCacheDir = Path.Combine(_uploadsDir, "preview-cache");
        _tempDir = Path.Combine(_uploadsDir, "temp");
        Directory.CreateDirectory(_uploadsDir);
        Directory.CreateDirectory(_previewCacheDir);
        Directory.CreateDirectory(_tempDir);
    }

    /// <summary>获取图片列表（默认按上传时间倒序，分页，支持按分类筛选 + 按文件名搜索）</summary>
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string? category, [FromQuery] string? keyword, [FromQuery] int page = 1, [FromQuery] int pageSize = 40)
    {
        if (page < 1) page = 1;
        if (pageSize < 1) pageSize = 20;
        if (pageSize > 200) pageSize = 200;

        var q = _db.Images.OrderByDescending(i => i.UploadedAt).AsQueryable();
        if (!string.IsNullOrWhiteSpace(category) && category != "全部")
            q = q.Where(i => i.Category == category);
        // 按文件名模糊搜索（大小写不敏感，SQLite 默认 LIKE 不区分大小写）
        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var kw = keyword.Trim();
            q = q.Where(i => EF.Functions.Like(i.FileName, $"%{kw}%"));
        }

        var total = await q.CountAsync();
        var items = await q
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(i => new ImageDto(i.Id, i.FileName, i.FileSize, i.ContentType, i.Category, i.UploadedAt))
            .ToListAsync();
        var hasMore = page * pageSize < total;
        return Ok(new { items, total, hasMore, page, pageSize });
    }

    /// <summary>上传一张或多张图片（可指定自定义分类，分类不存在时自动创建）</summary>
    [Authorize]
    [HttpPost]
    [RequestSizeLimit(MaxFileSize * 10)]
    public async Task<IActionResult> Upload([FromForm] IFormFileCollection files, [FromForm] string? category)
    {
        if (files is null || files.Count == 0)
            return BadRequest(new { error = "未接收到文件" });

        var cat = string.IsNullOrWhiteSpace(category) ? "其他" : category.Trim();
        if (cat.Length > 64) cat = cat[..64];

        if (!await _db.Categories.AnyAsync(c => c.Name == cat))
        {
            _db.Categories.Add(new Category { Name = cat, CreatedAt = DateTime.UtcNow });
            await _db.SaveChangesAsync();
        }

        Directory.CreateDirectory(_uploadsDir);
        var created = new List<ImageDto>();

        foreach (var file in files)
        {
            if (file.Length == 0) continue;
            if (file.Length > MaxFileSize)
                return BadRequest(new { error = $"文件 {file.FileName} 超过 {MaxFileSize / (1024 * 1024)}MB 限制" });

            var ext = Path.GetExtension(file.FileName);
            if (!AllowedExtensions.Contains(ext))
                return BadRequest(new { error = $"文件类型 {ext} 不被支持" });

            var storedName = $"{Guid.NewGuid():N}{ext}";
            var filePath = Path.Combine(_uploadsDir, storedName);

            await using (var fs = System.IO.File.Create(filePath))
            {
                await file.CopyToAsync(fs);
            }

            var image = new Image
            {
                FileName = Path.GetFileName(file.FileName), // 安全化：去除路径分隔符
                StoredName = storedName,
                Url = $"/uploads/{storedName}",
                FileSize = file.Length,
                ContentType = file.ContentType,
                Category = cat,
                UploadedAt = DateTime.UtcNow
            };

            _db.Images.Add(image);
            await _db.SaveChangesAsync();

            created.Add(new ImageDto(image.Id, image.FileName, image.FileSize, image.ContentType, image.Category, image.UploadedAt));
        }

        return Ok(created);
    }

    /// <summary>大文件分片上传：接收单个分片
    /// 前端将大文件切片（每片 5MB），逐个上传到此接口，最后调用 merge 合并。
    /// uploadId 由前端生成（UUID），用于标识同一次上传会话。
    /// </summary>
    [Authorize]
    [HttpPost("upload-chunk")]
    [RequestSizeLimit(10 * 1024 * 1024)] // 每个分片最大 10MB
    public async Task<IActionResult> UploadChunk(
        [FromForm] IFormFile file,
        [FromForm] string uploadId,
        [FromForm] int chunkIndex,
        [FromForm] int totalChunks,
        [FromForm] string fileName)
    {
        if (file is null || file.Length == 0)
            return BadRequest(new { error = "分片数据为空" });
        if (string.IsNullOrWhiteSpace(uploadId))
            return BadRequest(new { error = "uploadId 不能为空" });
        if (chunkIndex < 0 || chunkIndex >= totalChunks)
            return BadRequest(new { error = "分片序号无效" });

        var ext = Path.GetExtension(fileName);
        if (!AllowedExtensions.Contains(ext))
            return BadRequest(new { error = $"文件类型 {ext} 不被支持" });

        var sessionDir = Path.Combine(_tempDir, uploadId);
        Directory.CreateDirectory(sessionDir);
        var chunkPath = Path.Combine(sessionDir, $"chunk_{chunkIndex:D5}");
        await using (var fs = System.IO.File.Create(chunkPath))
        {
            await file.CopyToAsync(fs);
        }

        return Ok(new { uploadId, chunkIndex, received = true, totalChunks });
    }

    /// <summary>合并分片：所有分片上传完成后调用，合并成完整文件并入库</summary>
    [Authorize]
    [HttpPost("merge")]
    public async Task<IActionResult> MergeChunks(
        [FromForm] string uploadId,
        [FromForm] string fileName,
        [FromForm] string? category)
    {
        if (string.IsNullOrWhiteSpace(uploadId))
            return BadRequest(new { error = "uploadId 不能为空" });

        var sessionDir = Path.Combine(_tempDir, uploadId);
        if (!System.IO.Directory.Exists(sessionDir))
            return BadRequest(new { error = "上传会话不存在或已过期" });

        var chunks = System.IO.Directory.GetFiles(sessionDir, "chunk_*")
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
        if (chunks.Count == 0)
            return BadRequest(new { error = "没有找到分片数据" });

        var ext = Path.GetExtension(fileName);
        if (!AllowedExtensions.Contains(ext))
            return BadRequest(new { error = $"文件类型 {ext} 不被支持" });

        var cat = string.IsNullOrWhiteSpace(category) ? "其他" : category.Trim();
        if (cat.Length > 64) cat = cat[..64];
        if (!await _db.Categories.AnyAsync(c => c.Name == cat))
        {
            _db.Categories.Add(new Category { Name = cat, CreatedAt = DateTime.UtcNow });
            await _db.SaveChangesAsync();
        }

        var storedName = $"{Guid.NewGuid():N}{ext}";
        var filePath = Path.Combine(_uploadsDir, storedName);
        await using (var outStream = System.IO.File.Create(filePath))
        {
            foreach (var chunk in chunks)
            {
                await using var chunkStream = System.IO.File.OpenRead(chunk);
                await chunkStream.CopyToAsync(outStream);
            }
        }

        // 清理临时目录
        try { System.IO.Directory.Delete(sessionDir, true); }
        catch { /* ignore */ }

        var fileSize = new System.IO.FileInfo(filePath).Length;
        if (fileSize > MaxFileSize)
        {
            System.IO.File.Delete(filePath);
            return BadRequest(new { error = $"合并后文件超过 {MaxFileSize / (1024 * 1024)}MB 限制" });
        }

        var image = new Image
        {
            FileName = Path.GetFileName(fileName),
            StoredName = storedName,
            Url = $"/uploads/{storedName}",
            FileSize = fileSize,
            ContentType = MimeContentType(ext) ?? "application/octet-stream",
            Category = cat,
            UploadedAt = DateTime.UtcNow
        };
        _db.Images.Add(image);
        await _db.SaveChangesAsync();

        return Ok(new ImageDto(image.Id, image.FileName, image.FileSize, image.ContentType, image.Category, image.UploadedAt));
    }

    /// <summary>获取缩略图 / 预览图：
    /// 所有格式统一转 JPEG 缓存，按 ?size 区分尺寸：
    ///   - size=thumb（默认）→ 480px 宽，用于卡片网格
    ///   - size=medium        → 1600px 宽，用于 Lightbox 查看 RAW/ARW（浏览器无法直接渲染 RAW 二进制）
    /// RAW/ARW 用多格式解码回退链；通用格式直接读；失败写负缓存避免反复重试。
    /// </summary>
    [HttpGet("{id:int}/preview")]
    public async Task<IActionResult> Preview(int id, [FromQuery] string? size, CancellationToken ct)
    {
        var image = await _db.Images.FindAsync(new object[] { id }, ct);
        if (image is null) return NotFound(new { error = "图片不存在" });

        var ext = Path.GetExtension(image.StoredName);
        var sourcePath = Path.Combine(_uploadsDir, image.StoredName);
        if (!System.IO.File.Exists(sourcePath))
            return NotFound(new { error = "文件未找到" });

        // 按 size 选择目标宽度
        var targetWidth = size == "medium" ? MediumMaxWidth : ThumbMaxWidth;
        var baseName = Path.GetFileNameWithoutExtension(image.StoredName);

        Directory.CreateDirectory(_previewCacheDir);
        // 缓存路径按宽度分文件，避免不同尺寸互相覆盖
        var cacheFile = Path.Combine(_previewCacheDir, $"{baseName}_{targetWidth}.jpg");
        var failMark = cacheFile + ".fail";
        var sourceMtime = System.IO.File.GetLastWriteTimeUtc(sourcePath);

        // 负缓存：转码失败过且源文件没变动，25 分钟内不重试
        if (System.IO.File.Exists(failMark)
            && System.IO.File.GetLastWriteTimeUtc(failMark) >= sourceMtime
            && (DateTime.UtcNow - System.IO.File.GetLastWriteTimeUtc(failMark)).TotalMinutes <= 25)
        {
            return StatusCode(StatusCodes.Status500InternalServerError,
                new { error = "预览生成失败（负缓存，稍后重试）" });
        }

        // 缓存有效 → 直接返回
        if (System.IO.File.Exists(cacheFile) && System.IO.File.GetLastWriteTimeUtc(cacheFile) >= sourceMtime)
        {
            return PhysicalFile(cacheFile, "image/jpeg", true);
        }

        // 生成缩略图
        try
        {
            MagickImage? magick = null;
            Exception? lastErr = null;

            if (RawExtensions.Contains(ext))
            {
                // RAW/ARW：按扩展名给出最优解码顺序，匹配不到则回退，确保真实 RAW 和同容器测试文件都能解码
                var extLc = ext.ToLowerInvariant();
                var readFormats = extLc switch
                {
                    ".arw" => new[] { MagickFormat.Arw, MagickFormat.Dng, MagickFormat.Tiff, MagickFormat.Unknown },
                    ".raw" => new[] { MagickFormat.Dng, MagickFormat.Tiff, MagickFormat.Arw, MagickFormat.Miff, MagickFormat.Unknown },
                    _ => new[] { MagickFormat.Unknown }
                };
                foreach (var fmt in readFormats)
                {
                    try
                    {
                        var settings = new MagickReadSettings { Format = fmt };
                        magick = new MagickImage(sourcePath, settings);
                        if (magick.Width > 0) break;
                    }
                    catch (Exception e)
                    {
                        lastErr = e;
                        magick?.Dispose();
                        magick = null;
                    }
                }
            }
            else
            {
                // 通用格式（jpg/png/webp/gif/bmp）直接读取
                magick = new MagickImage(sourcePath);
            }

            if (magick is null)
            {
                try { System.IO.File.WriteAllText(failMark, lastErr?.Message ?? "无法解码"); } catch { /* ignore */ }
                return StatusCode(StatusCodes.Status500InternalServerError,
                    new { error = $"生成预览失败：{lastErr?.Message ?? "无法解码该文件"}" });
            }

            using (magick)
            {
                if (magick.Width > targetWidth)
                {
                    // 按宽度等比缩放，计算等比高度（MagickGeometry(480,0) 不生效，必须给实际高度）
                    var newHeight = (uint)(magick.Height * (double)targetWidth / magick.Width);
                    magick.Resize((uint)targetWidth, newHeight);
                }
                magick.Quality = ThumbQuality;
                magick.Format = MagickFormat.Jpeg;
                await magick.WriteAsync(cacheFile, ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "生成缩略图失败: {Name} (size={Size})", image.StoredName, size);
            try { System.IO.File.WriteAllText(failMark, ex.Message); } catch { /* ignore */ }
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = $"生成预览失败：{ex.Message}" });
        }

        return PhysicalFile(cacheFile, "image/jpeg", true);
    }

    /// <summary>获取原图：点击"查看原图 / 下载原图"时才调用，以二进制流形式返回原始文件（支持 Range 断点续传）。
    /// 注意：静态 /uploads 目录已不再对外公开，原图只能通过该接口按 ID 按需获取。
    /// </summary>
    [HttpGet("{id:int}/raw")]
    public async Task<IActionResult> Raw(int id, CancellationToken ct)
    {
        var image = await _db.Images.FindAsync(new object[] { id }, ct);
        if (image is null) return NotFound(new { error = "图片不存在" });

        var sourcePath = Path.Combine(_uploadsDir, image.StoredName);
        if (!System.IO.File.Exists(sourcePath))
            return NotFound(new { error = "文件未找到" });

        var ext = Path.GetExtension(image.StoredName);
        var contentType = MimeContentType(ext) ?? image.ContentType ?? "application/octet-stream";
        Response.GetTypedHeaders().LastModified = System.IO.File.GetLastWriteTimeUtc(sourcePath);
        var cd = new System.Net.Http.Headers.ContentDispositionHeaderValue("inline")
        {
            FileNameStar = image.FileName,
            FileName = Uri.EscapeDataString(image.FileName)
        };
        Response.Headers.ContentDisposition = cd.ToString();
        return PhysicalFile(sourcePath, contentType, true);
    }

    /// <summary>按扩展名映射 MIME Content-Type，PhysicalFile 返回时写正确的响应头</summary>
    private static string? MimeContentType(string ext)
    {
        return (ext ?? string.Empty).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".bmp" => "image/bmp",
            ".raw" or ".arw" => "application/octet-stream",
            _ => null
        };
    }

    /// <summary>删除指定图片（同步清除预览缓存）</summary>
    [Authorize]
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        var image = await _db.Images.FindAsync(id);
        if (image is null) return NotFound();

        var filePath = Path.Combine(_uploadsDir, image.StoredName);
        if (System.IO.File.Exists(filePath))
        {
            try { System.IO.File.Delete(filePath); }
            catch (Exception ex) { _logger.LogWarning(ex, "删除文件失败: {Name}", image.StoredName); }
        }

        // 清理所有尺寸的缩略图缓存 + 旧的 .fail 标记
        var baseName = Path.GetFileNameWithoutExtension(image.StoredName);
        if (System.IO.Directory.Exists(_previewCacheDir))
        {
            foreach (var f in System.IO.Directory.EnumerateFiles(_previewCacheDir, baseName + "*", System.IO.SearchOption.TopDirectoryOnly))
            {
                try { System.IO.File.Delete(f); }
                catch (Exception ex) { _logger.LogWarning(ex, "删除预览缓存失败: {File}", f); }
            }
        }

        _db.Images.Remove(image);
        await _db.SaveChangesAsync();

        return NoContent();
    }

    /// <summary>批量删除图片</summary>
    [Authorize]
    [HttpDelete("batch")]
    public async Task<IActionResult> DeleteBatch([FromBody] BatchDeleteRequest? req)
    {
        if (req is null || req.Ids is null || req.Ids.Count == 0)
            return BadRequest(new { error = "未指定要删除的图片 ID" });

        var ids = req.Ids.Distinct().ToList();
        var images = await _db.Images.Where(i => ids.Contains(i.Id)).ToListAsync();
        if (images.Count == 0)
            return NotFound(new { error = "未找到任何图片" });

        foreach (var image in images)
        {
            var filePath = Path.Combine(_uploadsDir, image.StoredName);
            if (System.IO.File.Exists(filePath))
            {
                try { System.IO.File.Delete(filePath); }
                catch (Exception ex) { _logger.LogWarning(ex, "删除文件失败: {Name}", image.StoredName); }
            }

            var baseName = Path.GetFileNameWithoutExtension(image.StoredName);
            if (System.IO.Directory.Exists(_previewCacheDir))
            {
                foreach (var f in System.IO.Directory.EnumerateFiles(_previewCacheDir, baseName + "*", System.IO.SearchOption.TopDirectoryOnly))
                {
                    try { System.IO.File.Delete(f); }
                    catch (Exception ex) { _logger.LogWarning(ex, "删除预览缓存失败: {File}", f); }
                }
            }
        }

        _db.Images.RemoveRange(images);
        await _db.SaveChangesAsync();

        return Ok(new { deleted = images.Count });
    }

    /// <summary>修改图片分类</summary>
    [Authorize]
    [HttpPatch("{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] UpdateImageRequest? req)
    {
        var image = await _db.Images.FindAsync(id);
        if (image is null) return NotFound(new { error = "图片不存在" });

        if (req is null)
            return BadRequest(new { error = "请求体为空" });

        // 改分类
        if (!string.IsNullOrWhiteSpace(req.Category))
        {
            var cat = req.Category.Trim();
            if (cat.Length > 64) cat = cat[..64];
            // 分类不存在时自动创建
            if (!await _db.Categories.AnyAsync(c => c.Name == cat))
            {
                _db.Categories.Add(new Category { Name = cat, CreatedAt = DateTime.UtcNow });
                await _db.SaveChangesAsync();
            }
            image.Category = cat;
        }

        // 改文件名（安全化）
        if (!string.IsNullOrWhiteSpace(req.FileName))
        {
            image.FileName = Path.GetFileName(req.FileName.Trim());
        }

        await _db.SaveChangesAsync();
        return Ok(new ImageDto(image.Id, image.FileName, image.FileSize, image.ContentType, image.Category, image.UploadedAt));
    }

    /// <summary>批量修改分类</summary>
    [Authorize]
    [HttpPatch("batch")]
    public async Task<IActionResult> UpdateBatch([FromBody] BatchUpdateRequest? req)
    {
        if (req is null || req.Ids is null || req.Ids.Count == 0 || string.IsNullOrWhiteSpace(req.Category))
            return BadRequest(new { error = "参数不完整" });

        var cat = req.Category.Trim();
        if (cat.Length > 64) cat = cat[..64];
        if (!await _db.Categories.AnyAsync(c => c.Name == cat))
        {
            _db.Categories.Add(new Category { Name = cat, CreatedAt = DateTime.UtcNow });
            await _db.SaveChangesAsync();
        }

        var ids = req.Ids.Distinct().ToList();
        var images = await _db.Images.Where(i => ids.Contains(i.Id)).ToListAsync();
        foreach (var img in images)
            img.Category = cat;

        await _db.SaveChangesAsync();
        return Ok(new { updated = images.Count });
    }

    /// <summary>获取图片 EXIF 信息（相机型号、拍摄时间、光圈、快门、ISO、焦距等）</summary>
    [HttpGet("{id:int}/exif")]
    public async Task<IActionResult> GetExif(int id, CancellationToken ct)
    {
        var image = await _db.Images.FindAsync(new object[] { id }, ct);
        if (image is null) return NotFound(new { error = "图片不存在" });

        var sourcePath = Path.Combine(_uploadsDir, image.StoredName);
        if (!System.IO.File.Exists(sourcePath))
            return NotFound(new { error = "文件未找到" });

        var ext = Path.GetExtension(image.StoredName);
        try
        {
            using var magick = RawExtensions.Contains(ext)
                ? TryReadRaw(sourcePath, ext)
                : new MagickImage(sourcePath);

            if (magick is null)
                return Ok(new { exif = (Dictionary<string, string>?)null, note = "无法读取该文件的 EXIF 信息" });

            var profile = magick.GetExifProfile();
            if (profile is null || profile.Values.Count() == 0)
                return Ok(new { exif = (Dictionary<string, string>?)null, note = "该图片没有 EXIF 信息" });

            var result = new Dictionary<string, string>();
            foreach (var v in profile.Values)
            {
                var key = v.Tag.ToString();
                var val = v.ToString();
                if (!string.IsNullOrEmpty(val))
                    result[key] = val;
            }
            return Ok(new { exif = result });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "读取 EXIF 失败: {Name}", image.StoredName);
            return Ok(new { exif = (Dictionary<string, string>?)null, note = $"读取失败：{ex.Message}" });
        }
    }

    /// <summary>尝试用多格式回退链读取 RAW/ARW 文件</summary>
    private MagickImage? TryReadRaw(string sourcePath, string ext)
    {
        var extLc = ext.ToLowerInvariant();
        var readFormats = extLc switch
        {
            ".arw" => new[] { MagickFormat.Arw, MagickFormat.Dng, MagickFormat.Tiff, MagickFormat.Unknown },
            ".raw" => new[] { MagickFormat.Dng, MagickFormat.Tiff, MagickFormat.Arw, MagickFormat.Miff, MagickFormat.Unknown },
            _ => new[] { MagickFormat.Unknown }
        };
        foreach (var fmt in readFormats)
        {
            try
            {
                var settings = new MagickReadSettings { Format = fmt };
                var m = new MagickImage(sourcePath, settings);
                if (m.Width > 0) return m;
                m.Dispose();
            }
            catch { /* try next */ }
        }
        return null;
    }
}

public record ImageDto(int Id, string FileName, long FileSize, string ContentType, string Category, DateTime UploadedAt);
public record BatchDeleteRequest(List<int> Ids);
public record UpdateImageRequest(string? Category, string? FileName);
public record BatchUpdateRequest(List<int> Ids, string Category);
