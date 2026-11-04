using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using ZulipQQ.Core;
using ZulipQQ.Data;

namespace ZulipQQ.Services;

public enum DownloadState
{
    NotDownloaded,
    Downloading,
    Downloaded,
}

/// <summary>
/// 聊天文件的打开与保存。打开的文件下载到数据目录（见 <see cref="CacheFolder"/>），下载记录保存在 SQLite（<c>downloaded_file</c>）；
/// 保存的文件直接存到“接收或下载的文件保存到”的目录，不记录。只在 UI 线程上调用。
/// </summary>
public sealed class FileService(ZulipSession session, DownloadRepository repo)
{
    // 直接打开可能执行代码的文件类型：下载后只在资源管理器中定位，不直接运行
    private static readonly HashSet<string> ExecutableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".com", ".bat", ".cmd", ".scr", ".pif", ".msi", ".msp", ".ps1", ".psm1", ".vbs", ".vbe",
        ".js", ".jse", ".wsf", ".wsh", ".hta", ".lnk", ".jar", ".reg", ".cpl", ".url", ".appref-ms",
    };

    // 同一个附件同时只下载一次
    private readonly Dictionary<string, Task<string?>> _inflight = [];
    private readonly Dictionary<string, int> _progress = [];

    // 正在下载使用的本地路径，避免两个同名文件同时下载时得到同一个路径
    private readonly HashSet<string> _reservedPaths = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, Task<long?>> _sizes = [];

    /// <summary>某个附件的下载状态或进度变化，参数为上传路径。</summary>
    public event EventHandler<string>? StateChanged;

    /// <summary>
    /// 直接打开的文件所在的目录：<c>数据目录\files\服务器\path_id 的目录部分</c>。
    /// path_id（<c>组织id/随机目录/随机串/文件名</c>）在服务器内唯一，不同文件即使同名也不会冲突，文件仍保留原名。
    /// </summary>
    public string CacheFolder(UploadLink link)
    {
        var realm = session.Client.RealmUrl;
        var host = realm.IsDefaultPort ? realm.Host : $"{realm.Host}_{realm.Port}";
        const string prefix = "/user_uploads/";
        var relative = link.Path.StartsWith(prefix, StringComparison.Ordinal) ? link.Path[prefix.Length..] : link.Path.TrimStart('/');
        var segments = relative.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return Path.Combine([AppPaths.FilesDir, FileNameDeduper.SanitizeDirectoryName(host),
            .. segments[..^1].Select(FileNameDeduper.SanitizeDirectoryName)]);
    }

    public DownloadState GetState(string uploadPath)
    {
        if (_inflight.ContainsKey(uploadPath))
            return DownloadState.Downloading;
        return LocalPath(uploadPath) is null ? DownloadState.NotDownloaded : DownloadState.Downloaded;
    }

    /// <summary>下载进度百分比；没有在下载时为 null。</summary>
    public int? GetProgress(string uploadPath) => _progress.TryGetValue(uploadPath, out var p) ? p : null;

    /// <summary>已下载文件的本地路径。记录存在但文件已被删除时清除记录并返回 null。</summary>
    public string? LocalPath(string uploadPath)
    {
        if (repo.Find(uploadPath) is not { } record)
            return null;
        if (File.Exists(record.LocalPath))
            return record.LocalPath;
        repo.Delete(uploadPath);
        return null;
    }

    /// <summary>最近下载或打开的文件（本地记录），最近的在前。</summary>
    public IReadOnlyList<DownloadRecord> Recent(int limit) => repo.Recent(limit);

    /// <summary>文件大小（字节），会话期间缓存；无法获取时为 null。</summary>
    public Task<long?> GetSizeAsync(string uploadPath)
    {
        if (!_sizes.TryGetValue(uploadPath, out var task))
        {
            task = session.Client.HeadSizeAsync(uploadPath, session.Token);
            _sizes[uploadPath] = task;
        }
        return task;
    }

    /// <summary>下载到默认目录，返回本地路径；失败时返回 null。同一个附件正在下载时返回同一个任务。</summary>
    public Task<string?> DownloadAsync(UploadLink link, ConversationKey key, long messageId)
    {
        if (_inflight.TryGetValue(link.Path, out var running))
            return running;
        var task = DownloadCoreAsync(link, key, messageId);
        _inflight[link.Path] = task;
        StateChanged?.Invoke(this, link.Path);
        return task;
    }

    /// <summary>“已保存为 文件名”：重名时文件名带序号，提示实际的名字。</summary>
    public static string SavedToast(string path) => $"已保存为 {Path.GetFileName(path)}";

    /// <summary>
    /// 打开文件（点击聊天中的文件也是这个）：未下载时先下载到数据目录（<see cref="CacheFolder"/>），以只读方式打开
    /// （设置只读属性，编辑后只能另存，数据目录中的文件保持与收到的一致）。可执行文件不直接运行，只在资源管理器中定位。返回是否成功。
    /// </summary>
    public async Task<bool> OpenAsync(UploadLink link, ConversationKey key, long messageId)
    {
        var path = LocalPath(link.Path) ?? await DownloadAsync(link, key, messageId);
        if (path is null || session.IsDisposed)
            return false;
        if (ExecutableExtensions.Contains(Path.GetExtension(path)))
        {
            RevealInFolder(path);
            return true;
        }
        try
        {
            File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            repo.MarkOpened(link.Path, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"打开文件失败：{path}", ex);
            return false;
        }
    }

    /// <summary>
    /// 保存：以原文件名直接存到“接收或下载的文件保存到”的目录（不分子目录），重名时加序号。已下载时复制，否则下载。
    /// 不写下载记录。返回保存的路径；失败时返回 null。
    /// </summary>
    public async Task<string?> SaveAsync(UploadLink link)
    {
        string? path = null;
        string? part = null;
        try
        {
            var dir = session.Settings.Local.SaveRoot;
            Directory.CreateDirectory(dir);
            path = FileNameDeduper.NextFreePath(dir, link.FileName, p => _reservedPaths.Contains(p) || File.Exists(p + ".part"));
            _reservedPaths.Add(path);
            part = path + ".part";
            await using (var target = new FileStream(part, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                if (LocalPath(link.Path) is { } local)
                {
                    await using var source = File.OpenRead(local);
                    await source.CopyToAsync(target, session.Token);
                }
                else
                {
                    await session.Client.DownloadAsync(link.Path, target, ct: session.Token);
                }
            }
            File.Move(part, path);
            MarkFromInternet(path);
            return path;
        }
        catch (Exception ex)
        {
            if (!session.IsDisposed)
                Log.Warn($"保存文件失败：{link.Path}", ex);
            if (part is not null)
                TryDelete(part);
            return null;
        }
        finally
        {
            if (path is not null)
                _reservedPaths.Remove(path);
        }
    }

    /// <summary>另存为：选择位置后下载（已下载时直接复制）。不写下载记录。返回 false 表示失败（取消不算失败）。</summary>
    public async Task<bool> SaveAsAsync(UploadLink link, TopLevel top)
    {
        var storage = top.StorageProvider;
        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            SuggestedFileName = link.FileName,
            SuggestedStartLocation = await storage.TryGetWellKnownFolderAsync(WellKnownFolder.Downloads),
            ShowOverwritePrompt = true,
        });
        if (file is null || session.IsDisposed)
            return true;
        // 选中了已下载的文件本身：打开写入流会先把它清空，而内容本来就已经在那里
        var local = LocalPath(link.Path);
        if (local is not null && file.TryGetLocalPath() is { } chosen
            && string.Equals(Path.GetFullPath(chosen), Path.GetFullPath(local), StringComparison.OrdinalIgnoreCase))
            return true;

        try
        {
            await using (var target = await file.OpenWriteAsync())
            {
                if (local is not null)
                {
                    await using var source = File.OpenRead(local);
                    await source.CopyToAsync(target, session.Token);
                }
                else
                {
                    await session.Client.DownloadAsync(link.Path, target, ct: session.Token);
                }
            }
            // 流复制不会带上原文件的 Zone.Identifier，另存的文件同样标记为来自网络
            if (file.TryGetLocalPath() is { } saved)
                MarkFromInternet(saved);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn($"另存为失败：{link.Path}", ex);
            return false;
        }
    }

    /// <summary>在资源管理器中定位文件。</summary>
    public static void RevealInFolder(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = false });
        }
        catch (Exception ex)
        {
            Log.Warn($"打开文件夹失败：{path}", ex);
        }
    }

    private async Task<string?> DownloadCoreAsync(UploadLink link, ConversationKey key, long messageId)
    {
        // 先让调用方登记进行中的任务，保证 finally 中的移除发生在登记之后
        await Task.Yield();
        string? path = null;
        string? part = null;
        try
        {
            var dir = CacheFolder(link);
            Directory.CreateDirectory(dir);
            path = FileNameDeduper.NextFreePath(dir, link.FileName, p => _reservedPaths.Contains(p) || File.Exists(p + ".part"));
            _reservedPaths.Add(path);
            part = path + ".part";

            var progress = new Progress<(long Done, long? Total)>(p =>
            {
                if (p.Total is not > 0)
                    return;
                var percent = (int)(p.Done * 100 / p.Total.Value);
                if (_progress.GetValueOrDefault(link.Path, -1) == percent)
                    return;
                _progress[link.Path] = percent;
                StateChanged?.Invoke(this, link.Path);
            });
            await using (var stream = new FileStream(part, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                await session.Client.DownloadAsync(link.Path, stream, progress, session.Token);
            if (session.IsDisposed)
            {
                TryDelete(part);
                return null;
            }

            File.Move(part, path);
            MarkFromInternet(path);
            repo.Upsert(new DownloadRecord(link.Path, path, Path.GetFileName(path), new FileInfo(path).Length, messageId,
                key.ToStorageString(), DateTimeOffset.UtcNow.ToUnixTimeSeconds(), null));
            return path;
        }
        catch (Exception ex)
        {
            if (!session.IsDisposed)
                Log.Warn($"下载失败：{link.Path}", ex);
            if (part is not null)
                TryDelete(part);
            return null;
        }
        finally
        {
            if (path is not null)
                _reservedPaths.Remove(path);
            _inflight.Remove(link.Path);
            _progress.Remove(link.Path);
            if (!session.IsDisposed)
                StateChanged?.Invoke(this, link.Path);
        }
    }

    // 与浏览器一样写入 Mark of the Web，打开时由系统按来自网络的文件处理
    private static void MarkFromInternet(string path)
    {
        try
        {
            File.WriteAllText(path + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n");
        }
        catch (Exception ex)
        {
            Log.Debug($"写入 Zone.Identifier 失败：{path}", ex);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex)
        {
            Log.Debug($"删除临时文件失败：{path}", ex);
        }
    }
}
