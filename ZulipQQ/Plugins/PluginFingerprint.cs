using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace ZulipQQ.Plugins;

/// <summary>
/// 插件指纹：插件目录中所有文件（含子目录）的相对路径与内容的 SHA-256。任何文件新增、删除或修改都会改变指纹，
/// 需要用户重新确认开启。插件应把运行时数据写到 get_data_dir 返回的目录，而不是插件目录。
/// </summary>
public static class PluginFingerprint
{
    /// <summary>计算指纹（小写十六进制）；读取失败时返回 null。</summary>
    public static string? Compute(string directory)
    {
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var files = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                .Select(f => (Path: f, Relative: Path.GetRelativePath(directory, f).Replace('\\', '/')))
                .OrderBy(f => f.Relative, StringComparer.Ordinal);
            Span<byte> length = stackalloc byte[8];
            foreach (var (path, relative) in files)
            {
                hash.AppendData(Encoding.UTF8.GetBytes(relative));
                hash.AppendData([0]);
                var content = File.ReadAllBytes(path);
                BitConverter.TryWriteBytes(length, (long)content.Length);
                hash.AppendData(length);
                hash.AppendData(content);
            }
            return Convert.ToHexStringLower(hash.GetHashAndReset());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Services.Log.Warn($"计算插件指纹失败：{directory}", ex);
            return null;
        }
    }

    /// <summary>显示用的短格式：前 16 位，每 4 位一组。</summary>
    public static string Short(string fingerprint) =>
        string.Join(" ", Enumerable.Range(0, 4).Select(i => fingerprint.Substring(i * 4, 4)));
}
