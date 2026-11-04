using System;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZulipQQ.Core;
using ZulipQQ.Services;

namespace ZulipQQ.ViewModels;

/// <summary>
/// 话题名片（弹出卡片和右侧资料面板共用）：头像、所属频道（话题名上方的小字，区分不同频道的同名话题）、话题名、
/// 频道备注与话题备注（分开设置，都只保存在本地）、“发消息”。频道名与话题名有备注时显示为“备注(原名称)”。
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

    /// <summary>话题被改名或移动时随之更新。</summary>
    public TopicKey Key { get; private set; }

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

    [ObservableProperty]
    public partial string ChannelRemarkText { get; private set; } = "";

    [ObservableProperty]
    public partial bool IsEditingChannelRemark { get; private set; }

    [ObservableProperty]
    public partial string ChannelRemarkDraft { get; set; } = "";

    public void Attach()
    {
        if (_attached)
            return;
        _attached = true;
        _session.Conversations.EntryChanged += OnEntryChanged;
        _session.Conversations.ConversationRekeyed += OnRekeyed;
        _session.Channels.ChannelChanged += OnChannelChanged;
        Refresh();
    }

    public void Detach()
    {
        if (!_attached)
            return;
        _attached = false;
        _session.Conversations.EntryChanged -= OnEntryChanged;
        _session.Conversations.ConversationRekeyed -= OnRekeyed;
        _session.Channels.ChannelChanged -= OnChannelChanged;
    }

    private void OnRekeyed(object? sender, (ConversationKey Old, ConversationKey New) e)
    {
        if (!e.Old.Equals(Key) || e.New is not TopicKey key)
            return;
        Key = key;
        Refresh();
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
        Title = _session.Conversations.GetTopicTitleWithOriginal(Key);
        AvatarFill = ConversationItemViewModel.BrushOf(channel?.Color);
        ChannelText = channel is null ? "" : $"#{_session.Channels.NameWithOriginal(Key.StreamId)}";
        IsPrivate = channel?.InviteOnly ?? false;
        RemarkText = _session.Conversations.Get(Key)?.Remark ?? "";
        ChannelRemarkText = _session.Contacts.GetChannelRemark(Key.StreamId) ?? "";
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

    [RelayCommand]
    private void BeginEditChannelRemark()
    {
        ChannelRemarkDraft = ChannelRemarkText;
        IsEditingChannelRemark = true;
    }

    [RelayCommand]
    private void CommitChannelRemark()
    {
        if (!IsEditingChannelRemark)
            return;
        IsEditingChannelRemark = false;
        _session.Channels.SetRemark(Key.StreamId, ChannelRemarkDraft);
    }

    [RelayCommand]
    private void CancelChannelRemark() => IsEditingChannelRemark = false;
}
