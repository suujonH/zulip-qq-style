using System.IO.Compression;
using System.Security.Cryptography;

namespace ZulipQQ.Update;

/// <summary>更新包校验失败（内容损坏、被篡改或结构不对）。</summary>
public sealed class PackageInvalidException(string message) : Exception(message);

/// <summary>更新包的校验与解压。</summary>
public static class PackageVerifier
{
    /// <summary>校验更新包的大小和 SHA-256，不符时抛出 <see cref="PackageInvalidException"/>。</summary>
    public static void VerifyArchive(string zipPath, UpdateManifest manifest)
    {
        var length = new FileInfo(zipPath).Length;
        if (length != manifest.Size)
            throw new PackageInvalidException($"更新包大小不符：应为 {manifest.Size}，实际 {length}");
        var actual = HashFile(zipPath);
        if (!string.Equals(actual, manifest.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new PackageInvalidException($"更新包 SHA-256 不符：应为 {manifest.Sha256}，实际 {actual}");
    }

    /// <summary>解压到空目录；条目指向目录之外时抛出 <see cref="PackageInvalidException"/>。</summary>
    public static void ExtractSafely(string zipPath, string targetDir)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(targetDir)) + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(root);
        try
        {
            using var archive = ZipFile.OpenRead(zipPath);
            foreach (var entry in archive.Entries)
            {
                var path = Path.GetFullPath(Path.Combine(root, entry.FullName));
                if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    throw new PackageInvalidException($"更新包中的路径不合法：{entry.FullName}");
                if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
                {
                    Directory.CreateDirectory(path);
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                entry.ExtractToFile(path, overwrite: true);
            }
        }
        catch (InvalidDataException ex)
        {
            throw new PackageInvalidException($"更新包无法解压：{ex.Message}");
        }
    }

    /// <summary>
    /// 校验解压后的目录：必须有文件清单、主程序和更新器，清单中的每个文件都存在且 SHA-256 相符。返回清单。
    /// </summary>
    public static IReadOnlyList<FileEntry> VerifyTree(string dir)
    {
        var listPath = Path.Combine(dir, UpdateLayout.FileListName);
        if (!File.Exists(listPath))
            throw new PackageInvalidException($"更新包缺少文件清单 {UpdateLayout.FileListName}");
        var entries = FileEntry.ParseList(File.ReadAllLines(listPath));
        foreach (var required in new[] { UpdateLayout.AppExeName, UpdateLayout.UpdaterExeName })
        {
            if (!entries.Any(e => string.Equals(e.Path, required, StringComparison.OrdinalIgnoreCase)))
                throw new PackageInvalidException($"更新包缺少 {required}");
        }
        VerifyFiles(dir, entries);
        return entries;
    }

    /// <summary>清单中的每个文件都存在且 SHA-256 相符，否则抛出 <see cref="PackageInvalidException"/>。</summary>
    public static void VerifyFiles(string dir, IEnumerable<FileEntry> entries)
    {
        foreach (var entry in entries)
        {
            var path = Path.Combine(dir, entry.Path);
            if (!File.Exists(path))
                throw new PackageInvalidException($"缺少文件：{entry.Path}");
            if (!string.Equals(HashFile(path), entry.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new PackageInvalidException($"文件校验失败：{entry.Path}");
        }
    }

    public static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
}

/// <summary>文件清单的一项：相对路径和 SHA-256。</summary>
public sealed record FileEntry(string Path, string Sha256)
{
    /// <summary>解析 <c>sha256  相对路径</c> 格式的清单；格式不对或路径指向目录之外时抛出 <see cref="PackageInvalidException"/>。</summary>
    public static IReadOnlyList<FileEntry> ParseList(IEnumerable<string> lines)
    {
        var entries = new List<FileEntry>();
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0)
                continue;
            var space = line.IndexOf(' ');
            if (space != 64 || !line[..space].All(char.IsAsciiHexDigit))
                throw new PackageInvalidException($"文件清单格式不对：{line}");
            var path = line[(space + 1)..].Trim().TrimStart('*').Replace('/', '\\');
            if (!IsSafeRelativePath(path))
                throw new PackageInvalidException($"文件清单中的路径不合法：{path}");
            entries.Add(new FileEntry(path, line[..space]));
        }
        return entries;
    }

    // 清单决定替换和删除哪些文件：不能是绝对路径、盘符路径或含 ..，也不能指向备份目录
    private static bool IsSafeRelativePath(string path)
    {
        if (path.Length == 0 || System.IO.Path.IsPathRooted(path) || path.Contains(':'))
            return false;
        var parts = path.Split('\\');
        return !parts.Any(p => p is "" or "." or "..")
               && !string.Equals(parts[0], UpdateLayout.BackupDirName, StringComparison.OrdinalIgnoreCase);
    }
}
