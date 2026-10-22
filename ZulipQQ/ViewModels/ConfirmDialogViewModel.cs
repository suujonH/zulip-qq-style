using System;
using CommunityToolkit.Mvvm.Input;

namespace ZulipQQ.ViewModels;

/// <summary>确认对话框。关闭时调用 onClosed：确定传入 true，取消传入 false。</summary>
public partial class ConfirmDialogViewModel(string message, string confirmText, bool isDanger, Action<bool> onClosed)
    : ViewModelBase
{
    public string Message { get; } = message;

    public string ConfirmText { get; } = confirmText;

    /// <summary>确定按钮显示为红色（删除等不可恢复的操作）。</summary>
    public bool IsDanger { get; } = isDanger;

    [RelayCommand]
    private void Confirm() => onClosed(true);

    [RelayCommand]
    private void Cancel() => onClosed(false);
}
