using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace ZulipQQ.Core;

/// <summary>下载文件时生成不重名的本地路径。</summary>
public static class FileNameDeduper
{
    private const int MaxAttempts = 9999;
    private const int MaxDirectoryNameLength = 60;

    private static readonly char[] InvalidFileNameChars = Path.GetInvalidFileNameChars();

    private static readonly HashSet<string> ReservedNames = new(
        ["CON", "PRN", "AUX", "NUL",
         "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
         "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"],
        StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 返回 <paramref name="directory"/> 下可用的文件路径：原名可用就用原名，否则依次尝试 name(1).ext、name(2).ext……
    /// “可用”指文件不存在，且 <paramref name="isTaken"/> 没有声明占用（例如另一个下载正在使用该路径）。
    /// </summary>
    /// <exception cref="IOException">尝试 9999 次仍没有可用的名字。</exception>
    public static string NextFreePath(string directory, string fileName, Func<string, bool>? isTaken = null)
    {
        var safe = EscapeReservedName(ReplaceInvalid(fileName).TrimEnd(' ', '.'));
        if (safe.Length == 0)
            safe = "_";

        var candidate = Path.Combine(directory, safe);
        if (IsFree(candidate, isTaken))
            return candidate;

        var name = Path.GetFileNameWithoutExtension(safe);
        var ext = Path.GetExtension(safe);
        for (var i = 1; i <= MaxAttempts; i++)
        {
            candidate = Path.Combine(directory, $"{name}({i}){ext}");
            if (IsFree(candidate, isTaken))
                return candidate;
        }

        throw new IOException($"无法为 {fileName} 找到可用的文件名");
    }

    /// <summary>把任意文字转为可作为目录名的形式：替换非法字符、去掉首尾空格和点、截断到 60 个字符。</summary>
    public static string SanitizeDirectoryName(string s)
    {
        var result = ReplaceInvalid(s).Trim(' ', '.');
        if (result.Length > MaxDirectoryNameLength)
            result = result[..MaxDirectoryNameLength].TrimEnd(' ', '.');
        return result.Length == 0 ? "_" : EscapeReservedName(result);
    }

    private static bool IsFree(string path, Func<string, bool>? isTaken) =>
        !File.Exists(path) && !Directory.Exists(path) && !(isTaken?.Invoke(path) ?? false);

    /// <summary>Windows 保留的设备名（CON、AUX.zip 等，不区分大小写和扩展名）前加 “_”。</summary>
    private static string EscapeReservedName(string name)
    {
        var dot = name.IndexOf('.');
        var stem = (dot >= 0 ? name[..dot] : name).TrimEnd(' ');
        return ReservedNames.Contains(stem) ? "_" + name : name;
    }

    private static string ReplaceInvalid(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
            sb.Append(InvalidFileNameChars.Contains(c) ? '_' : c);
        return sb.ToString();
    }
}
