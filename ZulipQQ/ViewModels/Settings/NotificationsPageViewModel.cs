using ZulipQQ.Zulip.Dtos;

namespace ZulipQQ.ViewModels.Settings;

/// <summary>消息通知：Zulip 的桌面通知与提示音设置，以及本机的 @全体提醒（账号级）。通知类型是程序级，在“通用”中。</summary>
public sealed class NotificationsPageViewModel : SettingsPageViewModel
{
    public NotificationsPageViewModel(SettingsViewModel owner) : base(owner)
    {
        Session.Settings.Changed += OnChanged;
        Session.Settings.Local.Changed += OnChanged;
    }

    private UserSettingsDto Z => Session.Settings.Zulip;

    public bool EnableDesktopNotifications
    {
        get => Z.EnableDesktopNotifications;
        set => Set("enable_desktop_notifications", Z.EnableDesktopNotifications, value);
    }

    public bool EnableStreamDesktopNotifications
    {
        get => Z.EnableStreamDesktopNotifications;
        set => Set("enable_stream_desktop_notifications", Z.EnableStreamDesktopNotifications, value);
    }

    public bool WildcardMentionsNotify
    {
        get => Session.Settings.Local.WildcardMentionsNotify;
        set => Session.Settings.Local.WildcardMentionsNotify = value;
    }

    public bool PmContentInDesktopNotifications
    {
        get => Z.PmContentInDesktopNotifications;
        set => Set("pm_content_in_desktop_notifications", Z.PmContentInDesktopNotifications, value);
    }

    public bool EnableSounds
    {
        get => Z.EnableSounds;
        set => Set("enable_sounds", Z.EnableSounds, value);
    }

    public bool EnableStreamAudibleNotifications
    {
        get => Z.EnableStreamAudibleNotifications;
        set => Set("enable_stream_audible_notifications", Z.EnableStreamAudibleNotifications, value);
    }

    public override void Close()
    {
        Session.Settings.Changed -= OnChanged;
        Session.Settings.Local.Changed -= OnChanged;
    }

    private void Set(string name, bool current, bool value)
    {
        if (value != current)
            SetZulip(name, value);
    }

    private void OnChanged(object? sender, string name) => OnPropertyChanged(string.Empty);
}
