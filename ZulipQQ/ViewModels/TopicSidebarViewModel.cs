using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZulipQQ.Core;
using ZulipQQ.Services;

namespace ZulipQQ.ViewModels;

/// <summary>
/// 话题聊天的右侧栏：上半部分为群公告（频道描述），下半部分为群聊成员（群主、管理员在前）及搜索。
/// 显示期间（<see cref="Attach"/> 到 <see cref="Detach"/>）随数据变化刷新。
/// </summary>
public partial class TopicSidebarViewModel : ViewModelBase
{
    private readonly ChatViewModel _chat;
    private List<ChatMemberItem> _members = [];
    private int _membersVersion;
    private bool _attached;

    public TopicSidebarViewModel(ChatViewModel chat)
    {
        _chat = chat;
        Refresh();
    }

    private ZulipSession Session => _chat.Session;

    private long? StreamId => (_chat.Key as TopicKey)?.StreamId;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNotice))]
    public partial string NoticeHtml { get; private set; } = "";

    public bool HasNotice => NoticeHtml.Length > 0;

    [ObservableProperty]
    public partial string MembersTitle { get; private set; } = "群聊成员";

    [ObservableProperty]
    public partial bool IsSearching { get; private set; }

    [ObservableProperty]
    public partial string Query { get; set; } = "";

    [ObservableProperty]
    public partial IReadOnlyList<ChatMemberItem> Members { get; private set; } = [];

    public void Attach()
    {
        if (_attached)
            return;
        _attached = true;
        Session.Conversations.ConversationRekeyed += OnRekeyed;
        Session.Channels.ChannelChanged += OnChannelChanged;
        Session.Channels.SubscribersChanged += OnSubscribersChanged;
        Session.Users.UserChanged += OnUserChanged;
        Session.Contacts.Changed += OnNamesChanged;
        Session.PermissionsChanged += OnNamesChanged;
        Session.Resynced += OnResynced;
        Refresh();
        _ = LoadMembersAsync();
    }

    public void Detach()
    {
        if (!_attached)
            return;
        _attached = false;
        Session.Conversations.ConversationRekeyed -= OnRekeyed;
        Session.Channels.ChannelChanged -= OnChannelChanged;
        Session.Channels.SubscribersChanged -= OnSubscribersChanged;
        Session.Users.UserChanged -= OnUserChanged;
        Session.Contacts.Changed -= OnNamesChanged;
        Session.PermissionsChanged -= OnNamesChanged;
        Session.Resynced -= OnResynced;
    }

    // 话题被移动到其他频道：公告和成员都换成新频道的
    private void OnRekeyed(object? sender, (ConversationKey Old, ConversationKey New) e)
    {
        if (e.New.Equals(_chat.Key))
        {
            Refresh();
            _ = LoadMembersAsync();
        }
    }

    private void OnChannelChanged(object? sender, long streamId)
    {
        if (streamId != StreamId)
            return;
        Refresh();
        // 频道管理员设置可能变了，徽标随之变化
        _ = LoadMembersAsync();
    }

    private void OnSubscribersChanged(object? sender, long streamId)
    {
        if (streamId == StreamId)
            _ = LoadMembersAsync();
    }

    private void OnUserChanged(object? sender, long userId)
    {
        if (_members.Exists(m => m.UserId == userId))
            _ = LoadMembersAsync();
    }

    private void OnNamesChanged(object? sender, EventArgs e) => _ = LoadMembersAsync();

    // 重新 register 后频道整体重建，不会逐个触发 ChannelChanged
    private void OnResynced(object? sender, EventArgs e)
    {
        Refresh();
        _ = LoadMembersAsync();
    }

    private void Refresh()
    {
        var channel = StreamId is { } id ? Session.Channels.Get(id) : null;
        NoticeHtml = channel?.RenderedDescription.Trim() ?? "";
    }

    private async Task LoadMembersAsync()
    {
        if (StreamId is not { } streamId)
            return;
        var version = ++_membersVersion;
        IReadOnlyList<long> ids;
        try
        {
            ids = await Session.Channels.GetSubscribersAsync(streamId);
        }
        catch (Exception ex)
        {
            if (!Session.IsDisposed)
                Log.Warn($"加载频道成员失败：{streamId}", ex);
            return;
        }
        if (version != _membersVersion || !_attached || Session.IsDisposed)
            return;

        _members = ChatMembers.Build(Session, Session.Channels.Get(streamId), ids);
        MembersTitle = $"群聊成员 {_members.Count}";
        ApplyFilter();
    }

    partial void OnQueryChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        var q = Query.Trim();
        Members = q.Length == 0 ? _members : _members.Where(m => m.Matches(q)).ToList();
    }

    [RelayCommand]
    private void ToggleSearch()
    {
        IsSearching = !IsSearching;
        if (!IsSearching)
            Query = "";
    }

    [RelayCommand]
    private void ShowNotice()
    {
        if (StreamId is { } id)
            ChannelNoticeDialogViewModel.Show(_chat.Main, _chat.Host, id);
    }

    /// <summary>点击成员弹出的名片。</summary>
    /// <summary>所属的聊天（成员的双击、右键菜单）。</summary>
    public ChatViewModel Chat => _chat;
}
