namespace ZulipQQ.Update;

/// <summary>
/// 替换程序目录中的文件：旧文件先移到备份目录，再复制新文件。只动旧版本和新版本清单中列出的文件，
/// 程序目录中的其它文件（用户自己的文件、自行安装的插件）不受影响。
/// <see cref="Commit"/> 删除备份（移除旧版本），<see cref="Rollback"/> 恢复原样。
/// </summary>
public sealed class FileSwap(string appDir, Action<string> log)
{
    private readonly string _backupDir = Path.Combine(appDir, UpdateLayout.BackupDirName);
    private readonly List<string> _moved = [];
    private readonly List<string> _installed = [];

    /// <summary>重试文件操作（文件可能被杀毒软件或刚退出的进程短暂占用）的等待间隔；测试中可以缩短。</summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromMilliseconds(300);

    public int RetryCount { get; init; } = 10;

    /// <summary>执行替换。失败时抛出异常，调用方应调用 <see cref="Rollback"/>。</summary>
    public void Apply(IEnumerable<string> oldPaths, string stagedDir, IReadOnlyList<FileEntry> newEntries)
    {
        DeleteDirectory(_backupDir);
        // 新版本的文件如果和目录中已有的文件同名，也要先备份才能回滚
        var newPaths = newEntries.Select(e => e.Path).Append(UpdateLayout.FileListName).ToList();
        var existing = oldPaths.Append(UpdateLayout.FileListName).Concat(newPaths).Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var path in existing)
        {
            var source = Path.Combine(appDir, path);
            if (!File.Exists(source))
                continue;
            var target = Path.Combine(_backupDir, path);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            Retry(() => File.Move(source, target));
            _moved.Add(path);
        }
        foreach (var path in newPaths)
        {
            var target = Path.Combine(appDir, path);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            // 先记录再复制：复制到一半失败时残留的文件也要清除
            _installed.Add(path);
            Retry(() => File.Copy(Path.Combine(stagedDir, path), target, overwrite: true));
        }
        PackageVerifier.VerifyFiles(appDir, newEntries);
    }

    /// <summary>恢复替换前的文件。返回是否全部恢复；个别文件失败时继续恢复其余文件，备份目录保留。</summary>
    public bool Rollback()
    {
        var ok = true;
        foreach (var path in _installed)
        {
            try
            {
                var target = Path.Combine(appDir, path);
                if (File.Exists(target))
                    Retry(() => DeleteFile(target));
            }
            catch (Exception ex) when (IsFileError(ex))
            {
                ok = false;
                log($"回滚时无法删除新文件 {path}：{ex.Message}");
            }
        }
        foreach (var path in _moved)
        {
            try
            {
                Retry(() => File.Move(Path.Combine(_backupDir, path), Path.Combine(appDir, path), overwrite: true));
            }
            catch (Exception ex) when (IsFileError(ex))
            {
                ok = false;
                log($"回滚时无法恢复旧文件 {path}：{ex.Message}");
            }
        }
        PruneEmptyDirectories(_installed);
        if (ok)
            TryDeleteDirectory(_backupDir);
        return ok;
    }

    /// <summary>更新成功：删除备份，以及旧版本文件移走后变空的目录。删不掉时只记日志，下次启动再清理。</summary>
    public void Commit()
    {
        TryDeleteDirectory(_backupDir);
        PruneEmptyDirectories(_moved);
    }

    /// <summary>删除程序目录中残留的备份（更新器被中断时可能留下）。</summary>
    public static void RemoveLeftoverBackup(string appDir)
    {
        var backup = Path.Combine(appDir, UpdateLayout.BackupDirName);
        if (Directory.Exists(backup))
            DeleteDirectory(backup);
    }

    private void PruneEmptyDirectories(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            var dir = Path.GetDirectoryName(path);
            while (!string.IsNullOrEmpty(dir))
            {
                var full = Path.Combine(appDir, dir);
                try
                {
                    if (!Directory.Exists(full) || Directory.EnumerateFileSystemEntries(full).Any())
                        break;
                    Directory.Delete(full);
                }
                catch (Exception ex) when (IsFileError(ex))
                {
                    break;
                }
                dir = Path.GetDirectoryName(dir);
            }
        }
    }

    private void TryDeleteDirectory(string dir)
    {
        try
        {
            DeleteDirectory(dir);
        }
        catch (Exception ex) when (IsFileError(ex))
        {
            log($"无法删除 {dir}：{ex.Message}");
        }
    }

    internal static void DeleteDirectory(string dir)
    {
        if (!Directory.Exists(dir))
            return;
        foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(dir, recursive: true);
    }

    private static void DeleteFile(string path)
    {
        File.SetAttributes(path, FileAttributes.Normal);
        File.Delete(path);
    }

    private void Retry(Action action)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (Exception ex) when (IsFileError(ex) && attempt < RetryCount)
            {
                Thread.Sleep(RetryDelay);
            }
        }
    }

    internal static bool IsFileError(Exception ex) => ex is IOException or UnauthorizedAccessException;
}
