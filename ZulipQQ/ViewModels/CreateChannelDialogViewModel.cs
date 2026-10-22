using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZulipQQ.Services;
using ZulipQQ.Zulip;

namespace ZulipQQ.ViewModels;

/// <summary>
/// “创建频道”对话框：名称、描述、是否私有（只能选有权限的类型）、初始成员。
/// 创建者自己总是成员。成功后关闭并提示；失败时保留对话框并提示服务器的错误。
/// </summary>
public partial class CreateChannelDialogViewModel : ViewModelBase
{
    /// <summary>Zulip 的频道名称长度上限。</summary>
    public const int MaxNameLength = 60;

    public const int MaxDescriptionLength = 1024;

    private readonly ZulipSession _session;
    private readonly MainViewModel _main;
    private readonly Action _onClosed;
    private IReadOnlyList<long> _members = [];

    public CreateChannelDialogViewModel(ZulipSession session, MainViewModel main, Action onClosed)
    {
        _session = session;
        _main = main;
        _onClosed = onClosed;
        var canPublic = session.Permissions.CanCreatePublicChannel;
        CanChoosePrivacy = canPublic && session.Permissions.CanCreatePrivateChannel;
        IsPrivate = !canPublic;
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    public partial string Name { get; set; } = "";

    [ObservableProperty]
    public partial string Description { get; set; } = "";

    /// <summary>两种频道都有权限创建时才能切换；否则固定为有权限的那一种。</summary>
    public bool CanChoosePrivacy { get; }

    [ObservableProperty]
    public partial bool IsPrivate { get; set; }

    public string MembersText => $"已选择 {_members.Count.ToString(CultureInfo.InvariantCulture)} 人";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    public partial bool IsBusy { get; private set; }

    private bool CanConfirm => !IsBusy && Name.Trim().Length > 0;

    [RelayCommand]
    private void ChooseMembers()
    {
        UserPickerDialogViewModel picker = null!;
        picker = new UserPickerDialogViewModel(_session, "选择成员", 0, _members, ids =>
        {
            _main.Dialogs.Remove(picker);
            if (ids is null)
                return;
            _members = ids;
            OnPropertyChanged(nameof(MembersText));
        });
        _main.Dialogs.Add(picker);
    }

    [RelayCommand(CanExecute = nameof(CanConfirm))]
    private async Task ConfirmAsync()
    {
        IsBusy = true;
        try
        {
            var subscribers = _members.Prepend(_session.SelfId).Distinct().ToList();
            await _session.Client.CreateChannelAsync(Name.Trim(), Description.Trim(), IsPrivate, subscribers, _session.Token);
            _onClosed();
            _main.ShowToast("频道已创建");
        }
        catch (Exception ex) when (!_session.IsDisposed)
        {
            Log.Warn("创建频道失败", ex);
            _main.ShowToast(ex is ZulipApiException api ? $"创建失败：{api.Message}" : "创建失败");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Cancel() => _onClosed();
}
