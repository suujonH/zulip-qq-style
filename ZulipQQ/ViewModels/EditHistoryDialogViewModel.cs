using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZulipQQ.Controls;
using ZulipQQ.Core;
using ZulipQQ.Services;
using ZulipQQ.Zulip;
using ZulipQQ.Zulip.Dtos;

namespace ZulipQQ.ViewModels;

/// <summary>编辑历史中的一个版本。<see cref="Html"/> 为内容（编辑后为与上一版本的差异）；只移动了话题 / 频道时为 null。</summary>
public sealed record EditHistoryEntry(string Header, string TimeText, string? MoveText, string? Html)
{
    public bool HasMove => MoveText is not null;

    public bool HasHtml => Html is not null;
}

/// <summary>
/// 消息的编辑历史：按时间先后列出原始版本和每次编辑。内容的修改显示差异（新增的文字高亮，删除的文字加删除线）。
/// 能看到哪些历史由服务器按组织的编辑历史可见性决定。
/// </summary>
public partial class EditHistoryDialogViewModel : ViewModelBase
{
    private readonly ZulipSession _session;
    private readonly Action _onClosed;
    private readonly Func<MessageLinkTarget, TopLevel?, Task> _handleLink;

    private EditHistoryDialogViewModel(ZulipSession session, Action onClosed, Func<MessageLinkTarget, TopLevel?, Task> handleLink)
    {
        _session = session;
        _onClosed = onClosed;
        _handleLink = handleLink;
    }

    public ZulipSession Session => _session;

    /// <summary>
    /// 在对话框层中打开并加载历史。<paramref name="handleLink"/> 处理内容中链接、文件的点击（与聊天中相同）；
    /// 图片和提及由视图处理。
    /// </summary>
    public static void Show(ZulipSession session, IDialogHost host, long messageId, Func<MessageLinkTarget, TopLevel?, Task> handleLink)
    {
        EditHistoryDialogViewModel dialog = null!;
        dialog = new EditHistoryDialogViewModel(session, () => host.Dialogs.Remove(dialog), handleLink);
        host.Dialogs.Add(dialog);
        _ = dialog.LoadAsync(messageId);
    }

    [ObservableProperty]
    public partial IReadOnlyList<EditHistoryEntry> Entries { get; private set; } = [];

    /// <summary>加载中或加载失败时显示的文字；加载完成后为 null。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    public partial string? StatusText { get; private set; } = "加载中…";

    public bool HasStatus => StatusText is not null;

    private async Task LoadAsync(long messageId)
    {
        IReadOnlyList<MessageHistoryDto> history;
        try
        {
            history = await _session.Client.GetMessageHistoryAsync(messageId, _session.Token);
        }
        catch (Exception ex)
        {
            if (_session.IsDisposed)
                return;
            Log.Warn($"加载编辑历史失败：{messageId}", ex);
            StatusText = ex is ZulipApiException { Message.Length: > 0 } api ? $"加载失败：{api.Message}" : "加载失败";
            return;
        }
        if (_session.IsDisposed)
            return;

        var now = DateTime.Now;
        var use24h = _session.Settings.Zulip.TwentyFourHourTime;
        List<EditHistoryEntry> entries = [];
        for (var i = 0; i < history.Count; i++)
        {
            var h = history[i];
            var editor = h.UserId is { } userId ? _session.Contacts.DisplayName(userId) : "系统";
            var time = TimeText.PreciseTime(TimeText.FromUnix(h.Timestamp), now, use24h);
            if (i == 0)
            {
                entries.Add(new EditHistoryEntry($"{editor} 发送", time, null, h.RenderedContent));
                continue;
            }

            List<string> moves = [];
            if (h.PrevStream is { } prevStream)
                moves.Add($"频道：#{ChannelName(prevStream)} → #{ChannelName(h.Stream ?? prevStream)}");
            if (h.PrevTopic is { } prevTopic)
                moves.Add($"话题：{TopicName(prevTopic)} → {TopicName(h.Topic)}");
            var contentChanged = h.PrevContent is not null;
            var html = contentChanged ? h.ContentHtmlDiff ?? h.RenderedContent : null;
            entries.Add(new EditHistoryEntry($"{editor} {(contentChanged ? "编辑" : "移动")}", time,
                moves.Count > 0 ? string.Join("\n", moves) : null, html));
        }
        Entries = entries;
        StatusText = entries.Count == 0 ? "没有可以查看的编辑历史" : null;
    }

    private string ChannelName(long streamId) => _session.Channels.Get(streamId)?.Name ?? streamId.ToString();

    private string TopicName(string topic) => topic.Length == 0 ? _session.Realm.EmptyTopicDisplayName : topic;

    [RelayCommand]
    private void Close() => _onClosed();

    /// <summary>内容中链接、文件的点击。</summary>
    public Task HandleLinkAsync(MessageLinkTarget target, TopLevel? top) => _handleLink(target, top);
}
