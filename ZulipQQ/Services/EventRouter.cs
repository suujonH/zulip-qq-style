using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using ZulipQQ.Core;
using ZulipQQ.Models;
using ZulipQQ.Plugins;
using ZulipQQ.Zulip;
using ZulipQQ.Zulip.Dtos;

namespace ZulipQQ.Services;

/// <summary>把事件队列中的事件按 type / op 分发给各 Store。必须按顺序逐个应用。</summary>
public static class EventRouter
{
    public static Task ApplyAsync(ZulipSession s, JsonElement ev)
    {
        switch (ev.GetProperty("type").GetString())
        {
            case "heartbeat":
                break;
            case "restart":
                if (ev.TryGetProperty("zulip_feature_level", out var level))
                    s.Realm.FeatureLevel = level.GetInt32();
                if (ev.TryGetProperty("zulip_version", out var version))
                    s.Realm.ZulipVersion = version.GetString() ?? s.Realm.ZulipVersion;
                break;
            case "message":
                return OnMessageAsync(s, ev);
            case "update_message":
                OnUpdateMessage(s, ev);
                break;
            case "delete_message":
                OnDeleteMessage(s, ev);
                break;
            case "update_message_flags":
                s.Messages.OnFlagEvent(ev);
                if (ev.GetProperty("flag").GetString() == "read")
                    s.Unread.OnReadFlagEvent(ev);
                break;
            case "reaction":
                s.Messages.OnReaction(ev);
                break;
            case "subscription":
                if (s.Channels.OnSubscriptionEvent(ev) is { } mutedStream)
                    s.Conversations.RecomputeLevels(mutedStream);
                break;
            case "stream":
                s.Channels.OnStreamEvent(ev);
                break;
            case "realm_user":
                if (s.Users.OnRealmUserEvent(ev))
                    s.OnSelfRoleChanged();
                break;
            case "user_group":
                s.OnUserGroupEvent(ev);
                break;
            case "realm":
                s.OnRealmEvent(ev);
                break;
            case "user_topic":
                var topic = ZulipJson.Deserialize<UserTopicDto>(ev);
                s.Channels.OnUserTopic(topic);
                s.Conversations.RecomputeLevels(topic.StreamId);
                break;
            case "user_settings":
                OnUserSettings(s, ev);
                break;
            case "drafts":
                s.Drafts.OnDraftsEvent(ev);
                break;
            case "presence":
                s.Users.OnPresenceEvent(ev);
                break;
            case "user_status":
                s.Users.OnStatusEvent(ev);
                break;
            case "muted_users":
                s.Users.OnMutedUsersEvent(ev);
                break;
            case "realm_emoji":
                s.Emoji.LoadRealm(ZulipJson.Deserialize<Dictionary<string, RealmEmojiDto>>(ev.GetProperty("realm_emoji")));
                break;
            case "custom_profile_fields":
                s.Realm.ProfileFields = ZulipJson.Deserialize<List<CustomProfileFieldDto>>(ev.GetProperty("fields"));
                break;
            case "alert_words":
                s.Settings.OnAlertWords(ZulipJson.Deserialize<List<string>>(ev.GetProperty("alert_words")));
                break;
            // 输入状态、定时消息等由后续阶段处理
            default:
                Log.Debug($"忽略事件：{ev.GetProperty("type").GetString()}");
                break;
        }
        return Task.CompletedTask;
    }

    private static async Task OnMessageAsync(ZulipSession s, JsonElement ev)
    {
        var dto = ZulipJson.Deserialize<MessageDto>(ev.GetProperty("message"));
        var flags = ev.TryGetProperty("flags", out var f)
            ? f.EnumerateArray().Select(e => e.GetString() ?? "").ToList()
            : [];
        var localId = ev.TryGetProperty("local_message_id", out var l) ? l.GetString() : null;

        var msg = ChatMessage.FromDto(dto, ConversationKey.FromMessage(dto, s.SelfId));
        msg.Flags = [.. flags];

        // 插件过滤（自己发送的消息不经过）：被拦截的消息标记已读，不进入任何 Store
        if (msg.SenderId != s.SelfId && await PluginHost.Instance.FilterIncomingAsync(msg, s))
        {
            if (s.IsDisposed)
                return;
            s.Messages.HideByPlugin(msg.Id);
            // 不等待服务器：事件必须按顺序处理，不应被网络请求拖慢
            if (!flags.Contains("read"))
                s.Forget(s.Chat.MarkReadAsync([msg.Id]), "标记被插件拦截的消息已读");
            return;
        }
        if (s.IsDisposed)
            return;

        s.Messages.OnNewMessage(msg, localId);
        s.Channels.OnNewMessage(msg);
        s.Unread.OnNewMessage(msg, flags);
        s.Conversations.OnNewMessage(msg);
        s.Notifications.OnNewMessage(msg, flags);
        PluginHost.Instance.DispatchMessage(msg, s);
        s.Registry.OnNewMessage(msg);
    }

    private static void OnUpdateMessage(ZulipSession s, JsonElement ev)
    {
        s.Messages.OnUpdate(ev);

        if (MoveInfo.From(ev) is { } move)
        {
            s.Unread.OnMoved(move.MessageIds, move.NewKey);
            s.Channels.InvalidateTopics(move.OldKey.StreamId);
            if (move.NewKey.StreamId != move.OldKey.StreamId)
                s.Channels.InvalidateTopics(move.NewKey.StreamId);
            s.Conversations.OnMessagesMoved(move);
        }

        if (ev.TryGetProperty("message_id", out var id) && ev.TryGetProperty("rendered_content", out var rendered))
        {
            // 未读消息的提及 / 文件提示随编辑更新；消息不在缓存中也能用事件里的数据计算
            if (ev.TryGetProperty("flags", out var flags) && flags.ValueKind == JsonValueKind.Array)
                s.Unread.OnEdited(id.GetInt64(), [.. flags.EnumerateArray().Select(f => f.GetString() ?? "")],
                    rendered.GetString() ?? "");
            s.Conversations.OnEdited(id.GetInt64(), rendered.GetString() ?? "");
        }
    }

    private static void OnDeleteMessage(ZulipSession s, JsonElement ev)
    {
        List<long> ids = ev.TryGetProperty("message_ids", out var many)
            ? many.EnumerateArray().Select(e => e.GetInt64()).ToList()
            : [ev.GetProperty("message_id").GetInt64()];
        s.Messages.OnDeleted(ids);
        s.Unread.OnDeleted(ids);
        s.Conversations.OnDeleted(ids);
        // 频道消息被删除（含删除话题）后，话题列表可能变化，重新加载
        if (ev.TryGetProperty("stream_id", out var streamId))
            s.Channels.InvalidateTopics(streamId.GetInt64());
    }

    private static void OnUserSettings(ZulipSession s, JsonElement ev)
    {
        if (ev.GetProperty("op").GetString() != "update")
            return;
        var property = ev.GetProperty("property").GetString() ?? "";
        var value = ev.GetProperty("value");
        s.Settings.OnZulipSettingChanged(property, value);
        if (property == "enable_drafts_synchronization")
            s.Forget(s.Drafts.OnSyncSettingChangedAsync(value.GetBoolean()), "切换草稿同步");
    }
}
