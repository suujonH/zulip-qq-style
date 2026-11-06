using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using ZulipQQ.Update;

namespace ZulipQQ.Tests.Update;

/// <summary>一个临时目录；测试结束时删除。</summary>
internal sealed class TempDir : IDisposable
{
    public TempDir() => Directory.CreateDirectory(Path);

    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ZulipQQ-update-test-" + Guid.NewGuid().ToString("N"));

    public string Sub(string relative) => System.IO.Path.Combine(Path, relative);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // 临时目录，删不掉不影响结果
        }
    }
}

internal static class PackageBuilder
{
    /// <summary>在目录中写入文件，并生成文件清单（与 tools/pack-release.ps1 相同的格式）。</summary>
    public static void WriteTree(string dir, IReadOnlyDictionary<string, string> files)
    {
        foreach (var (path, content) in files)
        {
            var full = Path.Combine(dir, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }
        File.WriteAllLines(Path.Combine(dir, UpdateLayout.FileListName), files.Keys.Select(p =>
            $"{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(files[p])))}  {p}"));
    }

    public static Dictionary<string, string> Version(string marker, params string[] extra)
    {
        var files = new Dictionary<string, string>
        {
            [UpdateLayout.AppExeName] = "app " + marker,
            [UpdateLayout.UpdaterExeName] = "updater " + marker,
        };
        foreach (var path in extra)
            files[path] = path + " " + marker;
        return files;
    }

    /// <summary>生成更新包，返回 zip 的内容和对应的清单。</summary>
    public static (byte[] Zip, UpdateManifest Manifest) Zip(string version, IReadOnlyDictionary<string, string> files, string workDir)
    {
        var tree = Path.Combine(workDir, "tree-" + Guid.NewGuid().ToString("N"));
        WriteTree(tree, files);
        var zipPath = Path.Combine(workDir, "pkg-" + Guid.NewGuid().ToString("N") + ".zip");
        ZipFile.CreateFromDirectory(tree, zipPath);
        var bytes = File.ReadAllBytes(zipPath);
        return (bytes, new UpdateManifest(version, $"ZulipQQ-v{version}-win-x64.zip", Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes.Length));
    }
}
