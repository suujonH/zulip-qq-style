using System.Text.Json;
using ZulipQQ.Core;
using ZulipQQ.Zulip;
using ZulipQQ.Zulip.Dtos;

namespace ZulipQQ.Models;

/// <summary>
/// 频道（QQ 的“群分组”）。已订阅、已退订、从未订阅的频道都用它表示，以 <see cref="IsSubscribed"/> 区分；
/// 个人设置（颜色、静音、置顶、通知）只对订阅过的频道有意义。收到 subscription、stream 事件时就地更新。
/// </summary>
public sealed class Channel : IChannelPermissions
{
    public long StreamId { get; init; }
    public bool IsSubscribed { get; set; }

    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string RenderedDescription { get; set; } = "";
    public bool InviteOnly { get; set; }
    public bool IsWebPublic { get; set; }
    public bool IsArchived { get; set; }
    public bool HistoryPublicToSubscribers { get; set; } = true;
    public int SubscriberCount { get; set; }
    public long? CreatorId { get; set; }
    public long DateCreated { get; set; }
    public long? FolderId { get; set; }
    public string TopicsPolicy { get; set; } = "inherit";

    // 个人设置
    public string Color { get; set; } = "";
    public bool PinToTop { get; set; }
    public bool IsMuted { get; set; }
    public bool? DesktopNotifications { get; set; }
    public bool? AudibleNotifications { get; set; }
    public bool? PushNotifications { get; set; }
    public bool? EmailNotifications { get; set; }
    public bool? WildcardMentionsNotify { get; set; }

    public GroupSettingValue? CanAdministerChannelGroup { get; set; }
    public GroupSettingValue? CanAddSubscribersGroup { get; set; }
    public GroupSettingValue? CanRemoveSubscribersGroup { get; set; }
    public GroupSettingValue? CanSendMessageGroup { get; set; }
    public GroupSettingValue? CanMoveMessagesOutOfChannelGroup { get; set; }
    public GroupSettingValue? CanMoveMessagesWithinChannelGroup { get; set; }
    public GroupSettingValue? CanResolveTopicsGroup { get; set; }
    public GroupSettingValue? CanDeleteAnyMessageGroup { get; set; }
    public GroupSettingValue? CanDeleteOwnMessageGroup { get; set; }
    public GroupSettingValue? CanCreateTopicGroup { get; set; }
    public GroupSettingValue? CanSubscribeGroup { get; set; }

    public static Channel FromDto(StreamDto dto, bool subscribed)
    {
        var channel = new Channel { StreamId = dto.StreamId };
        channel.Apply(dto);
        channel.IsSubscribed = subscribed;
        return channel;
    }

    /// <summary>用完整的频道数据覆盖；传入 <see cref="SubscriptionDto"/> 时同时覆盖个人设置。</summary>
    public void Apply(StreamDto dto)
    {
        Name = dto.Name;
        Description = dto.Description;
        RenderedDescription = dto.RenderedDescription;
        InviteOnly = dto.InviteOnly;
        IsWebPublic = dto.IsWebPublic;
        IsArchived = dto.IsArchived;
        HistoryPublicToSubscribers = dto.HistoryPublicToSubscribers;
        SubscriberCount = dto.SubscriberCount;
        CreatorId = dto.CreatorId;
        DateCreated = dto.DateCreated;
        FolderId = dto.FolderId;
        TopicsPolicy = dto.TopicsPolicy;
        CanAdministerChannelGroup = dto.CanAdministerChannelGroup;
        CanAddSubscribersGroup = dto.CanAddSubscribersGroup;
        CanRemoveSubscribersGroup = dto.CanRemoveSubscribersGroup;
        CanSendMessageGroup = dto.CanSendMessageGroup;
        CanMoveMessagesOutOfChannelGroup = dto.CanMoveMessagesOutOfChannelGroup;
        CanMoveMessagesWithinChannelGroup = dto.CanMoveMessagesWithinChannelGroup;
        CanResolveTopicsGroup = dto.CanResolveTopicsGroup;
        CanDeleteAnyMessageGroup = dto.CanDeleteAnyMessageGroup;
        CanDeleteOwnMessageGroup = dto.CanDeleteOwnMessageGroup;
        CanCreateTopicGroup = dto.CanCreateTopicGroup;
        CanSubscribeGroup = dto.CanSubscribeGroup;

        if (dto is SubscriptionDto sub)
        {
            Color = sub.Color;
            PinToTop = sub.PinToTop;
            IsMuted = sub.IsMuted;
            DesktopNotifications = sub.DesktopNotifications;
            AudibleNotifications = sub.AudibleNotifications;
            PushNotifications = sub.PushNotifications;
            EmailNotifications = sub.EmailNotifications;
            WildcardMentionsNotify = sub.WildcardMentionsNotify;
        }
    }

    /// <summary>应用 stream update 事件（<c>property</c> + <c>value</c>，部分属性附带额外字段）。未知属性忽略。</summary>
    public void ApplyStreamUpdate(JsonElement ev)
    {
        var value = ev.GetProperty("value");
        switch (ev.GetProperty("property").GetString())
        {
            case "name":
                Name = value.GetString() ?? Name;
                break;
            case "description":
                Description = value.GetString() ?? "";
                if (ev.TryGetProperty("rendered_description", out var rendered))
                    RenderedDescription = rendered.GetString() ?? "";
                break;
            case "invite_only":
                InviteOnly = value.GetBoolean();
                if (ev.TryGetProperty("history_public_to_subscribers", out var history))
                    HistoryPublicToSubscribers = history.GetBoolean();
                if (ev.TryGetProperty("is_web_public", out var webPublic))
                    IsWebPublic = webPublic.GetBoolean();
                break;
            case "is_web_public":
                IsWebPublic = value.GetBoolean();
                break;
            case "history_public_to_subscribers":
                HistoryPublicToSubscribers = value.GetBoolean();
                break;
            case "is_archived":
                IsArchived = value.GetBoolean();
                break;
            case "folder_id":
                FolderId = value.ValueKind == JsonValueKind.Number ? value.GetInt64() : null;
                break;
            case "topics_policy":
                TopicsPolicy = value.GetString() ?? TopicsPolicy;
                break;
            case "can_administer_channel_group":
                CanAdministerChannelGroup = Group(value);
                break;
            case "can_add_subscribers_group":
                CanAddSubscribersGroup = Group(value);
                break;
            case "can_remove_subscribers_group":
                CanRemoveSubscribersGroup = Group(value);
                break;
            case "can_send_message_group":
                CanSendMessageGroup = Group(value);
                break;
            case "can_move_messages_out_of_channel_group":
                CanMoveMessagesOutOfChannelGroup = Group(value);
                break;
            case "can_move_messages_within_channel_group":
                CanMoveMessagesWithinChannelGroup = Group(value);
                break;
            case "can_resolve_topics_group":
                CanResolveTopicsGroup = Group(value);
                break;
            case "can_delete_any_message_group":
                CanDeleteAnyMessageGroup = Group(value);
                break;
            case "can_delete_own_message_group":
                CanDeleteOwnMessageGroup = Group(value);
                break;
            case "can_create_topic_group":
                CanCreateTopicGroup = Group(value);
                break;
            case "can_subscribe_group":
                CanSubscribeGroup = Group(value);
                break;
        }
    }

    /// <summary>应用 subscription update 事件中的个人设置。未知属性忽略。</summary>
    public void ApplySubscriptionProperty(string property, JsonElement value)
    {
        switch (property)
        {
            case "color":
                Color = value.GetString() ?? Color;
                break;
            case "is_muted":
                IsMuted = value.GetBoolean();
                break;
            // 旧名字，含义与 is_muted 相反
            case "in_home_view":
                IsMuted = !value.GetBoolean();
                break;
            case "pin_to_top":
                PinToTop = value.GetBoolean();
                break;
            case "desktop_notifications":
                DesktopNotifications = NullableBool(value);
                break;
            case "audible_notifications":
                AudibleNotifications = NullableBool(value);
                break;
            case "push_notifications":
                PushNotifications = NullableBool(value);
                break;
            case "email_notifications":
                EmailNotifications = NullableBool(value);
                break;
            case "wildcard_mentions_notify":
                WildcardMentionsNotify = NullableBool(value);
                break;
        }
    }

    private static GroupSettingValue? Group(JsonElement value) =>
        value.ValueKind == JsonValueKind.Null ? null : value.Deserialize<GroupSettingValue>(ZulipJson.Options);

    private static bool? NullableBool(JsonElement value) =>
        value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;
}
