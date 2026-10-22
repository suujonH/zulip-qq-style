using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ZulipQQ.ViewModels;

/// <summary>
/// 选择日期和时间（定时发送、自定义提醒时间）。只能选择将来的时间。
/// 关闭时调用 onClosed：确定传入本地时间，取消传入 null。
/// </summary>
public partial class DateTimeDialogViewModel : ViewModelBase
{
    private readonly Action<DateTime?> _onClosed;

    public DateTimeDialogViewModel(string title, string confirmText, DateTime initial, Action<DateTime?> onClosed)
    {
        Title = title;
        ConfirmText = confirmText;
        _onClosed = onClosed;
        Date = initial.Date;
        Time = new TimeSpan(initial.Hour, initial.Minute, 0);
    }

    /// <summary>在对话框层中打开，返回选择的本地时间；取消时为 null。默认值为下一个整点。</summary>
    public static Task<DateTime?> ShowAsync(IDialogHost host, string title, string confirmText)
    {
        var tcs = new TaskCompletionSource<DateTime?>();
        var now = DateTime.Now;
        var initial = now.Date.AddHours(now.Hour + 1);
        DateTimeDialogViewModel dialog = null!;
        dialog = new DateTimeDialogViewModel(title, confirmText, initial, result =>
        {
            host.Dialogs.Remove(dialog);
            tcs.TrySetResult(result);
        });
        host.Dialogs.Add(dialog);
        return tcs.Task;
    }

    /// <summary>日历中可选的最早日期。</summary>
    public static DateTime Today => DateTime.Today;

    public string Title { get; }

    public string ConfirmText { get; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    public partial DateTime? Date { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    public partial TimeSpan? Time { get; set; }

    [ObservableProperty]
    public partial string? ErrorText { get; private set; }

    partial void OnDateChanged(DateTime? value) => ErrorText = null;

    partial void OnTimeChanged(TimeSpan? value) => ErrorText = null;

    private bool CanConfirm => Date is not null && Time is not null;

    [RelayCommand(CanExecute = nameof(CanConfirm))]
    private void Confirm()
    {
        var at = Date!.Value.Date + Time!.Value;
        if (at <= DateTime.Now)
        {
            ErrorText = "请选择将来的时间";
            return;
        }
        _onClosed(at);
    }

    [RelayCommand]
    private void Cancel() => _onClosed(null);
}
