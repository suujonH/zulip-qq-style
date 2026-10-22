using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ZulipQQ.ViewModels;

/// <summary>
/// 单行文字输入对话框（重命名话题等）。去掉首尾空白后为空、超过长度上限或与原值相同时不能确定。
/// 关闭时调用 onClosed：确定传入去掉首尾空白的文字，取消传入 null。
/// </summary>
public partial class TextInputDialogViewModel : ViewModelBase
{
    private readonly string _initial;
    private readonly Action<string?> _onClosed;

    public TextInputDialogViewModel(string title, string placeholder, string initial, int maxLength, Action<string?> onClosed)
    {
        Title = title;
        Placeholder = placeholder;
        MaxLength = maxLength;
        _initial = initial;
        _onClosed = onClosed;
        Text = initial;
    }

    public string Title { get; }

    public string Placeholder { get; }

    public int MaxLength { get; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    public partial string Text { get; set; }

    private bool CanConfirm => Text.Trim() is { Length: > 0 } t && t.Length <= MaxLength && t != _initial;

    [RelayCommand(CanExecute = nameof(CanConfirm))]
    private void Confirm() => _onClosed(Text.Trim());

    [RelayCommand]
    private void Cancel() => _onClosed(null);

    [RelayCommand]
    private void Clear() => Text = "";
}
