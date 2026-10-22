using System;
using System.Globalization;
using ZulipQQ.Data;

namespace ZulipQQ.Services;

/// <summary>新消息提醒的方式。</summary>
public enum NotificationStyle
{
    /// <summary>横幅弹窗（并闪烁任务栏）。</summary>
    Banner,

    /// <summary>只闪烁任务栏 / 托盘图标。</summary>
    TrayOnly,
}

/// <summary>点击聊天中未下载的文件时的行为。</summary>
public enum FileClickAction
{
    OpenAfterDownload,
    DownloadOnly,
}

/// <summary>
/// 只保存在本机的账号级设置（Zulip 没有的设置），读写 account_setting。
/// 属性赋值即保存，值有变化时触发 <see cref="Changed"/>。
/// </summary>
public sealed class LocalSettings(SettingRepository repository)
{
    private const string FontSizeKey = "font_size";
    private const string NotificationStyleKey = "notification_style";
    private const string DownloadRootKey = "download_root";
    private const string FileClickActionKey = "file_click_action";

    /// <summary>某项设置变化，参数为设置键（如 <c>font_size</c>）。</summary>
    public event EventHandler<string>? Changed;

    /// <summary>正文字号，12～18。</summary>
    public int FontSize
    {
        get => int.TryParse(repository.Get(FontSizeKey), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
            ? Math.Clamp(v, ThemeService.MinFontSize, ThemeService.MaxFontSize)
            : ThemeService.DefaultFontSize;
        set => Save(FontSizeKey, Math.Clamp(value, ThemeService.MinFontSize, ThemeService.MaxFontSize).ToString(CultureInfo.InvariantCulture));
    }

    public NotificationStyle NotificationStyle
    {
        get => Enum.TryParse<NotificationStyle>(repository.Get(NotificationStyleKey), out var v) ? v : NotificationStyle.Banner;
        set => Save(NotificationStyleKey, value.ToString());
    }

    /// <summary>聊天文件的下载根目录。</summary>
    public string DownloadRoot
    {
        get => repository.Get(DownloadRootKey) is { Length: > 0 } v ? v : AppPaths.DefaultDownloadRoot;
        set => Save(DownloadRootKey, value);
    }

    public FileClickAction FileClickAction
    {
        get => Enum.TryParse<FileClickAction>(repository.Get(FileClickActionKey), out var v) ? v : FileClickAction.OpenAfterDownload;
        set => Save(FileClickActionKey, value.ToString());
    }

    private void Save(string key, string value)
    {
        if (repository.Get(key) == value)
            return;
        repository.Set(key, value);
        Changed?.Invoke(this, key);
    }
}
