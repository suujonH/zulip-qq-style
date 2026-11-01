using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZulipQQ.Core;
using ZulipQQ.Models;
using ZulipQQ.Services;

namespace ZulipQQ.ViewModels;

/// <summary>
/// “新建话题”对话框：选择频道（只列出可以发言并新建话题的已订阅频道；开发者模式下也列出其它未归档的已订阅频道，
/// 确定时只模拟），输入话题名。
/// 关闭时调用 onClosed：确定传入话题键和原始话题名，取消传入 null。
/// <paramref name="streamId"/>、<paramref name="topic"/> 为预先填入的频道和话题名（打开已不存在的话题链接时）。
/// </summary>
public partial class NewTopicDialogViewModel : ViewModelBase
{
    private readonly ZulipSession _session;
    private readonly Action<string> _toast;
    private readonly Action<(TopicKey Key, string Name)?> _onClosed;

    public NewTopicDialogViewModel(ZulipSession session, Action<string> toast, Action<(TopicKey Key, string Name)?> onClosed,
        long? streamId = null, string topic = "")
    {
        _session = session;
        _toast = toast;
        _onClosed = onClosed;
        MaxLength = session.Realm.MaxTopicLength;
        Channels = session.Channels.Subscribed().Where(c => !c.IsArchived && session.Shows(CanCreateIn(session, c.StreamId))).ToList();
        SelectedChannel = Channels.FirstOrDefault(c => c.StreamId == streamId) ?? Channels.FirstOrDefault();
        Topic = topic;
    }

    /// <summary>可以在该频道新建话题（在可选的频道中）。</summary>
    public static bool CanCreateIn(ZulipSession session, long streamId) =>
        session.Channels.Get(streamId) is { IsSubscribed: true, IsArchived: false } c
        && session.Permissions.CanSendToChannel(c) && session.Permissions.CanCreateTopic(c) && session.Realm.AllowsNamedTopics(c);

    public IReadOnlyList<Channel> Channels { get; }

    public int MaxLength { get; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    public partial Channel? SelectedChannel { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    public partial string Topic { get; set; } = "";

    private bool CanConfirm => SelectedChannel is not null && Topic.Trim() is { Length: > 0 } t && t.Length <= MaxLength;

    [RelayCommand(CanExecute = nameof(CanConfirm))]
    private void Confirm()
    {
        if (_session.Mocks(CanCreateIn(_session, SelectedChannel!.StreamId), _toast))
            return;
        var name = Topic.Trim();
        _onClosed((new TopicKey(SelectedChannel!.StreamId, name), name));
    }

    [RelayCommand]
    private void Cancel() => _onClosed(null);
}
