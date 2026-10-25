using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Chat.Server.Common.Configuration;

/// <summary>
/// secrets.json 引导：该文件只存在本机/服务器（gitignore，不入库），用于 JWT 密钥、SMTP 凭据等敏感配置。
/// 加载顺序为可选加载（缺失不报错）；首次启动若缺少 JWT 密钥，则生成 32 字节随机 Base64 写入文件。
/// 注意：密钥值不写入日志、不放入任何响应。
/// </summary>
public static class SecretsBootstrapper
{
    /// <summary>密钥文件名（位于服务端内容根目录，即 server/Chat.Server/secrets.json）。</summary>
    public const string FileName = "secrets.json";

    private const string JwtKeyPath = "Jwt:Key";

    public static string GetPath(string contentRootPath) => Path.Combine(contentRootPath, FileName);

    /// <summary>把 secrets.json 挂到配置提供器链上（可选文件，缺失时跳过）。</summary>
    public static void AddJsonFile(ConfigurationManager configuration, string contentRootPath)
    {
        configuration.AddJsonFile(GetPath(contentRootPath), optional: true, reloadOnChange: false);
    }

    /// <summary>
    /// 确保 JWT 签名密钥存在：已有则跳过；没有则生成并写入 secrets.json（保留文件中的其他配置）。
    /// 返回是否本次新生成（仅用于日志提示，不返回/输出密钥值）。
    /// </summary>
    public static bool EnsureJwtKey(IConfiguration configuration, string contentRootPath)
    {
        if (!string.IsNullOrWhiteSpace(configuration[JwtKeyPath]))
        {
            return false;
        }

        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var path = GetPath(contentRootPath);
        var root = ReadExistingOrEmpty(path);

        if (root["Jwt"] is not JsonObject jwt)
        {
            jwt = new JsonObject();
            root["Jwt"] = jwt;
        }

        jwt["Key"] = key;

        var json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true })
            .Replace("\r\n", "\n", StringComparison.Ordinal) + "\n";
        File.WriteAllText(path, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        // 同进程内后续读取（options 绑定）使用内存值，避免依赖文件重新加载。
        configuration[JwtKeyPath] = key;
        return true;
    }

    private static JsonObject ReadExistingOrEmpty(string path)
    {
        if (!File.Exists(path))
        {
            return new JsonObject();
        }

        try
        {
            var text = File.ReadAllText(path);
            return string.IsNullOrWhiteSpace(text) ? new JsonObject() : JsonNode.Parse(text) as JsonObject ?? new JsonObject();
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("secrets.json 内容不是合法 JSON，请先修复该文件后再启动。", exception);
        }
    }
}
