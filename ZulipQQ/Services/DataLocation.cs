using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using ZulipQQ.Data;

namespace ZulipQQ.Services;

/// <summary>
/// 数据目录（设置中的“聊天信息默认保存到”）的位置与搬迁。
/// <para>
/// 位置记录在固定的 <c>%LocalAppData%\ZulipQQ\location.json</c> 中（数据库会随数据目录移动，不能记在数据库里）。
/// 更改位置时只记下目标（<see cref="RequestMove"/>）并重启程序；新进程在打开数据库之前搬迁（<see cref="Move"/>），
/// 此时没有任何文件被占用。
/// </para>
/// <para>
/// 搬迁先复制全部文件，再改写数据库中指向旧位置的路径，最后才切换位置并删除旧文件；
/// 复制或改写失败时删除已复制的文件，仍使用旧位置。
/// </para>
/// </summary>
public static class DataLocation
{
    /// <summary>默认的数据目录，同时也是 location.json 所在的目录（数据目录移走后它仍保留在这里）。</summary>
    public static string DefaultRoot { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZulipQQ");

    /// <summary>选择的文件夹中创建的子目录名：数据目录总是 <c>所选文件夹\ZulipQQ</c>。</summary>
    public const string FolderName = "ZulipQQ";

    private static string ConfigPath => Path.Combine(DefaultRoot, "location.json");

    private sealed record Config(string? DataRoot, string? PendingMoveTo);

    /// <summary>
    /// 启动时使用的数据目录：配置的目录存在时用它，否则用默认目录。
    /// 配置的目录不存在（例如移动硬盘未连接）时不改写配置，连接后再次启动仍使用它。
    /// 在 <see cref="AppPaths.DataRoot"/> 的初始化中调用：日志目录取决于它，这里不能写日志。
    /// </summary>
    public static string ResolveRoot() =>
        Load().DataRoot is { Length: > 0 } root && Directory.Exists(root) ? root : DefaultRoot;

    /// <summary>上次请求、还没有完成的搬迁目标；没有时为 null。</summary>
    public static string? PendingMove => Load().PendingMoveTo;

    /// <summary>
    /// 检查能否把数据目录搬到 <paramref name="target"/>，返回不能搬的原因；可以时返回 null。
    /// 会在目标位置创建目录并试写一个文件。
    /// </summary>
    public static string? Validate(string target)
    {
        var current = Normalize(AppPaths.DataRoot);
        target = Normalize(target);
        if (string.Equals(target, current, StringComparison.OrdinalIgnoreCase))
            return "已经是当前位置";
        if (IsInside(target, current) || IsInside(current, target))
            return "不能选择当前聊天信息文件夹之内或包含它的位置";
        if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any(p => !IsConfigFile(p)))
            return $"“{target}”不是空文件夹";
        try
        {
            Directory.CreateDirectory(target);
            var probe = Path.Combine(target, ".write-test");
            File.WriteAllText(probe, "");
            File.Delete(probe);
        }
        catch (Exception ex)
        {
            Log.Warn($"无法写入数据目录：{target}", ex);
            return "无法写入该位置";
        }
        var needed = Files(current).Sum(f => new FileInfo(f).Length);
        if (!SameVolume(current, target) && new DriveInfo(Path.GetPathRoot(target)!).AvailableFreeSpace < needed)
            return "磁盘空间不足";
        return null;
    }

    /// <summary>记下搬迁目标，下次启动时搬迁。调用前应先 <see cref="Validate"/>。</summary>
    public static void RequestMove(string target)
    {
        var config = Load();
        Save(config with { PendingMoveTo = Normalize(target) });
    }

    /// <summary>
    /// 启动时（打开数据库之前）把 <see cref="AppPaths.DataRoot"/> 搬到 <paramref name="target"/>。
    /// 无论成功与否都清除搬迁请求；成功后切换 <see cref="AppPaths.DataRoot"/> 与日志目录。
    /// 失败时抛出异常，数据目录不变。在后台线程上调用；<paramref name="progress"/> 报告已复制与总字节数。
    /// </summary>
    public static void Move(string target, IProgress<(long Done, long Total)> progress, CancellationToken ct = default)
    {
        var source = Normalize(AppPaths.DataRoot);
        target = Normalize(target);
        var config = Load();
        Save(config with { PendingMoveTo = null });

        var reason = Validate(target);
        if (reason is not null)
            throw new IOException(reason);

        var files = Files(source).ToList();
        var total = files.Sum(f => new FileInfo(f).Length);
        long done = 0;
        try
        {
            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();
                var dest = Path.Combine(target, Path.GetRelativePath(source, file));
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(file, dest, overwrite: true);
                done += new FileInfo(file).Length;
                progress.Report((done, total));
            }
            // 数据库在新位置打开、改写其中指向旧位置的路径；连接不使用连接池，Dispose 后即释放文件
            using (var db = LocalDb.Open(Path.Combine(target, "zulipqq.db")))
                RebasePaths(db, source, target);
        }
        catch
        {
            TryDeleteCopied(target);
            throw;
        }

        Save(config with { DataRoot = IsDefault(target) ? null : target, PendingMoveTo = null });
        AppPaths.DataRoot = target;
        Log.Directory = AppPaths.LogDir;
        Log.Info($"数据目录已从 {source} 移动到 {target}");
        DeleteOld(source, files);
    }

    /// <summary>
    /// 改写数据库中指向旧数据目录的绝对路径：已下载文件的本地路径，以及程序级、账号级设置中的路径
    /// （例如主题背景图，设置值可能是 JSON，其中的反斜杠是转义过的）。
    /// </summary>
    public static void RebasePaths(LocalDb db, string oldRoot, string newRoot)
    {
        var plain = new Regex(Regex.Escape(oldRoot) + @"(?=\\|""|$)", RegexOptions.IgnoreCase);
        var json = new Regex(Regex.Escape(JsonEscape(oldRoot)) + @"(?=\\\\|""|$)", RegexOptions.IgnoreCase);
        string Rebase(string value) => json.Replace(plain.Replace(value, _ => newRoot), _ => JsonEscape(newRoot));

        foreach (var (account, path, local) in db.Query("SELECT account_id, upload_path, local_path FROM downloaded_file;",
                     r => (r.GetInt64(0), r.GetString(1), r.GetString(2))))
        {
            var rebased = Rebase(local);
            if (rebased != local)
                db.Execute("UPDATE downloaded_file SET local_path = @l WHERE account_id = @a AND upload_path = @p;",
                    ("@l", rebased), ("@a", account), ("@p", path));
        }
        foreach (var (key, value) in db.Query("SELECT key, value FROM app_setting;", r => (r.GetString(0), r.GetString(1))))
        {
            var rebased = Rebase(value);
            if (rebased != value)
                db.Execute("UPDATE app_setting SET value = @v WHERE key = @k;", ("@v", rebased), ("@k", key));
        }
        foreach (var (account, key, value) in db.Query("SELECT account_id, key, value FROM account_setting;",
                     r => (r.GetInt64(0), r.GetString(1), r.GetString(2))))
        {
            var rebased = Rebase(value);
            if (rebased != value)
                db.Execute("UPDATE account_setting SET value = @v WHERE account_id = @a AND key = @k;",
                    ("@v", rebased), ("@a", account), ("@k", key));
        }
    }

    // 数据目录中要搬的文件：除 location.json 以外的全部文件
    private static IEnumerable<string> Files(string root) =>
        Directory.Exists(root)
            ? Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Where(f => !IsConfigFile(f))
            : [];

    // 已经复制、切换完成：删除旧文件与空目录，删不掉的（被其它程序占用）留下
    private static void DeleteOld(string source, IReadOnlyList<string> files)
    {
        foreach (var file in files)
        {
            try
            {
                DeleteFile(file);
            }
            catch (Exception ex)
            {
                Log.Warn($"删除旧数据失败：{file}", ex);
            }
        }
        if (!Directory.Exists(source))
            return;
        foreach (var dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
        {
            try
            {
                if (!Directory.EnumerateFileSystemEntries(dir).Any())
                    Directory.Delete(dir);
            }
            catch (Exception ex)
            {
                Log.Debug($"删除旧目录失败：{dir}", ex);
            }
        }
        if (!IsDefault(source))
        {
            try
            {
                if (!Directory.EnumerateFileSystemEntries(source).Any())
                    Directory.Delete(source);
            }
            catch (Exception ex)
            {
                Log.Debug($"删除旧目录失败：{source}", ex);
            }
        }
    }

    // 直接打开过的文件设置了只读属性（见 FileService.OpenAsync），删除前先去掉
    private static void DeleteFile(string path)
    {
        File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
        File.Delete(path);
    }

    // 搬迁失败：删除目标中已复制的内容（保留 location.json），目标是新建的文件夹时一并删除
    private static void TryDeleteCopied(string target)
    {
        try
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(target).Where(p => !IsConfigFile(p)))
            {
                if (Directory.Exists(entry))
                {
                    foreach (var file in Directory.EnumerateFiles(entry, "*", SearchOption.AllDirectories))
                        DeleteFile(file);
                    Directory.Delete(entry, recursive: true);
                }
                else
                {
                    DeleteFile(entry);
                }
            }
            if (!IsDefault(target) && !Directory.EnumerateFileSystemEntries(target).Any())
                Directory.Delete(target);
        }
        catch (Exception ex)
        {
            Log.Warn($"清理未完成的搬迁失败：{target}", ex);
        }
    }

    private static Config Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
                return JsonSerializer.Deserialize<Config>(File.ReadAllText(ConfigPath), JsonSerializerOptions.Web) ?? new Config(null, null);
        }
        catch (Exception)
        {
            // 文件损坏时视为没有配置（使用默认位置）；这里不能写日志，见 ResolveRoot
        }
        return new Config(null, null);
    }

    private static void Save(Config config)
    {
        Directory.CreateDirectory(DefaultRoot);
        if (config.DataRoot is null && config.PendingMoveTo is null)
        {
            File.Delete(ConfigPath);
            return;
        }
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(config, JsonSerializerOptions.Web));
    }

    private static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static bool IsDefault(string path) => string.Equals(Normalize(path), Normalize(DefaultRoot), StringComparison.OrdinalIgnoreCase);

    private static bool IsConfigFile(string path) => string.Equals(Normalize(path), Normalize(ConfigPath), StringComparison.OrdinalIgnoreCase);

    private static bool IsInside(string path, string root) =>
        path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static bool SameVolume(string a, string b) =>
        string.Equals(Path.GetPathRoot(a), Path.GetPathRoot(b), StringComparison.OrdinalIgnoreCase);

    private static string JsonEscape(string s) => s.Replace("\\", "\\\\");
}
