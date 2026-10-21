using System.Collections.Generic;
using ZulipQQ.Core;

namespace ZulipQQ.Zulip.Dtos;

/// <summary>频道（<c>streams</c>、<c>never_subscribed</c>）。各 <c>can_*_group</c> 缺失时为 null。</summary>
public record StreamDto : IChannelPermissions
{
    public long StreamId { get; init; }
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public string RenderedDescription { get; init; } = "";
    public bool InviteOnly { get; init; }
    public bool IsWebPublic { get; init; }
    public bool IsArchived { get; init; }
    public bool HistoryPublicToSubscribers { get; init; } = true;
    public int SubscriberCount { get; init; }
    public long? CreatorId { get; init; }
    public long DateCreated { get; init; }
    public long? FolderId { get; init; }

    public GroupSettingValue? CanAdministerChannelGroup { get; init; }
    public GroupSettingValue? CanAddSubscribersGroup { get; init; }
    public GroupSettingValue? CanRemoveSubscribersGroup { get; init; }
    public GroupSettingValue? CanSendMessageGroup { get; init; }
    public GroupSettingValue? CanMoveMessagesOutOfChannelGroup { get; init; }
    public GroupSettingValue? CanMoveMessagesWithinChannelGroup { get; init; }
    public GroupSettingValue? CanResolveTopicsGroup { get; init; }
    public GroupSettingValue? CanDeleteAnyMessageGroup { get; init; }
    public GroupSettingValue? CanDeleteOwnMessageGroup { get; init; }
    public GroupSettingValue? CanCreateTopicGroup { get; init; }
    public GroupSettingValue? CanSubscribeGroup { get; init; }
}

/// <summary>已订阅或曾订阅的频道（<c>subscriptions</c>、<c>unsubscribed</c>），包含个人设置。</summary>
public sealed record SubscriptionDto : StreamDto
{
    public string Color { get; init; } = "";
    public bool PinToTop { get; init; }
    public bool IsMuted { get; init; }
    public bool? DesktopNotifications { get; init; }
    public bool? AudibleNotifications { get; init; }
    public bool? PushNotifications { get; init; }
    public bool? EmailNotifications { get; init; }
    public bool? WildcardMentionsNotify { get; init; }
}

public sealed record UserTopicDto
{
    public long StreamId { get; init; }
    public string TopicName { get; init; } = "";

    /// <summary>0 无 / 1 静音 / 2 取消静音 / 3 关注。</summary>
    public int VisibilityPolicy { get; init; }

    public long LastUpdated { get; init; }
}

/// <summary><c>GET /users/me/{stream_id}/topics</c> 的一项。</summary>
public sealed record TopicDto
{
    public string Name { get; init; } = "";
    public long MaxId { get; init; }
}

public sealed record RecentPrivateConversationDto
{
    /// <summary>对方的 id（不含自己）；与自己的私聊为空数组。</summary>
    public List<long> UserIds { get; init; } = [];
    public long MaxMessageId { get; init; }
}

/// <summary><c>POST /users/me/subscriptions/properties</c> 的一项。</summary>
public sealed record SubscriptionPropertyChange(long StreamId, string Property, object Value);
