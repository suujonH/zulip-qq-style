using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using ZulipQQ.Services;

namespace ZulipQQ.ViewModels;

/// <summary>“消息”页：会话列表。</summary>
public partial class MessagesPageViewModel : ViewModelBase
{
    public MessagesPageViewModel()
    {
        var now = DateTime.Now;
        // 置顶在前，各自按最后消息时间倒序
        Conversations = MockData.Conversations
            .OrderByDescending(c => c.IsPinned)
            .ThenByDescending(c => c.LastMessageTime)
            .Select(c => new ConversationItemViewModel(c, now))
            .ToList();
    }

    public IReadOnlyList<ConversationItemViewModel> Conversations { get; }

    [ObservableProperty]
    public partial ConversationItemViewModel? SelectedConversation { get; set; }
}
