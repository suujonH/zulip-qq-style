namespace ZulipQQ.ViewModels.Settings;

/// <summary>偏好设置：保存在 Zulip 上的个人设置，外观（color_scheme）、发送消息的按键、24 小时制。</summary>
public sealed class PreferencesPageViewModel : SettingsPageViewModel
{
    public PreferencesPageViewModel(SettingsViewModel owner) : base(owner)
    {
        Session.Settings.Changed += OnChanged;
    }

    public bool IsSchemeAuto
    {
        get => Session.Settings.Zulip.ColorScheme is not (2 or 3);
        set => SetScheme(value, 1);
    }

    public bool IsSchemeLight
    {
        get => Session.Settings.Zulip.ColorScheme == 3;
        set => SetScheme(value, 3);
    }

    public bool IsSchemeDark
    {
        get => Session.Settings.Zulip.ColorScheme == 2;
        set => SetScheme(value, 2);
    }

    public bool IsEnterSends
    {
        get => Session.Settings.Zulip.EnterSends;
        set => SetEnterSends(value, true);
    }

    public bool IsCtrlEnterSends
    {
        get => !Session.Settings.Zulip.EnterSends;
        set => SetEnterSends(value, false);
    }

    public bool TwentyFourHourTime
    {
        get => Session.Settings.Zulip.TwentyFourHourTime;
        set
        {
            if (value != Session.Settings.Zulip.TwentyFourHourTime)
                SetZulip("twenty_four_hour_time", value);
        }
    }

    public override void Close()
    {
        Session.Settings.Changed -= OnChanged;
    }

    // 单选框取消选中时也会写入 false，只处理选中
    private void SetScheme(bool isChecked, int scheme)
    {
        if (isChecked && Session.Settings.Zulip.ColorScheme != scheme)
            SetZulip("color_scheme", scheme);
    }

    private void SetEnterSends(bool isChecked, bool enterSends)
    {
        if (isChecked && Session.Settings.Zulip.EnterSends != enterSends)
            SetZulip("enter_sends", enterSends);
    }

    private void OnChanged(object? sender, string name) => OnPropertyChanged(string.Empty);
}
