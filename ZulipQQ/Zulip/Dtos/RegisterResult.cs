using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using ZulipQQ.Core;

namespace ZulipQQ.Zulip.Dtos;

/// <summary><c>POST /register</c> 的结果。未列出的字段保留在 <see cref="Extra"/> 中。</summary>
public sealed record RegisterResult
{
    // 队列与版本
    public string QueueId { get; init; } = "";
    public long LastEventId { get; init; }
    public int ZulipFeatureLevel { get; init; }
    public string ZulipVersion { get; init; } = "";
    public int EventQueueLongpollTimeoutSeconds { get; init; } = 90;

    // 自己
    public long UserId { get; init; }
    public string Email { get; init; } = "";
    public string FullName { get; init; } = "";
    public string? AvatarUrl { get; init; }
    public bool IsAdmin { get; init; }

    // 用户
    public List<UserDto> RealmUsers { get; init; } = [];
    public List<UserDto> RealmNonActiveUsers { get; init; } = [];
    public List<UserDto> CrossRealmBots { get; init; } = [];

    // 频道
    public List<SubscriptionDto> Subscriptions { get; init; } = [];
    public List<SubscriptionDto> Unsubscribed { get; init; } = [];
    public List<StreamDto> NeverSubscribed { get; init; } = [];
    public List<StreamDto> Streams { get; init; } = [];

    // 会话与未读
    public List<RecentPrivateConversationDto> RecentPrivateConversations { get; init; } = [];
    public UnreadMsgsDto UnreadMsgs { get; init; } = new();
    public List<UserTopicDto> UserTopics { get; init; } = [];

    // 个人数据
    public List<DraftDto> Drafts { get; init; } = [];
    public List<long> StarredMessages { get; init; } = [];
    public List<MutedUserDto> MutedUsers { get; init; } = [];
    public Dictionary<long, PresenceDto> Presences { get; init; } = [];
    public long PresenceLastUpdateId { get; init; } = -1;
    public Dictionary<long, UserStatusDto> UserStatus { get; init; } = [];
    public UserSettingsDto UserSettings { get; init; } = new();
    public List<string> AlertWords { get; init; } = [];
    public List<ScheduledMessageDto> ScheduledMessages { get; init; } = [];
    public List<SavedSnippetDto> SavedSnippets { get; init; } = [];
    public JsonElement Reminders { get; init; }

    // 组与表情
    public List<UserGroupDto> RealmUserGroups { get; init; } = [];
    public Dictionary<string, RealmEmojiDto> RealmEmoji { get; init; } = [];
    public List<CustomProfileFieldDto> CustomProfileFields { get; init; } = [];
    public string? ServerEmojiDataUrl { get; init; }

    // 限制
    public int MaxMessageLength { get; init; } = 10000;
    public int MaxTopicLength { get; init; } = 60;
    public int MaxFileUploadSizeMib { get; init; } = 25;
    public string RealmEmptyTopicDisplayName { get; init; } = "";
    public JsonElement ServerSupportedPermissionSettings { get; init; }

    // 组织设置
    public string RealmName { get; init; } = "";
    public string? RealmIconUrl { get; init; }
    public bool RealmAllowMessageEditing { get; init; } = true;

    /// <summary>编辑时限（秒）；null 表示不限。</summary>
    public int? RealmMessageContentEditLimitSeconds { get; init; }

    public bool RealmEnableReadReceipts { get; init; }
    public int? RealmMoveMessagesBetweenStreamsLimitSeconds { get; init; }
    public int? RealmMoveMessagesWithinStreamLimitSeconds { get; init; }

    /// <summary><c>all</c>、<c>moves</c> 或 <c>none</c>。</summary>
    public string RealmMessageEditHistoryVisibilityPolicy { get; init; } = "all";
    public string RealmTopicsPolicy { get; init; } = "allow_empty_topic";

    // 时间参数
    public int ServerTypingStartedWaitPeriodMilliseconds { get; init; } = 10000;
    public int ServerTypingStoppedWaitPeriodMilliseconds { get; init; } = 5000;
    public int ServerTypingStartedExpiryPeriodMilliseconds { get; init; } = 45000;
    public int ServerPresencePingIntervalSeconds { get; init; } = 60;
    public int ServerPresenceOfflineThresholdSeconds { get; init; } = 140;

    [JsonExtensionData]
    public Dictionary<string, JsonElement> Extra { get; init; } = [];

    /// <summary>自己的角色，取自 <see cref="RealmUsers"/> 中自己那一行；找不到时为成员（400）。</summary>
    public int SelfRole() => RealmUsers.FirstOrDefault(u => u.UserId == UserId)?.Role ?? 400;

    /// <summary>
    /// 组织级的组设置，键为去掉 <c>realm_</c> 前缀后的名字（如 <c>can_create_groups</c>）。
    /// 设置名以服务器的 <c>server_supported_permission_settings.realm</c> 为准；
    /// 注意部分名字不以 <c>_group</c> 结尾（<c>can_create_groups</c>、<c>can_manage_all_groups</c>）。
    /// </summary>
    public Dictionary<string, GroupSettingValue> RealmGroupSettings()
    {
        Dictionary<string, GroupSettingValue> result = [];
        foreach (var name in PermissionSettingNames("realm"))
        {
            if (Extra.TryGetValue("realm_" + name, out var value)
                && value.Deserialize<GroupSettingValue>(ZulipJson.Options) is { } setting)
                result[name] = setting;
        }
        return result;
    }

    /// <summary>某一范围（<c>realm</c>、<c>stream</c>、<c>group</c>）下服务器支持的权限设置名。</summary>
    public IEnumerable<string> PermissionSettingNames(string scope)
    {
        if (ServerSupportedPermissionSettings.ValueKind != JsonValueKind.Object
            || !ServerSupportedPermissionSettings.TryGetProperty(scope, out var settings)
            || settings.ValueKind != JsonValueKind.Object)
            return [];
        return settings.EnumerateObject().Select(p => p.Name).ToList();
    }

    /// <summary>每个 (范围, 设置名) 可以设置的组的限制。</summary>
    public Dictionary<(string Scope, string Name), PermissionSettingInfo> PermissionSettingInfos()
    {
        Dictionary<(string, string), PermissionSettingInfo> result = [];
        if (ServerSupportedPermissionSettings.ValueKind != JsonValueKind.Object)
            return result;
        foreach (var scope in ServerSupportedPermissionSettings.EnumerateObject())
        {
            if (scope.Value.ValueKind != JsonValueKind.Object)
                continue;
            foreach (var setting in scope.Value.EnumerateObject())
            {
                var v = setting.Value;
                List<string> allowed = v.TryGetProperty("allowed_system_groups", out var groups) && groups.ValueKind == JsonValueKind.Array
                    ? groups.EnumerateArray().Select(g => g.GetString() ?? "").ToList()
                    : [];
                result[(scope.Name, setting.Name)] = new PermissionSettingInfo(
                    !v.TryGetProperty("allow_nobody_group", out var nobody) || nobody.ValueKind == JsonValueKind.True,
                    !v.TryGetProperty("allow_everyone_group", out var everyone) || everyone.ValueKind == JsonValueKind.True,
                    allowed);
            }
        }
        return result;
    }

    /// <summary>每个 (范围, 设置名) 是否允许设为“所有人”组（含访客）。</summary>
    public Dictionary<(string Scope, string Name), bool> AllowEveryoneGroup()
    {
        Dictionary<(string, string), bool> result = [];
        if (ServerSupportedPermissionSettings.ValueKind != JsonValueKind.Object)
            return result;
        foreach (var scope in ServerSupportedPermissionSettings.EnumerateObject())
        {
            if (scope.Value.ValueKind != JsonValueKind.Object)
                continue;
            foreach (var setting in scope.Value.EnumerateObject())
            {
                if (setting.Value.TryGetProperty("allow_everyone_group", out var allow))
                    result[(scope.Name, setting.Name)] = allow.ValueKind == JsonValueKind.True;
            }
        }
        return result;
    }
}
