using System;
using System.Collections.Generic;
using System.Text.Json;
using ZulipQQ.Models;
using ZulipQQ.Zulip.Dtos;

namespace ZulipQQ.Services;

/// <summary>组织信息、服务器限制与时间参数。register 时整体读取，收到 realm 事件时更新对应字段。</summary>
public sealed class RealmInfo
{
    public Uri Url { get; init; } = null!;
    public int FeatureLevel { get; set; }
    public string ZulipVersion { get; set; } = "";

    public string Name { get; set; } = "";
    public string? IconUrl { get; set; }

    public int MaxMessageLength { get; set; }
    public int MaxTopicLength { get; set; }
    public int MaxFileUploadSizeMib { get; set; }

    /// <summary>组织的上传存储空间配额（MiB）；null 为不限。</summary>
    public long? UploadQuotaMib { get; set; }

    /// <summary>空话题（<c>""</c>）的显示名。</summary>
    public string EmptyTopicDisplayName { get; set; } = "";

    public string? ServerEmojiDataUrl { get; set; }
    public bool AllowMessageEditing { get; set; }

    /// <summary>服务器是否能发送手机推送；为 false 时手机推送设置不起作用。</summary>
    public bool PushNotificationsEnabled { get; set; } = true;

    // 公告频道：-1（未设置）存为 null
    public long? NewChannelsAnnouncementsChannelId { get; set; }
    public long? SignupAnnouncementsChannelId { get; set; }
    public long? ZulipUpdateAnnouncementsChannelId { get; set; }
    public long? ModerationRequestChannelId { get; set; }

    /// <summary>设置为公告频道的频道（Notification Bot 在其中发公告）。</summary>
    public IReadOnlySet<long> AnnouncementChannelIds
    {
        get
        {
            HashSet<long> ids = [];
            foreach (var id in (long?[])[NewChannelsAnnouncementsChannelId, SignupAnnouncementsChannelId,
                         ZulipUpdateAnnouncementsChannelId, ModerationRequestChannelId])
            {
                if (id is { } v)
                    ids.Add(v);
            }
            return ids;
        }
    }

    /// <summary>编辑时限（秒）；null 表示不限。</summary>
    public int? EditLimitSeconds { get; set; }

    /// <summary>删除自己消息的时限（秒）；null 表示不限。</summary>
    public int? DeleteLimitSeconds { get; set; }

    public bool EnableReadReceipts { get; set; }
    public string EditHistoryVisibilityPolicy { get; set; } = "all";

    /// <summary>组织的话题策略（频道为 inherit 时使用）。</summary>
    public string TopicsPolicy { get; set; } = "allow_empty_topic";

    /// <summary>频道是否允许有名字的话题：话题策略为 empty_topic_only（只有“综合聊天”）时不允许。</summary>
    public bool AllowsNamedTopics(Channel channel) =>
        (channel.TopicsPolicy == "inherit" ? TopicsPolicy : channel.TopicsPolicy) != "empty_topic_only";

    /// <summary>自定义资料字段的定义（custom_profile_fields 事件整体替换）。</summary>
    public IReadOnlyList<CustomProfileFieldDto> ProfileFields { get; set; } = [];

    public int LongpollTimeoutSeconds { get; set; }
    public int TypingStartedWaitMs { get; set; }
    public int TypingStoppedWaitMs { get; set; }
    public int TypingStartedExpiryMs { get; set; }
    public int PresencePingIntervalSeconds { get; set; }
    public int PresenceOfflineThresholdSeconds { get; set; }

    public static RealmInfo FromRegister(Uri url, RegisterResult reg) => new()
    {
        Url = url,
        FeatureLevel = reg.ZulipFeatureLevel,
        ZulipVersion = reg.ZulipVersion,
        Name = reg.RealmName,
        IconUrl = reg.RealmIconUrl,
        MaxMessageLength = reg.MaxMessageLength,
        MaxTopicLength = reg.MaxTopicLength,
        MaxFileUploadSizeMib = reg.MaxFileUploadSizeMib,
        UploadQuotaMib = reg.RealmUploadQuotaMib,
        EmptyTopicDisplayName = reg.RealmEmptyTopicDisplayName,
        ServerEmojiDataUrl = reg.ServerEmojiDataUrl,
        AllowMessageEditing = reg.RealmAllowMessageEditing,
        PushNotificationsEnabled = reg.RealmPushNotificationsEnabled,
        NewChannelsAnnouncementsChannelId = IdOrNull(reg.RealmNewStreamAnnouncementsStreamId),
        SignupAnnouncementsChannelId = IdOrNull(reg.RealmSignupAnnouncementsStreamId),
        ZulipUpdateAnnouncementsChannelId = IdOrNull(reg.RealmZulipUpdateAnnouncementsStreamId),
        ModerationRequestChannelId = IdOrNull(reg.RealmModerationRequestChannelId),
        EditLimitSeconds = reg.RealmMessageContentEditLimitSeconds,
        DeleteLimitSeconds = reg.RealmMessageContentDeleteLimitSeconds,
        EnableReadReceipts = reg.RealmEnableReadReceipts,
        EditHistoryVisibilityPolicy = reg.RealmMessageEditHistoryVisibilityPolicy,
        TopicsPolicy = reg.RealmTopicsPolicy,
        ProfileFields = reg.CustomProfileFields,
        LongpollTimeoutSeconds = reg.EventQueueLongpollTimeoutSeconds,
        TypingStartedWaitMs = reg.ServerTypingStartedWaitPeriodMilliseconds,
        TypingStoppedWaitMs = reg.ServerTypingStoppedWaitPeriodMilliseconds,
        TypingStartedExpiryMs = reg.ServerTypingStartedExpiryPeriodMilliseconds,
        PresencePingIntervalSeconds = reg.ServerPresencePingIntervalSeconds,
        PresenceOfflineThresholdSeconds = reg.ServerPresenceOfflineThresholdSeconds,
    };

    /// <summary>应用 realm 事件中的一个设置（名字不带 <c>realm_</c> 前缀）。不认识的设置忽略。</summary>
    public void Apply(string name, JsonElement value)
    {
        switch (name)
        {
            case "name":
                Name = value.GetString() ?? Name;
                break;
            case "icon_url":
                IconUrl = value.GetString();
                break;
            case "allow_message_editing":
                AllowMessageEditing = value.GetBoolean();
                break;
            case "push_notifications_enabled":
                PushNotificationsEnabled = value.GetBoolean();
                break;
            case "new_stream_announcements_stream_id":
                NewChannelsAnnouncementsChannelId = IdOrNull(value.GetInt64());
                break;
            case "signup_announcements_stream_id":
                SignupAnnouncementsChannelId = IdOrNull(value.GetInt64());
                break;
            case "zulip_update_announcements_stream_id":
                ZulipUpdateAnnouncementsChannelId = IdOrNull(value.GetInt64());
                break;
            case "moderation_request_channel_id":
                ModerationRequestChannelId = IdOrNull(value.GetInt64());
                break;
            case "message_content_edit_limit_seconds":
                EditLimitSeconds = value.ValueKind == JsonValueKind.Number ? value.GetInt32() : null;
                break;
            case "message_content_delete_limit_seconds":
                DeleteLimitSeconds = value.ValueKind == JsonValueKind.Number ? value.GetInt32() : null;
                break;
            case "enable_read_receipts":
                EnableReadReceipts = value.GetBoolean();
                break;
            case "message_edit_history_visibility_policy":
                EditHistoryVisibilityPolicy = value.GetString() ?? EditHistoryVisibilityPolicy;
                break;
            case "empty_topic_display_name":
                EmptyTopicDisplayName = value.GetString() ?? EmptyTopicDisplayName;
                break;
            case "upload_quota_mib":
                UploadQuotaMib = value.ValueKind == JsonValueKind.Number ? value.GetInt64() : null;
                break;
            case "topics_policy":
                TopicsPolicy = value.GetString() ?? TopicsPolicy;
                break;
        }
    }

    private static long? IdOrNull(long id) => id > 0 ? id : null;
}
