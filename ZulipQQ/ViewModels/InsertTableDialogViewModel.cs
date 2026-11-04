using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZulipQQ.Core;

namespace ZulipQQ.ViewModels;

/// <summary>“插入表格”对话框：填写列数和行数（含标题行），确定时交给 <c>insert</c>；之后（确定或取消）调用 <c>onClosed</c>。</summary>
public partial class InsertTableDialogViewModel(Action<int, int> insert, Action onClosed) : ViewModelBase
{
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    public partial string Columns { get; set; } = "3";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    public partial string Rows { get; set; } = "4";

    private bool CanConfirm =>
        int.TryParse(Columns, out var c) && c is >= MarkdownTableEdit.MinColumns and <= MarkdownTableEdit.MaxColumns
        && int.TryParse(Rows, out var r) && r is >= MarkdownTableEdit.MinRows and <= MarkdownTableEdit.MaxRows;

    [RelayCommand(CanExecute = nameof(CanConfirm))]
    private void Confirm()
    {
        onClosed();
        insert(int.Parse(Columns), int.Parse(Rows));
    }

    [RelayCommand]
    private void Cancel() => onClosed();
}
