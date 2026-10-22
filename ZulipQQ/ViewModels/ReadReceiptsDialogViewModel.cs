using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZulipQQ.Services;
using ZulipQQ.Zulip;

namespace ZulipQQ.ViewModels;

/// <summary>
/// 群聊消息的已读成员（read receipts）。不含自己和发送者；关闭了已读回执的用户服务器不返回。
/// </summary>
public partial class ReadReceiptsDialogViewModel : ViewModelBase
{
    private readonly ZulipSession _session;
    private readonly Action _onClosed;

    private ReadReceiptsDialogViewModel(ZulipSession session, Action onClosed)
    {
        _session = session;
        _onClosed = onClosed;
    }

    /// <summary>在对话框层中打开并加载已读成员。</summary>
    public static void Show(ZulipSession session, IDialogHost host, long messageId, long senderId)
    {
        ReadReceiptsDialogViewModel dialog = null!;
        dialog = new ReadReceiptsDialogViewModel(session, () => host.Dialogs.Remove(dialog));
        host.Dialogs.Add(dialog);
        _ = dialog.LoadAsync(messageId, senderId);
    }

    [ObservableProperty]
    public partial string Title { get; private set; } = "已读成员";

    [ObservableProperty]
    public partial IReadOnlyList<ChatMemberItem> Members { get; private set; } = [];

    /// <summary>加载中、加载失败或没有人已读时显示的文字。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    public partial string? StatusText { get; private set; } = "加载中…";

    public bool HasStatus => StatusText is not null;

    private async Task LoadAsync(long messageId, long senderId)
    {
        IReadOnlyList<long> ids;
        try
        {
            ids = await _session.Client.GetReadReceiptsAsync(messageId, _session.Token);
        }
        catch (Exception ex)
        {
            if (_session.IsDisposed)
                return;
            Log.Warn($"加载已读成员失败：{messageId}", ex);
            StatusText = ex is ZulipApiException { Message.Length: > 0 } api ? $"加载失败：{api.Message}" : "加载失败";
            return;
        }
        if (_session.IsDisposed)
            return;

        Members = ChatMembers.Build(_session, null, ids.Where(id => id != _session.SelfId && id != senderId));
        Title = $"已读成员 ({Members.Count})";
        StatusText = Members.Count == 0 ? "暂时没有成员已读" : null;
    }

    [RelayCommand]
    private void Close() => _onClosed();
}
