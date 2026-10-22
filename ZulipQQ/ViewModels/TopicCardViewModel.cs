using System;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZulipQQ.Core;
using ZulipQQ.Services;

namespace ZulipQQ.ViewModels;

/// <summary>
/// 话题名片（弹出卡片和右侧资料面板共用）：头像、话题名、所属频道、本地备注、“发消息”。
/// 显示期间（<see cref="Attach"/> 到 <see cref="Detach"/>）随数据变化刷新。
/// </summary>
public partial class TopicCardViewModel : ViewModelBase
{
    private readonly ZulipSession _session;
    private readonly Action<ConversationKey> _openChat;
    private readonly Action? _onOpened;
    private bool _attached;

    public TopicCardViewModel(ZulipSession session, TopicKey key, Action<ConversationKey> openChat, Action? onOpened = null)
    {
        _session = session;
        _openChat = openChat;
        _onOpened = onOpened;
        Key = key;
        Refresh();
    }

    public TopicKey Key { get; }

    [ObservableProperty]
    public partial string Title { get; private set; } = "";

    [ObservableProperty]
    public partial IBrush? AvatarFill { get; private set; }

    [ObservableProperty]
    public partial string ChannelText { get; private set; } = "";

    [ObservableProperty]
    public partial bool IsPrivate { get; private set; }

    [ObservableProperty]
    public partial string RemarkText { get; private set; } = "";

    [ObservableProperty]
    public partial bool IsEditingRemark { get; private set; }

    [ObservableProperty]
    public partial string RemarkDraft { get; set; } = "";

    public void Attach()
    {
        if (_attached)
            return;
        _attached = true;
        _session.Conversations.EntryChanged += OnEntryChanged;
        _session.Channels.ChannelChanged += OnChannelChanged;
        Refresh();
    }

    public void Detach()
    {
        if (!_attached)
            return;
        _attached = false;
        _session.Conversations.EntryChanged -= OnEntryChanged;
        _session.Channels.ChannelChanged -= OnChannelChanged;
    }

    private void OnEntryChanged(object? sender, ConversationKey key)
    {
        if (key.Equals(Key))
            Refresh();
    }

    private void OnChannelChanged(object? sender, long streamId)
    {
        if (streamId == Key.StreamId)
            Refresh();
    }

    private void Refresh()
    {
        var channel = _session.Channels.Get(Key.StreamId);
        Title = _session.Conversations.GetTitle(Key);
        AvatarFill = ConversationItemViewModel.BrushOf(channel?.Color);
        ChannelText = channel is null ? "" : $"#{channel.Name}";
        IsPrivate = channel?.InviteOnly ?? false;
        RemarkText = _session.Conversations.Get(Key)?.Remark ?? "";
    }

    [RelayCommand]
    private void SendMessage()
    {
        _openChat(Key);
        _onOpened?.Invoke();
    }

    [RelayCommand]
    private void BeginEditRemark()
    {
        RemarkDraft = RemarkText;
        IsEditingRemark = true;
    }

    [RelayCommand]
    private void CommitRemark()
    {
        if (!IsEditingRemark)
            return;
        IsEditingRemark = false;
        _session.Conversations.SetRemark(Key, RemarkDraft);
    }

    [RelayCommand]
    private void CancelRemark() => IsEditingRemark = false;
}
