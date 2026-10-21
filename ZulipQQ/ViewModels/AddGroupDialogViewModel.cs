using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZulipQQ.Models;
using ZulipQQ.Services;

namespace ZulipQQ.ViewModels;

/// <summary>
/// “添加分组”对话框。名称为空或只有空白时不能确定；名称允许与已有分组重复。
/// 关闭时调用 onClosed：确定传入新建的分组，取消传入 null。
/// </summary>
public partial class AddGroupDialogViewModel(FriendStore store, Action<FriendGroup?> onClosed) : ViewModelBase
{
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    public partial string Name { get; set; } = "";

    private bool CanConfirm => !string.IsNullOrWhiteSpace(Name);

    [RelayCommand(CanExecute = nameof(CanConfirm))]
    private void Confirm() => onClosed(store.AddGroup(Name.Trim()));

    [RelayCommand]
    private void Cancel() => onClosed(null);

    [RelayCommand]
    private void Clear() => Name = "";
}
