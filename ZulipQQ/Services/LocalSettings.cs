using System;
using System.Collections.Generic;
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
/// 只保存在本机的设置（Zulip 没有的设置）：ZulipQQ 自身的行为是程序级（所有账号共用），读写 app_setting；
/// 与账号的数据有关的（@全体提醒、输入框模式、系统通知）是账号级，读写 account_setting。
/// 属性赋值即保存，值有变化时触发 <see cref="Changed"/>。
/// </summary>
public sealed class LocalSettings(SettingRepository repository, SettingRepository appRepository)
{
    public const string OpenChatInNewWindowKey = "open_chat_in_new_window";
    public const string ClassicModeKey = "classic_mode";
    public const string EdgeAutoHideKey = "edge_auto_hide";
    public const string ShowTrayIconKey = "show_tray_icon";
    public const string CloseActionKey = "close_action";
    public const string ShowUnavailableKey = "dev_show_unavailable";
    public const string LogRequestsKey = "dev_log_requests";
    public const string RequestLogLimitKey = "dev_request_log_limit";
    public const string ShowSettingSourceKey = "dev_show_setting_source";

    /// <summary>请求记录默认保存的条数。</summary>
    public const int DefaultRequestLogLimit = 1000;

    /// <summary>请求记录保存的条数为此值时不限制条数。</summary>
    public const int UnlimitedRequestLogLimit = 0;
    private const string BoundsNormalKey = "main_window_bounds_normal";
    private const string BoundsClassicKey = "main_window_bounds_classic";

    private const string FontSizeKey = "font_size";
    private const string NotificationStyleKey = "notification_style";
    private const string DownloadRootKey = "download_root";
    private const string FileClickActionKey = "file_click_action";
    public const string TopicTitleFormatKey = "topic_title_format";
    public const string CollapseResolvedTopicsKey = "collapse_resolved_topics";
    public const string WildcardMentionsNotifyKey = "wildcard_mentions_notify";
    public const string SystemNoticesClearedBeforeKey = "system_notices_cleared_before";
    public const string ComposerSourceModeKey = "composer_source_mode";

    private readonly Dictionary<(SettingRepository, string), string?> _cache = [];

    private static LocalSettings? _app;

    // 程序级设置所有账号共用：缓存和变化通知是静态的，一个账号修改后其他账号的 LocalSettings 也触发 Changed
    private static readonly Dictionary<string, string?> AppCache = [];
    private static event EventHandler<string>? AppChanged;
    private EventHandler<string>? _changed;
    private bool _detached;

    /// <summary>某项设置变化，参数为设置键（如 <c>font_size</c>）。</summary>
    public event EventHandler<string>? Changed
    {
        add
        {
            if (_changed is null && !_detached)
                AppChanged += OnAppChanged;
            _changed += value;
        }
        remove
        {
            _changed -= value;
            if (_changed is null)
                AppChanged -= OnAppChanged;
        }
    }

    /// <summary>
    /// 不属于任何账号的实例：只用于读写程序级设置（主窗口的位置和布局、托盘、设置窗口的程序级页面），没有账号在线时也可用。
    /// 账号级的设置写入程序级的表，不要通过它读写。
    /// </summary>
    public static LocalSettings App
    {
        get
        {
            if (_app is null)
            {
                var app = new SettingRepository(LocalDb.Instance!, null);
                _app = new LocalSettings(app, app);
            }
            return _app;
        }
    }

    /// <summary>会话结束：不再接收程序级设置的变化。</summary>
    public void Detach()
    {
        _detached = true;
        AppChanged -= OnAppChanged;
    }

    private void OnAppChanged(object? sender, string key) => _changed?.Invoke(this, key);

    /// <summary>在独立窗口中打开聊天（程序级；经典模式下总是在独立窗口中打开）。</summary>
    public bool OpenChatInNewWindow
    {
        get => GetBool(appRepository, OpenChatInNewWindowKey, false);
        set => Save(appRepository, OpenChatInNewWindowKey, value);
    }

    /// <summary>经典模式（程序级）：主窗口只有导航栏和列表，聊天都在独立窗口中打开。</summary>
    public bool ClassicMode
    {
        get => GetBool(appRepository, ClassicModeKey, false);
        set => Save(appRepository, ClassicModeKey, value);
    }

    /// <summary>主窗口贴住屏幕边缘时自动隐藏（程序级）。</summary>
    public bool EdgeAutoHide
    {
        get => GetBool(appRepository, EdgeAutoHideKey, true);
        set => Save(appRepository, EdgeAutoHideKey, value);
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
        get => Enum.TryParse<CloseAction>(Read(appRepository, CloseActionKey), out var v) ? v : CloseAction.MinimizeToTray;
        set => Save(appRepository, CloseActionKey, value.ToString());
    }

    /// <summary>
    /// 开发者模式（程序级）：没有权限或服务器不支持的设置和操作也显示，操作时只模拟，不发送到服务器。
    /// 见 <see cref="ZulipSession.Shows"/>、<see cref="ZulipSession.Mocks"/>。
    /// </summary>
    public bool ShowUnavailable
    {
        get => GetBool(appRepository, ShowUnavailableKey, false);
        set => Save(appRepository, ShowUnavailableKey, value);
    }

    /// <summary>开发者模式（程序级）：设置项下方显示它对应的 Zulip 配置名，或 ZulipQQ 本机设置的范围（全局 / 账号）。</summary>
    public bool ShowSettingSource
    {
        get => GetBool(appRepository, ShowSettingSourceKey, false);
        set => Save(appRepository, ShowSettingSourceKey, value);
    }

    /// <summary>开发者模式（程序级）：记录与 Zulip 服务器之间的请求和响应，在设置的“开发者模式”页中查看。</summary>
    public bool LogRequests
    {
        get => GetBool(appRepository, LogRequestsKey, false);
        set => Save(appRepository, LogRequestsKey, value);
    }

    /// <summary>
    /// 请求记录保存的条数（程序级），超出时删除最早的记录；<see cref="UnlimitedRequestLogLimit"/> 为不限制。
    /// </summary>
    public int RequestLogLimit
    {
        get => int.TryParse(Read(appRepository, RequestLogLimitKey), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
            ? Math.Max(v, UnlimitedRequestLogLimit)
            : DefaultRequestLogLimit;
        set => Save(appRepository, RequestLogLimitKey, Math.Max(value, UnlimitedRequestLogLimit).ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>实际的关闭行为：没有托盘图标时无法从托盘恢复，只能退出。</summary>
    public CloseAction EffectiveCloseAction => ShowTrayIcon ? CloseAction : CloseAction.Exit;

    /// <summary>默认模式 / 经典模式下主窗口的位置和大小（程序级）；没有保存过时为 null。</summary>
    public WindowBounds? GetMainWindowBounds(bool classic) =>
        WindowBounds.Parse(Read(appRepository, classic ? BoundsClassicKey : BoundsNormalKey));

    public void SetMainWindowBounds(bool classic, WindowBounds bounds) =>
        Write(appRepository, classic ? BoundsClassicKey : BoundsNormalKey, bounds.ToString());

    /// <summary>正文字号（程序级），12～18。</summary>
    public int FontSize
    {
        get => int.TryParse(Read(appRepository, FontSizeKey), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
            ? Math.Clamp(v, ThemeService.MinFontSize, ThemeService.MaxFontSize)
            : ThemeService.DefaultFontSize;
        set => Save(appRepository, FontSizeKey,
            Math.Clamp(value, ThemeService.MinFontSize, ThemeService.MaxFontSize).ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>新消息提醒的方式（程序级）。</summary>
    public NotificationStyle NotificationStyle
    {
        get => Enum.TryParse<NotificationStyle>(Read(appRepository, NotificationStyleKey), out var v) ? v : NotificationStyle.Banner;
        set => Save(appRepository, NotificationStyleKey, value.ToString());
    }

    /// <summary>聊天文件的下载根目录（程序级）。</summary>
    public string DownloadRoot
    {
        get => Read(appRepository, DownloadRootKey) is { Length: > 0 } v ? v : AppPaths.DefaultDownloadRoot;
        set => Save(appRepository, DownloadRootKey, value);
    }

    /// <summary>点击聊天中未下载的文件时的行为（程序级）。</summary>
    public FileClickAction FileClickAction
    {
        get => Enum.TryParse<FileClickAction>(Read(appRepository, FileClickActionKey), out var v) ? v : FileClickAction.OpenAfterDownload;
        set => Save(appRepository, FileClickActionKey, value.ToString());
    }

    /// <summary>话题会话的标题模板（程序级；会话列表、聊天标题、独立窗口标题），见 <see cref="TopicTitleFormat"/>。</summary>
    public string TopicTitleFormat
    {
        get => Read(appRepository, TopicTitleFormatKey) ?? Core.TopicTitleFormat.Default;
        set => Save(appRepository, TopicTitleFormatKey, value);
    }

    /// <summary>收起已解决话题（程序级）：已解决的话题收进“已解决话题”，不出现在消息列表和群助手中，也不提醒。</summary>
    public bool CollapseResolvedTopics
    {
        get => GetBool(appRepository, CollapseResolvedTopicsKey, false);
        set => Save(appRepository, CollapseResolvedTopicsKey, value);
    }

    /// <summary>@全体成员时提醒（只影响 ZulipQQ 的桌面提醒，手机推送和邮件见 Zulip 的同名设置）。</summary>
    public bool WildcardMentionsNotify
    {
        get => GetBool(repository, WildcardMentionsNotifyKey, true);
        set => Save(repository, WildcardMentionsNotifyKey, value);
    }

    /// <summary>输入框显示 Markdown 源码（否则显示渲染效果）。</summary>
    public bool ComposerSourceMode
    {
        get => GetBool(repository, ComposerSourceModeKey, false);
        set => Save(repository, ComposerSourceModeKey, value);
    }

    /// <summary>清空系统通知时最新一条通知的 id；不大于它的通知不再显示。</summary>
    public long SystemNoticesClearedBefore
    {
        get => long.TryParse(Read(repository, SystemNoticesClearedBeforeKey), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;
        set => Save(SystemNoticesClearedBeforeKey, value.ToString(CultureInfo.InvariantCulture));
    }

    private void Save(string key, string value) => Save(repository, key, value);

    private void Save(SettingRepository target, string key, bool value) => Save(target, key, value ? "1" : "0");

    private void Save(SettingRepository target, string key, string value)
    {
        if (Read(target, key) == value)
            return;
        Write(target, key, value);
        if (target == appRepository)
            AppChanged?.Invoke(this, key);
        else
            _changed?.Invoke(this, key);
    }

    // 会话列表刷新时每个会话都会读取设置（标题模板、收起已解决话题），读过的值缓存在内存中；
    // 所有写入都经过 Write，缓存不会过期
    private string? Read(SettingRepository source, string key)
    {
        if (source == appRepository)
        {
            if (!AppCache.TryGetValue(key, out var app))
                AppCache[key] = app = source.Get(key);
            return app;
        }
        if (!_cache.TryGetValue((source, key), out var value))
            _cache[(source, key)] = value = source.Get(key);
        return value;
    }

    private void Write(SettingRepository target, string key, string value)
    {
        target.Set(key, value);
        if (target == appRepository)
            AppCache[key] = value;
        else
            _cache[(target, key)] = value;
    }

    private bool GetBool(SettingRepository source, string key, bool defaultValue) => ParseBool(Read(source, key), defaultValue);

    private static bool ParseBool(string? value, bool defaultValue) => value switch
    {
        "1" => true,
        "0" => false,
        _ => defaultValue,
    };
}
