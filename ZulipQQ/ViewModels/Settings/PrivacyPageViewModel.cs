using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using ZulipQQ.Services;

namespace ZulipQQ.ViewModels.Settings;

/// <summary>邮箱可见范围的一个选项（Zulip 的 email_address_visibility）。</summary>
public sealed record EmailVisibilityOption(int Value, string Text)
{
    public static readonly IReadOnlyList<EmailVisibilityOption> All =
    [
        new(1, "所有人"),
        new(2, "成员"),
        new(5, "版主"),
        new(3, "管理员"),
        new(4, "无人"),
    ];

    public override string ToString() => Text;
}

/// <summary>已屏蔽（静音）的用户。</summary>
public sealed record MutedUserItem(long UserId, string Name, string? AvatarUrl);

/// <summary>隐私：已读回执、正在输入状态、邮箱可见范围、已屏蔽的用户。</summary>
public sealed partial class PrivacyPageViewModel : SettingsPageViewModel
{
    public PrivacyPageViewModel(SettingsViewModel owner) : base(owner)
    {
        Session.Settings.Changed += OnSettingChanged;
        Session.Users.MutedChanged += OnMutedChanged;
        Session.Users.Reset += OnMutedChanged;
        BuildMuted();
    }

    public bool SendReadReceipts
    {
        get => Session.Settings.Zulip.SendReadReceipts;
        set
        {
            if (value != Session.Settings.Zulip.SendReadReceipts)
                SetZulip("send_read_receipts", value);
        }
    }

    /// <summary>私聊与频道的“正在输入”一起开关；任一开启即显示为开启。</summary>
    public bool SendTyping
    {
        get => Session.Settings.Zulip.SendPrivateTypingNotifications || Session.Settings.Zulip.SendStreamTypingNotifications;
        set
        {
            if (value != Session.Settings.Zulip.SendPrivateTypingNotifications)
                SetZulip("send_private_typing_notifications", value);
            if (value != Session.Settings.Zulip.SendStreamTypingNotifications)
                SetZulip("send_stream_typing_notifications", value);
        }
    }

    public IReadOnlyList<EmailVisibilityOption> EmailVisibilityOptions => EmailVisibilityOption.All;

    public EmailVisibilityOption? EmailVisibility
    {
        get => EmailVisibilityOption.All.FirstOrDefault(o => o.Value == Session.Settings.Zulip.EmailAddressVisibility);
        set
        {
            if (value is not null && value.Value != Session.Settings.Zulip.EmailAddressVisibility)
                SetZulip("email_address_visibility", value.Value);
        }
    }

    public ObservableCollection<MutedUserItem> MutedUsers { get; } = [];

    public bool HasMutedUsers => MutedUsers.Count > 0;

    /// <summary>取消屏蔽；结果由 muted_users 事件更新。</summary>
    [RelayCommand]
    private async Task UnmuteAsync(MutedUserItem item)
    {
        try
        {
            await Session.Client.UnmuteUserAsync(item.UserId, Session.Token);
        }
        catch (Exception ex) when (!Session.IsDisposed)
        {
            Log.Warn($"取消屏蔽失败：{item.UserId}", ex);
            Owner.ShowToast("操作失败");
        }
    }

    public override void Close()
    {
        Session.Settings.Changed -= OnSettingChanged;
        Session.Users.MutedChanged -= OnMutedChanged;
        Session.Users.Reset -= OnMutedChanged;
    }

    private void BuildMuted()
    {
        MutedUsers.Clear();
        foreach (var id in Session.Users.MutedIds.OrderBy(id => Session.Contacts.DisplayName(id), StringComparer.Create(System.Globalization.CultureInfo.GetCultureInfo("zh-CN"), false)))
            MutedUsers.Add(new MutedUserItem(id, Session.Contacts.DisplayName(id), Session.Users.Get(id)?.AvatarUrl));
        OnPropertyChanged(nameof(HasMutedUsers));
    }

    private void OnSettingChanged(object? sender, string name)
    {
        OnPropertyChanged(nameof(SendReadReceipts));
        OnPropertyChanged(nameof(SendTyping));
        OnPropertyChanged(nameof(EmailVisibility));
    }

    private void OnMutedChanged(object? sender, EventArgs e) => BuildMuted();
}
