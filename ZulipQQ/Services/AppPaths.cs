using System;
using System.IO;
using System.Runtime.InteropServices;

namespace ZulipQQ.Services;

/// <summary>本地数据和默认保存目录的位置。</summary>
public static class AppPaths
{
    /// <summary>
    /// 聊天信息（数据目录）：数据库、日志、缓存、打开过的文件、插件数据等。默认为 <see cref="DataLocation.DefaultRoot"/>，
    /// 可在设置中移动（见 <see cref="DataLocation"/>）。启动时确定；只有启动时的搬迁会修改它。
    /// </summary>
    public static string DataRoot { get; internal set; } = DataLocation.ResolveRoot();

    public static string DatabasePath => Path.Combine(DataRoot, "zulipqq.db");

    public static string ImageCacheDir => Path.Combine(DataRoot, "cache", "images");

    public static string CacheDir => Path.Combine(DataRoot, "cache");

    public static string LogDir => Path.Combine(DataRoot, "logs");

    /// <summary>直接打开的聊天文件：<c>files\服务器\path_id 的目录部分\文件名</c>。</summary>
    public static string FilesDir => Path.Combine(DataRoot, "files");

    public static string PluginDataDir(string pluginId) => Path.Combine(DataRoot, "plugin-data", pluginId);

    /// <summary>接收或下载的文件默认保存到的目录：当前用户的“下载”文件夹。</summary>
    public static string DefaultSaveRoot { get; } = DownloadsFolder();

    private static string DownloadsFolder()
    {
        var id = new Guid("374DE290-123F-4565-9164-39C4925E467B"); // FOLDERID_Downloads
        if (SHGetKnownFolderPath(ref id, 0, IntPtr.Zero, out var path) == 0 && !string.IsNullOrEmpty(path))
            return path;
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetKnownFolderPath(ref Guid id, uint flags, IntPtr token, out string path);
}
