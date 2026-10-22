using System;
using System.Collections.Generic;
using System.Text.Json;
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

    /// <summary>空话题（<c>""</c>）的显示名。</summary>
    public string EmptyTopicDisplayName { get; set; } = "";

    public string? ServerEmojiDataUrl { get; set; }
    public bool AllowMessageEditing { get; set; }

    /// <summary>编辑时限（秒）；null 表示不限。</summary>
    public int? EditLimitSeconds { get; set; }

    public bool EnableReadReceipts { get; set; }
    public string EditHistoryVisibilityPolicy { get; set; } = "all";

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
        EmptyTopicDisplayName = reg.RealmEmptyTopicDisplayName,
        ServerEmojiDataUrl = reg.ServerEmojiDataUrl,
        AllowMessageEditing = reg.RealmAllowMessageEditing,
        EditLimitSeconds = reg.RealmMessageContentEditLimitSeconds,
        EnableReadReceipts = reg.RealmEnableReadReceipts,
        EditHistoryVisibilityPolicy = reg.RealmMessageEditHistoryVisibilityPolicy,
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
            case "message_content_edit_limit_seconds":
                EditLimitSeconds = value.ValueKind == JsonValueKind.Number ? value.GetInt32() : null;
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
        }
    }
}
