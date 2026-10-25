namespace Chat.Server.Data.Entities;

/// <summary>files：上传文件元数据（二进制存磁盘 /data/files/yyyyMM/{fileId}{ext}）。</summary>
public sealed class StoredFile
{
    /// <summary>文件 ID（UUID 文本）。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>上传者用户 ID。</summary>
    public string OwnerId { get; set; } = string.Empty;

    /// <summary>原始文件名（展示用，不参与磁盘路径）。</summary>
    public string OriginalName { get; set; } = string.Empty;

    /// <summary>MIME 类型。</summary>
    public string Mime { get; set; } = string.Empty;

    /// <summary>文件大小（字节）。</summary>
    public long Size { get; set; }

    /// <summary>磁盘相对路径（服务端维护，不暴露给客户端）。</summary>
    public string StoragePath { get; set; } = string.Empty;

    /// <summary>创建时间（UTC 毫秒）。</summary>
    public long CreatedAt { get; set; }
}
