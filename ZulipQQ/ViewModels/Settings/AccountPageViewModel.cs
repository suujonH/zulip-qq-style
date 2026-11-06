using System;
using System.IO;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZulipQQ.Core;
using ZulipQQ.Services;
using ZulipQQ.Zulip;

namespace ZulipQQ.ViewModels.Settings;

/// <summary>我：头像、姓名、个性签名、登录信息（邮箱、用户 ID、角色）、隐身、草稿同步、账号邮件（退出账号在“Zulip 账号”页的账号列表中右键）。</summary>
public sealed partial class AccountPageViewModel : SettingsPageViewModel
{
    public AccountPageViewModel(SettingsViewModel owner) : base(owner)
    {
        Session.Users.UserChanged += OnUserChanged;
        Session.Users.Reset += OnReset;
        Session.Settings.Changed += OnSettingChanged;
    }

    public string? AvatarUrl => Session.Users.Get(Session.SelfId)?.AvatarUrl ?? Session.Account.AvatarUrl;

    public string FullName => Session.Users.Get(Session.SelfId)?.FullName ?? Session.Account.FullName;

    public string Email => Session.Account.Email;

    public long UserId => Session.SelfId;

    public string RoleText => Session.Users.Get(Session.SelfId)?.Role switch
    {
        PermissionEvaluator.RoleOwner => "所有者",
        PermissionEvaluator.RoleAdministrator => "管理员",
        PermissionEvaluator.RoleModerator => "版主",
        PermissionEvaluator.RoleMember => "成员",
        >= PermissionEvaluator.RoleGuest => "访客",
        _ => "未知",
    };

    public string Signature => Session.Users.GetStatusText(Session.SelfId) ?? "";

    public int SignatureMaxLength => MainViewModel.SignatureMaxLength;

    [ObservableProperty]
    public partial bool IsEditingName { get; private set; }

    [ObservableProperty]
    public partial string NameDraft { get; set; } = "";

    [ObservableProperty]
    public partial bool IsEditingSignature { get; private set; }

    [ObservableProperty]
    public partial string SignatureDraft { get; set; } = "";

    [ObservableProperty]
    public partial bool IsChangingAvatar { get; private set; }

    /// <summary>隐身 = 不向他人显示在线状态（Zulip 的 presence_enabled 取反）。</summary>
    public bool IsInvisible
    {
        get => !Session.Settings.Zulip.PresenceEnabled;
        set
        {
            if (value == Session.Settings.Zulip.PresenceEnabled)
                SetZulip("presence_enabled", !value);
        }
    }

    public bool EnableLoginEmails
    {
        get => Session.Settings.Zulip.EnableLoginEmails;
        set
        {
            if (value != Session.Settings.Zulip.EnableLoginEmails)
                SetZulip("enable_login_emails", value);
        }
    }

    public bool EnableMarketingEmails
    {
        get => Session.Settings.Zulip.EnableMarketingEmails;
        set
        {
            if (value != Session.Settings.Zulip.EnableMarketingEmails)
                SetZulip("enable_marketing_emails", value);
        }
    }

    public bool DraftsSync
    {
        get => Session.Settings.Zulip.EnableDraftsSynchronization;
        set
        {
            if (value != Session.Settings.Zulip.EnableDraftsSynchronization)
                _ = SetDraftsSyncAsync(value);
        }
    }

    [RelayCommand]
    private void BeginEditName()
    {
        NameDraft = FullName;
        IsEditingName = true;
    }

    [RelayCommand]
    private void CancelEditName() => IsEditingName = false;

    /// <summary>提交姓名；结果由 realm_user 事件更新。</summary>
    [RelayCommand]
    private async Task CommitNameAsync()
    {
        if (!IsEditingName)
            return;
        IsEditingName = false;
        var name = NameDraft.Trim();
        if (name.Length == 0 || name == FullName)
            return;
        try
        {
            await Session.Client.UpdateSettingsAsync(new FormBuilder().Add("full_name", name), Session.Token);
        }
        catch (Exception ex) when (!Session.IsDisposed)
        {
            Log.Warn("修改姓名失败", ex);
            Owner.ShowToast(ex is ZulipApiException { Message.Length: > 0 } api ? api.Message : "修改失败");
        }
    }

    [RelayCommand]
    private void BeginEditSignature()
    {
        SignatureDraft = Signature;
        IsEditingSignature = true;
    }

    [RelayCommand]
    private void CancelEditSignature() => IsEditingSignature = false;

    /// <summary>提交个性签名：空文字表示清除。结果由 user_status 事件更新。</summary>
    [RelayCommand]
    private async Task CommitSignatureAsync()
    {
        if (!IsEditingSignature)
            return;
        IsEditingSignature = false;
        var text = SignatureDraft.Trim();
        if (text == Signature)
            return;
        try
        {
            await Session.Client.SetStatusAsync(text, Session.Token);
        }
        catch (Exception ex) when (!Session.IsDisposed)
        {
            Log.Warn("修改个性签名失败", ex);
            Owner.ShowToast("修改失败");
        }
    }

    /// <summary>
    /// 更换头像（由窗口弹出文件选择器）：读取图片、打开裁剪对话框，保存后上传裁剪结果；取消时不上传。
    /// 组织禁止更换时显示服务器的错误。
    /// </summary>
    public async Task ChangeAvatarAsync(string path)
    {
        if (IsChangingAvatar)
            return;
        IsChangingAvatar = true;
        try
        {
            var image = await Task.Run(() => AvatarImage.Load(path));
            if (Session.IsDisposed)
            {
                image.Dispose();
                return;
            }
            var png = await Owner.CropAvatarAsync(image);
            if (png is null || Session.IsDisposed)
                return;
            using var stream = new MemoryStream(png, writable: false);
            await Session.Client.UploadAvatarAsync(stream, "avatar.png", "image/png", Session.Token);
            if (!Session.IsDisposed)
                Owner.ShowToast("头像已更换");
        }
        catch (AvatarImageException ex) when (!Session.IsDisposed)
        {
            Log.Warn($"读取头像图片失败：{path}", ex);
            Owner.ShowToast(ex.Message);
        }
        catch (Exception ex) when (!Session.IsDisposed)
        {
            Log.Warn("更换头像失败", ex);
            Owner.ShowToast(ex is ZulipApiException { Message.Length: > 0 } api ? $"更换失败：{api.Message}" : "更换失败");
        }
        finally
        {
            IsChangingAvatar = false;
        }
    }

    public override void Close()
    {
        Session.Users.UserChanged -= OnUserChanged;
        Session.Users.Reset -= OnReset;
        Session.Settings.Changed -= OnSettingChanged;
    }

    /// <summary>
    /// 关闭同步时 Zulip 会删除服务器上的全部草稿，所以先把草稿转存到本地再提交设置；
    /// 开启时先提交设置，再上传本地草稿（与收到 user_settings 事件时的处理相同，重复调用无副作用）。
    /// </summary>
    private async Task SetDraftsSyncAsync(bool enabled)
    {
        try
        {
            if (!enabled)
                await Session.Drafts.OnSyncSettingChangedAsync(false);
            try
            {
                await Session.Settings.SetZulipAsync("enable_drafts_synchronization", enabled);
            }
            catch when (!enabled)
            {
                // 设置没有改成，服务器上的草稿还在：草稿恢复为服务器模式
                await Session.Drafts.RevertDisableAsync();
                throw;
            }
            if (enabled)
                await Session.Drafts.OnSyncSettingChangedAsync(true);
        }
        catch (Exception ex) when (!Session.IsDisposed)
        {
            Log.Warn("切换草稿同步失败", ex);
            Owner.ShowToast("设置失败");
        }
    }

    private void OnUserChanged(object? sender, long id)
    {
        if (id == Session.SelfId)
            OnPropertyChanged(string.Empty);
    }

    private void OnReset(object? sender, EventArgs e) => OnPropertyChanged(string.Empty);

    private void OnSettingChanged(object? sender, string name) => OnPropertyChanged(string.Empty);
}
