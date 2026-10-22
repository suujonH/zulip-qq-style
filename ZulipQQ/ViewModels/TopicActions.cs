using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using ZulipQQ.Core;
using ZulipQQ.Models;
using ZulipQQ.Services;
using ZulipQQ.Zulip;
using ZulipQQ.Zulip.Dtos;

namespace ZulipQQ.ViewModels;

/// <summary>
/// 话题操作：移动到其他频道、标记已解决、重命名、删除、关注、复制链接。联系人页和消息页的右键菜单共用。
/// 各操作的 Can… 方法返回 false 时，界面完全不显示对应菜单项。结果由服务器事件更新到各 Store。
/// </summary>
public sealed class TopicActions(MainViewModel main, IDialogHost host)
{
    /// <summary>Zulip 已解决话题的前缀。</summary>
    public const string ResolvedPrefix = "✔ ";

    private ZulipSession Session => main.Session;

    private PermissionEvaluator Permissions => Session.Permissions;

    public Channel? ChannelOf(TopicKey key) => Session.Channels.Get(key.StreamId);

    public static bool IsResolvedName(string topic) => topic.StartsWith(ResolvedPrefix, StringComparison.Ordinal);

    public bool IsResolved(TopicKey key) => IsResolvedName(Session.Conversations.GetTopicName(key));

    public bool IsFollowed(TopicKey key) =>
        Session.Channels.TopicPolicy(key.StreamId, key.Topic) == TopicVisibility.Followed;

    public bool CanMove(TopicKey key) => ChannelOf(key) is { } ch && Permissions.CanMoveTopicBetweenChannels(ch);

    /// <summary>
    /// 可以移入的频道：其他已订阅、未归档、可以发言、可以新建话题的频道；有名字的话题不能移入只允许“综合聊天”的频道。
    /// </summary>
    public IEnumerable<Channel> MoveTargets(TopicKey key)
    {
        var named = Session.Conversations.GetTopicName(key).Length > 0;
        return Session.Channels.Subscribed().Where(c => c.StreamId != key.StreamId && !c.IsArchived
            && Permissions.CanSendToChannel(c) && Permissions.CanCreateTopic(c)
            && (!named || Session.Realm.AllowsNamedTopics(c)));
    }

    /// <summary>空话题不能标记已解决。</summary>
    public bool CanResolve(TopicKey key) =>
        ChannelOf(key) is { } ch && Permissions.CanResolveTopic(ch) && Session.Conversations.GetTopicName(key).Length > 0;

    public bool CanRename(TopicKey key) => ChannelOf(key) is { } ch && Permissions.CanMoveTopicWithinChannel(ch);

    public bool CanDelete(TopicKey key) => ChannelOf(key) is { } ch && Permissions.CanDeleteTopic(ch);

    public string Link(TopicKey key) =>
        ZulipLinks.TopicLink(Session.Client.RealmUrl, key.StreamId, ChannelOf(key)?.Name ?? "", Session.Conversations.GetTopicName(key));

    /// <summary>把整个话题移动到 <paramref name="target"/>（先确认）。</summary>
    public async Task MoveAsync(TopicKey key, Channel target)
    {
        var topic = Session.Conversations.GetTitle(key);
        if (!await host.ConfirmAsync($"将话题「{topic}」移动到 #{target.Name}？"))
            return;
        await ChangeAsync(key, null, target.StreamId, "移动");
    }

    /// <summary>标记已解决：话题名加上或去掉“✔ ”前缀。</summary>
    public Task ToggleResolvedAsync(TopicKey key)
    {
        var name = Session.Conversations.GetTopicName(key);
        var newName = IsResolvedName(name) ? StripResolved(name) : ResolvedPrefix + name;
        return ChangeAsync(key, newName, null, "操作");
    }

    /// <summary>打开重命名对话框，确定后修改整个话题的名称。</summary>
    public void ShowRenameDialog(TopicKey key)
    {
        var name = Session.Conversations.GetTopicName(key);
        TextInputDialogViewModel dialog = null!;
        dialog = new TextInputDialogViewModel("重命名话题", "填写话题名称", name, Session.Realm.MaxTopicLength, newName =>
        {
            host.Dialogs.Remove(dialog);
            if (newName is not null)
                _ = ChangeAsync(key, newName, null, "重命名");
        });
        host.Dialogs.Add(dialog);
    }

    /// <summary>删除话题（先确认，确认按钮为红色）。服务器可能分批删除，直到返回全部完成。</summary>
    public async Task DeleteAsync(TopicKey key)
    {
        if (!await host.ConfirmAsync("删除后话题中的所有消息将被永久删除，且无法恢复", "删除", isDanger: true))
            return;
        var name = Session.Conversations.GetTopicName(key);
        try
        {
            while (!await Session.Client.DeleteTopicAsync(key.StreamId, name, Session.Token))
            {
                if (Session.IsDisposed)
                    return;
            }
        }
        catch (Exception ex) when (!Session.IsDisposed)
        {
            Log.Warn($"删除话题失败：{key.ToStorageString()}", ex);
            host.ShowToast(FailureText("删除", ex));
            return;
        }
        if (Session.IsDisposed)
            return;
        // 话题已不存在，同时取消置顶，否则联系人页的“置顶群聊”中会留下空条目
        if (Session.Conversations.Get(key) is { PinnedAt: not null })
            Session.Conversations.SetPinned(key, false);
        Session.Conversations.Remove(key);
        Session.Registry.CloseWindows(key);
        await main.CloseChatAsync(key);
    }

    /// <summary>关注 / 取消关注。取消关注时，所在频道静音则设为“取消静音”，以保持原来的提醒状态。</summary>
    public async Task ToggleFollowAsync(TopicKey key)
    {
        var channel = ChannelOf(key);
        var policy = IsFollowed(key)
            ? channel is { IsMuted: true } ? TopicVisibility.Unmuted : TopicVisibility.Inherit
            : TopicVisibility.Followed;
        var name = Session.Conversations.GetTopicName(key);
        try
        {
            await Session.Client.SetTopicVisibilityAsync(key.StreamId, name, policy, Session.Token);
        }
        catch (Exception ex) when (!Session.IsDisposed)
        {
            Log.Warn($"设置关注失败：{key.ToStorageString()}", ex);
            host.ShowToast("设置失败");
            return;
        }
        if (Session.IsDisposed)
            return;
        // 与设置免打扰级别相同：立即在本地生效，不等 user_topic 事件
        Session.Channels.OnUserTopic(new UserTopicDto { StreamId = key.StreamId, TopicName = name, VisibilityPolicy = policy });
        Session.Conversations.RecomputeLevels(key.StreamId);
    }

    private static string StripResolved(string name)
    {
        while (IsResolvedName(name))
            name = name[ResolvedPrefix.Length..];
        return name;
    }

    /// <summary>
    /// 修改整个话题（改名和 / 或换频道）。部分消息超出移动时限时服务器一条都不会移动：
    /// 全部超时只提示；否则询问是否只移动时限内的消息，确认后从第一条可移动的消息开始移动。
    /// </summary>
    private async Task ChangeAsync(TopicKey key, string? newTopic, long? newStreamId, string action)
    {
        try
        {
            if (await LatestMessageIdAsync(key) is not { } latest)
                return;
            try
            {
                await Session.Client.UpdateMessageAsync(latest, newTopic, newStreamId, "change_all", ct: Session.Token);
            }
            catch (ZulipApiException e) when (e.Code == "MOVE_MESSAGES_TIME_LIMIT_EXCEEDED" && e.Body is { } body)
            {
                var allowed = IntOf(body, "total_messages_allowed_to_move");
                var total = IntOf(body, "total_messages_in_topic");
                if (allowed == 0 || !body.TryGetProperty("first_message_id_allowed_to_move", out var first)
                                 || first.ValueKind != JsonValueKind.Number)
                {
                    host.ShowToast("所有消息都已超出可移动的时间限制");
                    return;
                }
                if (Session.IsDisposed
                    || !await host.ConfirmAsync($"只有 {allowed}/{total} 条消息可以{action}（更早的消息已超出时间限制），是否{action}这 {allowed} 条？"))
                    return;
                await Session.Client.UpdateMessageAsync(first.GetInt64(), newTopic, newStreamId, "change_later", ct: Session.Token);
            }
        }
        catch (Exception ex) when (!Session.IsDisposed)
        {
            Log.Warn($"话题{action}失败：{key.ToStorageString()}", ex);
            host.ShowToast(FailureText(action, ex));
        }
    }

    // 话题列表可能未加载或已过期，直接向服务器查询最新一条消息
    private async Task<long?> LatestMessageIdAsync(TopicKey key)
    {
        var narrow = Narrow.For(key, Session.SelfId, Session.Conversations.GetTopicName(key));
        var result = await Session.Client.GetMessagesAsync(new MessageQuery(narrow, "newest", 1, 0), Session.Token);
        return result.Messages.Count > 0 ? result.Messages[^1].Id : null;
    }

    private static int IntOf(JsonElement body, string name) =>
        body.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;

    private static string FailureText(string action, Exception ex) =>
        ex is ZulipApiException { Message.Length: > 0 } api ? $"{action}失败：{api.Message}" : $"{action}失败";
}
