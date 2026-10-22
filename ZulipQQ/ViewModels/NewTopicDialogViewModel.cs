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
/// “新建话题”对话框：选择频道（只列出可以发言并新建话题的已订阅频道），输入话题名。
/// 关闭时调用 onClosed：确定传入话题键和原始话题名，取消传入 null。
/// </summary>
public partial class NewTopicDialogViewModel : ViewModelBase
{
    private readonly Action<(TopicKey Key, string Name)?> _onClosed;

    public NewTopicDialogViewModel(ZulipSession session, Action<(TopicKey Key, string Name)?> onClosed)
    {
        _onClosed = onClosed;
        MaxLength = session.Realm.MaxTopicLength;
        Channels = session.Channels.Subscribed()
            .Where(c => !c.IsArchived && session.Permissions.CanSendToChannel(c) && session.Permissions.CanCreateTopic(c)
                        && session.Realm.AllowsNamedTopics(c))
            .ToList();
        SelectedChannel = Channels.FirstOrDefault();
    }

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
        var name = Topic.Trim();
        _onClosed((new TopicKey(SelectedChannel!.StreamId, name), name));
    }

    [RelayCommand]
    private void Cancel() => _onClosed(null);
}
