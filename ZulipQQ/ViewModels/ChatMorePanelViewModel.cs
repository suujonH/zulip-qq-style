using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZulipQQ.Core;
using ZulipQQ.Models;
using ZulipQQ.Services;
using ZulipQQ.Zulip;

namespace ZulipQQ.ViewModels;

/// <summary>
/// 聊天标题栏“…”打开的更多面板。话题：信息卡、群聊成员、资料管理、群公告、备注、开关、话题操作、删除话题；
/// 一对一私聊：置顶、消息免打扰、屏蔽此人；多人私聊：成员、备注、置顶、群消息设置；与自己的私聊：置顶、免打扰。
/// 没有权限的项完全不显示。显示期间（<see cref="Attach"/> 到 <see cref="Detach"/>）随数据变化刷新。
/// </summary>
public partial class ChatMorePanelViewModel : ViewModelBase
{
    /// <summary>成员卡中直接显示的人数。</summary>
    private const int PreviewMemberCount = 10;

    private readonly ChatViewModel _chat;
    private readonly MainViewModel _main;
    private List<ChatMemberItem> _members = [];
    private int _membersVersion;
    private bool _attached;

    // Refresh 写入开关属性时不触发对应的操作
    private bool _refreshing;

    public ChatMorePanelViewModel(ChatViewModel chat, MainViewModel main)
    {
        _chat = chat;
        _main = main;
        Refresh();
    }

    private ZulipSession Session => _chat.Session;

    public MainViewModel Main => _main;

    /// <summary>对话框和提示显示在聊天所在的窗口。</summary>
    public IDialogHost Host => _chat.Host;

    // 话题操作的确认框同样显示在聊天所在的窗口
    private TopicActions Topics => new(_main, Host);

    public ConversationKey Key => _chat.Key;

    public bool IsTopic => Key is TopicKey;

    public bool IsGroupDm => Key is DmKey { IsGroupDm: true };

    public bool IsOneToOne => Key is DmKey { OtherUserIds.Length: 1 };

    /// <summary>一对一私聊和与自己的私聊：免打扰只有开关两种状态。</summary>
    public bool HasDndSwitch => Key is DmKey { IsGroupDm: false };

    /// <summary>话题和多人私聊：成员、备注、四级群消息设置。</summary>
    public bool IsGroup => IsTopic || IsGroupDm;

    public IReadOnlyList<LevelOption> LevelOptions => LevelOption.All;

    // ---- 信息卡（话题） ----

    [ObservableProperty]
    public partial string Title { get; private set; } = "";

    [ObservableProperty]
    public partial string ChannelText { get; private set; } = "";

    [ObservableProperty]
    public partial bool IsPrivate { get; private set; }

    [ObservableProperty]
    public partial IBrush? AvatarFill { get; private set; }

    // ---- 群聊成员 ----

    [ObservableProperty]
    public partial IReadOnlyList<ChatMemberItem> PreviewMembers { get; private set; } = [];

    [ObservableProperty]
    public partial string MemberCountText { get; private set; } = "";

    [ObservableProperty]
    public partial string MemberListTitle { get; private set; } = "";

    /// <summary>面板切换到完整的成员列表。</summary>
    [ObservableProperty]
    public partial bool IsMemberListOpen { get; private set; }

    [ObservableProperty]
    public partial string MemberQuery { get; set; } = "";

    [ObservableProperty]
    public partial IReadOnlyList<ChatMemberItem> FilteredMembers { get; private set; } = [];

    [ObservableProperty]
    public partial bool CanInvite { get; private set; }

    [ObservableProperty]
    public partial bool CanRemove { get; private set; }

    // ---- 资料管理、群公告 ----

    [ObservableProperty]
    public partial bool CanEditChannel { get; private set; }

    [ObservableProperty]
    public partial string NoticeText { get; private set; } = "";

    // ---- 备注 ----

    [ObservableProperty]
    public partial string RemarkText { get; private set; } = "";

    [ObservableProperty]
    public partial bool IsEditingRemark { get; private set; }

    [ObservableProperty]
    public partial string RemarkDraft { get; set; } = "";

    // ---- 开关 ----

    [ObservableProperty]
    public partial bool IsPinned { get; set; }

    [ObservableProperty]
    public partial LevelOption? SelectedLevel { get; set; }

    [ObservableProperty]
    public partial bool IsDndOn { get; set; }

    [ObservableProperty]
    public partial bool IsFollowed { get; set; }

    [ObservableProperty]
    public partial bool IsUserMuted { get; set; }

    // ---- 话题操作 ----

    [ObservableProperty]
    public partial bool CanResolve { get; private set; }

    [ObservableProperty]
    public partial string ResolveText { get; private set; } = "";

    [ObservableProperty]
    public partial bool CanMove { get; private set; }

    [ObservableProperty]
    public partial bool CanRename { get; private set; }

    [ObservableProperty]
    public partial bool CanDelete { get; private set; }

    /// <summary>话题操作卡至少有一项。</summary>
    [ObservableProperty]
    public partial bool HasTopicActions { get; private set; }

    /// <summary>开始随数据刷新。面板已关闭（视图还在播放滑出动画或重新挂载）时不做任何事。</summary>
    public void Attach()
    {
        if (_attached || !_chat.IsMorePanelOpen || _chat.MorePanel != this)
            return;
        _attached = true;
        Session.Conversations.EntryChanged += OnEntryChanged;
        Session.Conversations.ListChanged += OnChanged;
        Session.Conversations.ConversationRekeyed += OnRekeyed;
        Session.Channels.ChannelChanged += OnChannelChanged;
        Session.Channels.SubscribersChanged += OnSubscribersChanged;
        Session.Users.MutedChanged += OnChanged;
        Session.Users.UserChanged += OnUserChanged;
        Session.Contacts.Changed += OnNamesChanged;
        Session.PermissionsChanged += OnChanged;
        Session.Resynced += OnResynced;
        Refresh();
        _ = LoadMembersAsync();
    }

    public void Detach()
    {
        if (!_attached)
            return;
        _attached = false;
        Session.Conversations.EntryChanged -= OnEntryChanged;
        Session.Conversations.ListChanged -= OnChanged;
        Session.Conversations.ConversationRekeyed -= OnRekeyed;
        Session.Channels.ChannelChanged -= OnChannelChanged;
        Session.Channels.SubscribersChanged -= OnSubscribersChanged;
        Session.Users.MutedChanged -= OnChanged;
        Session.Users.UserChanged -= OnUserChanged;
        Session.Contacts.Changed -= OnNamesChanged;
        Session.PermissionsChanged -= OnChanged;
        Session.Resynced -= OnResynced;
    }

    private void OnChanged(object? sender, EventArgs e) => Refresh();

    // 重新 register 后频道、成员整体重建，不会逐个触发变化事件
    private void OnResynced(object? sender, EventArgs e)
    {
        Refresh();
        _ = LoadMembersAsync();
    }

    private void OnEntryChanged(object? sender, ConversationKey key)
    {
        if (key.Equals(Key))
            Refresh();
    }

    private void OnRekeyed(object? sender, (ConversationKey Old, ConversationKey New) e)
    {
        // 聊天先换成新键，这里只需按新键刷新
        if (e.New.Equals(Key))
        {
            Refresh();
            _ = LoadMembersAsync();
        }
    }

    private void OnChannelChanged(object? sender, long streamId)
    {
        if (Key is TopicKey t && t.StreamId == streamId)
            Refresh();
    }

    private void OnSubscribersChanged(object? sender, long streamId)
    {
        if (Key is TopicKey t && t.StreamId == streamId)
            _ = LoadMembersAsync();
    }

    private void OnUserChanged(object? sender, long userId)
    {
        if (_members.Exists(m => m.UserId == userId))
            _ = LoadMembersAsync();
    }

    private void OnNamesChanged(object? sender, EventArgs e) => _ = LoadMembersAsync();

    private void Refresh()
    {
        _refreshing = true;
        try
        {
            var key = Key;
            var conversations = Session.Conversations;
            var entry = conversations.Get(key);
            var level = conversations.GetLevel(key);
            Title = conversations.GetTitle(key);
            RemarkText = entry?.Remark ?? "";
            IsPinned = entry?.PinnedAt is not null;
            SelectedLevel = LevelOption.All.FirstOrDefault(o => o.Level == level);
            IsDndOn = level != DndLevel.Notify;
            IsUserMuted = key is DmKey { OtherUserIds.Length: 1 } dm && Session.Users.IsMuted(dm.OtherUserIds[0]);

            var channel = key is TopicKey t ? Session.Channels.Get(t.StreamId) : null;
            ChannelText = channel is null ? "" : $"#{channel.Name}";
            IsPrivate = channel?.InviteOnly ?? false;
            AvatarFill = ConversationItemViewModel.BrushOf(channel?.Color);
            NoticeText = FirstLine(channel is null ? null : ZulipHtml.ToPlainText(channel.RenderedDescription));

            var permissions = Session.Permissions;
            var live = channel is { IsArchived: false };
            CanInvite = live && permissions.CanAddSubscribers(channel!);
            CanRemove = live && permissions.CanRemoveSubscribers(channel!);
            CanEditChannel = live && permissions.CanAdministerChannel(channel!);

            if (key is TopicKey topic)
            {
                var topics = Topics;
                IsFollowed = topics.IsFollowed(topic);
                CanResolve = topics.CanResolve(topic);
                ResolveText = topics.IsResolved(topic) ? "取消已解决" : "标记为已解决";
                CanMove = topics.CanMove(topic) && topics.MoveTargets(topic).Any();
                CanRename = topics.CanRename(topic);
                CanDelete = topics.CanDelete(topic);
            }
            else
            {
                IsFollowed = CanResolve = CanMove = CanRename = CanDelete = false;
            }
            HasTopicActions = CanResolve || CanMove || CanRename;
        }
        finally
        {
            _refreshing = false;
        }
    }

    private static string FirstLine(string? text) =>
        text?.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";

    /// <summary>话题为频道订阅者，多人私聊为参与者（包括自己）。</summary>
    private async Task LoadMembersAsync()
    {
        var version = ++_membersVersion;
        var key = Key;
        IReadOnlyList<long> ids;
        Channel? channel = null;
        switch (key)
        {
            case TopicKey t:
                channel = Session.Channels.Get(t.StreamId);
                try
                {
                    ids = await Session.Channels.GetSubscribersAsync(t.StreamId);
                }
                catch (Exception ex)
                {
                    if (!Session.IsDisposed)
                        Log.Warn($"加载频道成员失败：{t.StreamId}", ex);
                    return;
                }
                break;
            case DmKey { IsGroupDm: true } d:
                ids = [.. d.OtherUserIds, Session.SelfId];
                break;
            default:
                return;
        }
        if (version != _membersVersion || !_attached || Session.IsDisposed)
            return;

        _members = ChatMembers.Build(Session, channel, ids);
        PreviewMembers = _members.Take(PreviewMemberCount).ToList();
        MemberCountText = $"查看{_members.Count}名群成员";
        MemberListTitle = $"群聊成员 {_members.Count}";
        ApplyMemberFilter();
    }

    partial void OnMemberQueryChanged(string value) => ApplyMemberFilter();

    private void ApplyMemberFilter()
    {
        var q = MemberQuery.Trim();
        FilteredMembers = q.Length == 0 ? _members : _members.Where(m => m.Matches(q)).ToList();
    }

    [RelayCommand]
    private void OpenMemberList()
    {
        MemberQuery = "";
        IsMemberListOpen = true;
    }

    [RelayCommand]
    private void CloseMemberList() => IsMemberListOpen = false;

    /// <summary>邀请：只列出还不是成员的用户，确定后为他们订阅频道。</summary>
    [RelayCommand]
    private void Invite()
    {
        if (Key is not TopicKey t || Session.Channels.Get(t.StreamId) is not { } channel)
            return;
        var members = _members.Select(m => m.UserId).ToHashSet();
        UserPickerDialogViewModel picker = null!;
        picker = new UserPickerDialogViewModel(Session, "邀请成员", 1, null, ids =>
        {
            Host.Dialogs.Remove(picker);
            if (ids is not null)
                _ = ChangeSubscribersAsync(channel, ids, add: true);
        }, Session.Users.ActiveHumans(excludeSelf: true).Where(u => !members.Contains(u.Id)));
        Host.Dialogs.Add(picker);
    }

    /// <summary>移出：列出全部成员（含机器人，不含自己），确认后为他们退订频道。</summary>
    [RelayCommand]
    private void RemoveMembers()
    {
        if (Key is not TopicKey t || Session.Channels.Get(t.StreamId) is not { } channel)
            return;
        var members = _members.Select(m => Session.Users.Get(m.UserId)).OfType<ZulipUser>().ToList();
        UserPickerDialogViewModel picker = null!;
        picker = new UserPickerDialogViewModel(Session, "移出成员", 1, null, async ids =>
        {
            Host.Dialogs.Remove(picker);
            if (ids is not null && await Host.ConfirmAsync($"将从频道 #{channel.Name} 中移出所选成员", "移出", isDanger: true))
                await ChangeSubscribersAsync(channel, ids, add: false);
        }, members);
        Host.Dialogs.Add(picker);
    }

    private async Task ChangeSubscribersAsync(Channel channel, IReadOnlyList<long> userIds, bool add)
    {
        var action = add ? "邀请" : "移出";
        try
        {
            if (add)
                await Session.Client.SubscribeAsync([channel.Name], userIds, Session.Token);
            else
                await Session.Client.UnsubscribeAsync([channel.Name], userIds, Session.Token);
            // 成员列表由 peer_add / peer_remove 事件更新
            Host.ShowToast(add ? "已邀请" : "已移出");
        }
        catch (Exception ex) when (!Session.IsDisposed)
        {
            Log.Warn($"{action}成员失败：{channel.StreamId}", ex);
            Host.ShowToast(ex is ZulipApiException { Message.Length: > 0 } api ? $"{action}失败：{api.Message}" : $"{action}失败");
        }
    }

    [RelayCommand]
    private void ShowChannelSettings()
    {
        if (Key is TopicKey t)
            ChannelSettingsDialogViewModel.Show(_main, Host, t.StreamId);
    }

    [RelayCommand]
    private void ShowNotice()
    {
        if (Key is TopicKey t)
            ChannelNoticeDialogViewModel.Show(_main, Host, t.StreamId);
    }

    // ---- 备注 ----

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
        Session.Conversations.SetRemark(Key, RemarkDraft);
    }

    [RelayCommand]
    private void CancelRemark() => IsEditingRemark = false;

    // ---- 开关 ----

    partial void OnIsPinnedChanged(bool value)
    {
        if (!_refreshing)
            Session.Conversations.SetPinned(Key, value);
    }

    partial void OnSelectedLevelChanged(LevelOption? value)
    {
        if (!_refreshing && value is not null)
            _ = SetLevelAsync(value.Level);
    }

    partial void OnIsDndOnChanged(bool value)
    {
        if (!_refreshing)
            _ = SetLevelAsync(value ? DndLevel.Silent : DndLevel.Notify);
    }

    private async Task SetLevelAsync(DndLevel level)
    {
        var key = Key;
        if (Session.Conversations.GetLevel(key) != level)
        {
            try
            {
                await Session.Conversations.SetLevelAsync(key, level);
            }
            catch (Exception ex) when (!Session.IsDisposed)
            {
                Log.Warn($"设置免打扰失败：{key.ToStorageString()}", ex);
                Host.ShowToast("设置失败");
            }
        }
        // 失败时恢复显示
        if (_attached)
            Refresh();
    }

    partial void OnIsFollowedChanged(bool value)
    {
        if (!_refreshing && Key is TopicKey t)
            _ = ToggleFollowAsync(t);
    }

    private async Task ToggleFollowAsync(TopicKey key)
    {
        await Topics.ToggleFollowAsync(key);
        if (_attached)
            Refresh();
    }

    partial void OnIsUserMutedChanged(bool value)
    {
        if (!_refreshing && Key is DmKey { OtherUserIds.Length: 1 } dm)
            _ = SetUserMutedAsync(dm.OtherUserIds[0], value);
    }

    /// <summary>屏蔽此人（先确认）/ 取消屏蔽。取消确认或失败时开关恢复原状；成功后由 muted_users 事件刷新。</summary>
    private async Task SetUserMutedAsync(long userId, bool muted)
    {
        try
        {
            if (muted && !await Host.ConfirmAsync("屏蔽后将不再显示对方的消息", "屏蔽"))
                return;
            if (muted)
                await Session.Client.MuteUserAsync(userId, Session.Token);
            else
                await Session.Client.UnmuteUserAsync(userId, Session.Token);
        }
        catch (Exception ex) when (!Session.IsDisposed)
        {
            Log.Warn($"设置屏蔽失败：{userId}", ex);
            Host.ShowToast("设置失败");
        }
        finally
        {
            if (_attached)
                Refresh();
        }
    }

    // ---- 话题操作 ----

    [RelayCommand]
    private void ToggleResolved()
    {
        if (Key is TopicKey t)
            _ = Topics.ToggleResolvedAsync(t);
    }

    [RelayCommand]
    private void Rename()
    {
        if (Key is TopicKey t)
            Topics.ShowRenameDialog(t);
    }

    [RelayCommand]
    private void DeleteTopic()
    {
        if (Key is TopicKey t)
            _ = Topics.DeleteAsync(t);
    }

    public IEnumerable<Channel> MoveTargets() => Key is TopicKey t ? Topics.MoveTargets(t) : [];

    public void MoveTo(Channel target)
    {
        if (Key is TopicKey t)
            _ = Topics.MoveAsync(t, target);
    }

    public string Link => Key is TopicKey t ? Topics.Link(t) : "";

    /// <summary>点击成员弹出的名片。</summary>
    public UserCardViewModel? CreateUserCard(long userId, Action onOpened) => _chat.CreateUserCard(userId, onOpened);
}
