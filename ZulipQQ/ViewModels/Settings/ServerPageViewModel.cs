using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using ZulipQQ.Data;
using ZulipQQ.Services;

namespace ZulipQQ.ViewModels.Settings;

/// <summary>
/// 一个账号的服务器：账号（启动、删除账号）、可修改的连接配置（允许不受信任的证书；启动时自动登录在“通用”中），
/// 以及只读的服务器信息（登录信息在“我”中）。
/// 只在设置的“Zulip 账号”页中显示。
/// </summary>
public sealed partial class ServerPageViewModel : SettingsPageViewModel
{
    private readonly AccountRepository _accounts = new(LocalDb.Instance!);

    public ServerPageViewModel(SettingsViewModel owner) : base(owner)
    {
        Session.ConnectionChanged += OnChanged;
        Session.Resynced += OnChanged;
    }

    public string RealmUrl => Session.Account.RealmUrl;

    public string OrganizationName => Session.Realm.Name;

    public string ServerVersion => Session.Realm.ZulipVersion;

    public int FeatureLevel => Session.Realm.FeatureLevel;

    public string ConnectionText => Session.Connection switch
    {
        ConnectionState.Connected => "已连接",
        ConnectionState.Reconnecting => "正在重新连接…",
        _ => "登录已失效",
    };

    /// <summary>允许不受信任的证书（测试服务器）；重新登录该账号后生效。</summary>
    public bool AllowInvalidCert
    {
        get => _accounts.Get(Session.Account.Id)?.AllowInvalidCert ?? false;
        set
        {
            if (value == AllowInvalidCert)
                return;
            _accounts.SetAllowInvalidCert(Session.Account.Id, value);
            OnPropertyChanged();
            Owner.ShowToast("重新登录该账号后生效");
        }
    }

    /// <summary>“启动”（启用）：关闭后立即退出该账号（留在账号列表中，显示“未启用”），启动程序时也不再登录。</summary>
    public bool LoginOnStartup
    {
        get => AccountHubViewModel.Instance.Find(Session)?.LoginOnStartup ?? true;
        set
        {
            if (AccountHubViewModel.Instance.Find(Session) is { } entry)
                entry.LoginOnStartup = value;
        }
    }

    [RelayCommand]
    private Task DeleteAccountAsync() => AccountHubViewModel.Instance.Find(Session) is { } entry
        ? AccountsPanelViewModel.DeleteAccountAsync(Owner, entry)
        : Task.CompletedTask;

    public override void Close()
    {
        Session.ConnectionChanged -= OnChanged;
        Session.Resynced -= OnChanged;
    }

    private void OnChanged(object? sender, EventArgs e) => OnPropertyChanged(string.Empty);
}
