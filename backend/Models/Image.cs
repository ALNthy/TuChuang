namespace TuchuangApi.Models;

public class Image
{
    public int Id { get; set; }

    /// <summary>原始文件名</summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>存储在磁盘上的唯一文件名</summary>
    public string StoredName { get; set; } = string.Empty;

    /// <summary>访问图片的相对 URL 路径</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>文件大小（字节）</summary>
    public long FileSize { get; set; }

    /// <summary>MIME 类型</summary>
    public string ContentType { get; set; } = string.Empty;

    /// <summary>图片分类</summary>
    public string Category { get; set; } = "其他";

    /// <summary>上传时间</summary>
    public DateTime UploadedAt { get; set; }
}
