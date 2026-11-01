using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ZulipQQ.ViewModels;

/// <summary>确认对话框。关闭时调用 onClosed：确定传入 true，取消传入 false（所在窗口关闭时视为取消）。</summary>
public partial class ConfirmDialogViewModel(string message, string confirmText, bool isDanger, Action<bool> onClosed,
    string cancelText = "取消")
    : ViewModelBase, IDisposable
{
    public string Message { get; } = message;

    public string ConfirmText { get; } = confirmText;

    public string CancelText { get; } = cancelText;

    /// <summary>确定按钮显示为红色（删除等不可恢复的操作）。</summary>
    public bool IsDanger { get; } = isDanger;

    /// <summary>消息下方的复选框文字（例如“同时删除所有数据”）；为 null 时没有复选框。</summary>
    public string? CheckText { get; init; }

    public bool HasCheck => CheckText is not null;

    [ObservableProperty]
    public partial bool IsChecked { get; set; }

    [RelayCommand]
    private void Confirm() => onClosed(true);

    [RelayCommand]
    private void Cancel() => onClosed(false);

    public void Dispose() => onClosed(false);
}
