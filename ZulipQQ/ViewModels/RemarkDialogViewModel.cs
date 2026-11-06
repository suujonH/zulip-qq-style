using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ZulipQQ.ViewModels;

/// <summary>
/// “设置备注”对话框：用户、频道、话题共用。备注只保存在本地；确定时备注为空或只有空白表示清除备注。
/// 确定时调用 <paramref name="save"/>，之后（确定或取消）调用 <paramref name="onClosed"/>。
/// </summary>
public partial class RemarkDialogViewModel(string title, string originalName, string? remark,
    Action<string?> save, Action onClosed) : ViewModelBase
{
    public string Title { get; } = title;

    /// <summary>输入框下方的提示，显示原名称，便于对照。</summary>
    public string OriginalText { get; } = $"原名称：{originalName}";

    [ObservableProperty]
    public partial string Remark { get; set; } = remark ?? "";

    [RelayCommand]
    private void Confirm()
    {
        save(Remark);
        onClosed();
    }

    [RelayCommand]
    private void Cancel() => onClosed();

    [RelayCommand]
    private void Clear() => Remark = "";
}
