using System;
using System.Globalization;
using ZulipQQ.Core;
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

/// <summary>关闭主面板时的行为。</summary>
public enum CloseAction
{
    MinimizeToTray,
    Exit,
}

/// <summary>点击聊天中未下载的文件时的行为。</summary>
public enum FileClickAction
{
    OpenAfterDownload,
    DownloadOnly,
}

/// <summary>
/// 只保存在本机的设置（Zulip 没有的设置）：账号级读写 account_setting，托盘与关闭行为是程序级，读写 app_setting。
/// 属性赋值即保存，值有变化时触发 <see cref="Changed"/>。
/// </summary>
public sealed class LocalSettings(SettingRepository repository, SettingRepository appRepository)
{
    public const string OpenChatInNewWindowKey = "open_chat_in_new_window";
    public const string ClassicModeKey = "classic_mode";
    public const string EdgeAutoHideKey = "edge_auto_hide";
    public const string ShowTrayIconKey = "show_tray_icon";
    public const string CloseActionKey = "close_action";
    private const string BoundsNormalKey = "main_window_bounds_normal";
    private const string BoundsClassicKey = "main_window_bounds_classic";

    private const string FontSizeKey = "font_size";
    private const string NotificationStyleKey = "notification_style";
    private const string DownloadRootKey = "download_root";
    private const string FileClickActionKey = "file_click_action";
    public const string TopicTitleFormatKey = "topic_title_format";
    public const string CollapseResolvedTopicsKey = "collapse_resolved_topics";

    /// <summary>某项设置变化，参数为设置键（如 <c>font_size</c>）。</summary>
    public event EventHandler<string>? Changed;

    /// <summary>在独立窗口中打开聊天（经典模式下总是在独立窗口中打开）。</summary>
    public bool OpenChatInNewWindow
    {
        get => GetBool(repository, OpenChatInNewWindowKey, false);
        set => Save(repository, OpenChatInNewWindowKey, value);
    }

    /// <summary>经典模式：主窗口只有导航栏和列表，聊天都在独立窗口中打开。</summary>
    public bool ClassicMode
    {
        get => GetBool(repository, ClassicModeKey, false);
        set => Save(repository, ClassicModeKey, value);
    }

    /// <summary>主窗口贴住屏幕边缘时自动隐藏。</summary>
    public bool EdgeAutoHide
    {
        get => GetBool(repository, EdgeAutoHideKey, true);
        set => Save(repository, EdgeAutoHideKey, value);
    }

    /// <summary>显示托盘图标（程序级）。</summary>
    public bool ShowTrayIcon
    {
        get => GetBool(appRepository, ShowTrayIconKey, true);
        set => Save(appRepository, ShowTrayIconKey, value);
    }

    /// <summary>关闭主面板时的行为（程序级）。托盘图标隐藏时总是退出，见 <see cref="EffectiveCloseAction"/>。</summary>
    public CloseAction CloseAction
    {
        get => Enum.TryParse<CloseAction>(appRepository.Get(CloseActionKey), out var v) ? v : CloseAction.MinimizeToTray;
        set => Save(appRepository, CloseActionKey, value.ToString());
    }

    /// <summary>实际的关闭行为：没有托盘图标时无法从托盘恢复，只能退出。</summary>
    public CloseAction EffectiveCloseAction => ShowTrayIcon ? CloseAction : CloseAction.Exit;

    /// <summary>默认模式 / 经典模式下主窗口的位置和大小；没有保存过时为 null。</summary>
    public WindowBounds? GetMainWindowBounds(bool classic) =>
        WindowBounds.Parse(repository.Get(classic ? BoundsClassicKey : BoundsNormalKey));

    public void SetMainWindowBounds(bool classic, WindowBounds bounds) =>
        repository.Set(classic ? BoundsClassicKey : BoundsNormalKey, bounds.ToString());

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

    /// <summary>话题会话的标题模板（会话列表、聊天标题、独立窗口标题），见 <see cref="TopicTitleFormat"/>。</summary>
    public string TopicTitleFormat
    {
        get => repository.Get(TopicTitleFormatKey) ?? Core.TopicTitleFormat.Default;
        set => Save(TopicTitleFormatKey, value);
    }

    /// <summary>收起已解决话题：已解决的话题收进“已解决话题”，不出现在消息列表和群助手中，也不提醒。</summary>
    public bool CollapseResolvedTopics
    {
        get => GetBool(repository, CollapseResolvedTopicsKey, false);
        set => Save(repository, CollapseResolvedTopicsKey, value);
    }

    private void Save(string key, string value) => Save(repository, key, value);

    private void Save(SettingRepository target, string key, bool value) => Save(target, key, value ? "1" : "0");

    private void Save(SettingRepository target, string key, string value)
    {
        if (target.Get(key) == value)
            return;
        target.Set(key, value);
        Changed?.Invoke(this, key);
    }

    private static bool GetBool(SettingRepository source, string key, bool defaultValue) => source.Get(key) switch
    {
        "1" => true,
        "0" => false,
        _ => defaultValue,
    };
}
