using System;
using System.IO;

namespace ZulipQQ.Services;

/// <summary>本地数据和默认下载目录的位置。</summary>
public static class AppPaths
{
    /// <summary><c>%LocalAppData%\ZulipQQ\</c>。</summary>
    public static string DataRoot { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZulipQQ");

    public static string DatabasePath => Path.Combine(DataRoot, "zulipqq.db");

    public static string ImageCacheDir => Path.Combine(DataRoot, "cache", "images");

    public static string CacheDir => Path.Combine(DataRoot, "cache");

    public static string LogDir => Path.Combine(DataRoot, "logs");

    public static string PluginDataDir(string pluginId) => Path.Combine(DataRoot, "plugin-data", pluginId);

    /// <summary>默认的聊天文件目录：<c>文档\ZulipQQ Files</c>。</summary>
    public static string DefaultDownloadRoot { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ZulipQQ Files");
}
