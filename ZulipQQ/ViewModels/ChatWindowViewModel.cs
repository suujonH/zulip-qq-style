using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ZulipQQ.ViewModels;

/// <summary>独立聊天窗口：一个会话的聊天，以及窗口自己的对话框层和提示（聊天中的对话框显示在这里）。</summary>
public sealed partial class ChatWindowViewModel : ViewModelBase, IDialogHost
{
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(3) };

    public ChatWindowViewModel(ChatViewModel chat)
    {
        Chat = chat;
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            ToastText = null;
        };
    }

    public ChatViewModel Chat { get; }

    public ObservableCollection<ViewModelBase> Dialogs { get; } = [];

    [ObservableProperty]
    public partial string? ToastText { get; private set; }

    /// <summary>窗口关闭：关闭仍打开的对话框（退订事件、未回答的确认视为取消）。</summary>
    public void Close()
    {
        _toastTimer.Stop();
        foreach (var dialog in Dialogs.ToList())
            (dialog as IDisposable)?.Dispose();
        Dialogs.Clear();
    }

    public void ShowToast(string text)
    {
        ToastText = text;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    public Task<bool> ConfirmAsync(string message, string confirmText = "确定", bool isDanger = false)
    {
        var tcs = new TaskCompletionSource<bool>();
        ConfirmDialogViewModel dialog = null!;
        dialog = new ConfirmDialogViewModel(message, confirmText, isDanger, confirmed =>
        {
            Dialogs.Remove(dialog);
            tcs.TrySetResult(confirmed);
        });
        Dialogs.Add(dialog);
        return tcs.Task;
    }
}
