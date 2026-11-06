using System.Text.Json;
using System.Text.Json.Serialization;

namespace ZulipQQ.Update;

/// <summary>Release 附带的 <see cref="FileName"/>：最新版本和更新包的大小、SHA-256。</summary>
public sealed record UpdateManifest(string Version, string Package, string Sha256, long Size)
{
    /// <summary>Release 中清单文件的名称；由 tools/pack-release.ps1 生成。</summary>
    public const string FileName = "ZulipQQ-update.json";

    /// <summary>更新包大小的上限，防止清单错误时写满磁盘。</summary>
    public const long MaxPackageSize = 2L * 1024 * 1024 * 1024;

    /// <summary>解析并检查清单；内容不合法时返回 null。</summary>
    public static UpdateManifest? Parse(string json)
    {
        UpdateManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize(json, UpdateJsonContext.Default.UpdateManifest);
        }
        catch (JsonException)
        {
            return null;
        }
        return manifest is not null && manifest.IsValid ? manifest : null;
    }

    public string ToJson() => JsonSerializer.Serialize(this, UpdateJsonContext.Default.UpdateManifest);

    // 包名只能是一个文件名：下载地址由它拼接，不能借此跳到别的路径
    private bool IsValid =>
        AppVersion.TryParse(Version) is not null
        && !string.IsNullOrEmpty(Package)
        && Package.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_')
        && !Package.StartsWith('.')
        && Package.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
        && Sha256 is { Length: 64 }
        && Sha256.All(char.IsAsciiHexDigit)
        && Size is > 0 and <= MaxPackageSize;
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(UpdateManifest))]
[JsonSerializable(typeof(UpdateState))]
internal sealed partial class UpdateJsonContext : JsonSerializerContext;
