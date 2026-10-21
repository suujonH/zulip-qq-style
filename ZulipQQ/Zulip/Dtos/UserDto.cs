using System.Collections.Generic;
using System.Text.Json;

namespace ZulipQQ.Zulip.Dtos;

public sealed record UserDto
{
    public long UserId { get; init; }
    public string Email { get; init; } = "";

    /// <summary>真实邮箱，只在有权查看时出现。</summary>
    public string? DeliveryEmail { get; init; }

    public string FullName { get; init; } = "";
    public string? AvatarUrl { get; init; }
    public bool IsBot { get; init; }
    public int? BotType { get; init; }
    public long? BotOwnerId { get; init; }

    /// <summary>100 所有者 / 200 管理员 / 300 版主 / 400 成员 / 600 访客。</summary>
    public int Role { get; init; } = 400;

    /// <summary><c>realm_users</c> 中没有该字段，默认为 true；<c>realm_non_active_users</c> 中为 false。</summary>
    public bool IsActive { get; init; } = true;

    public string Timezone { get; init; } = "";
    public string? DateJoined { get; init; }
    public Dictionary<string, ProfileValueDto>? ProfileData { get; init; }
}

public sealed record ProfileValueDto
{
    public JsonElement Value { get; init; }
    public string? RenderedValue { get; init; }
}

public sealed record UserGroupDto
{
    public long Id { get; init; }
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public List<long> Members { get; init; } = [];
    public List<long> DirectSubgroupIds { get; init; } = [];
    public bool IsSystemGroup { get; init; }
    public bool Deactivated { get; init; }
}

public sealed record PresenceDto
{
    public long ActiveTimestamp { get; init; }
    public long IdleTimestamp { get; init; }
}

public sealed record UserStatusDto
{
    public string? StatusText { get; init; }
    public string? EmojiName { get; init; }
    public string? EmojiCode { get; init; }
    public string? ReactionType { get; init; }
}

public sealed record MutedUserDto
{
    public long Id { get; init; }
    public long Timestamp { get; init; }
}

public sealed record CustomProfileFieldDto
{
    public long Id { get; init; }
    public int Type { get; init; }
    public string Name { get; init; } = "";
    public string Hint { get; init; } = "";
    public int Order { get; init; }
    public bool DisplayInProfileSummary { get; init; }
    public JsonElement FieldData { get; init; }
}

public sealed record RealmEmojiDto
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string SourceUrl { get; init; } = "";
    public string? StillUrl { get; init; }
    public bool Deactivated { get; init; }
}

public sealed record PresenceUpdateResult
{
    public Dictionary<long, PresenceDto>? Presences { get; init; }
    public long? PresenceLastUpdateId { get; init; }
    public double? ServerTimestamp { get; init; }
}

public sealed record ApiKeyResultDto
{
    public string ApiKey { get; init; } = "";
    public string Email { get; init; } = "";
    public long UserId { get; init; }
}

public sealed record ServerSettingsDto
{
    public int ZulipFeatureLevel { get; init; }
    public string ZulipVersion { get; init; } = "";
    public string RealmName { get; init; } = "";
    public string? RealmIcon { get; init; }
    public string RealmUrl { get; init; } = "";
    public bool EmailAuthEnabled { get; init; }
    public bool RequireEmailFormatUsernames { get; init; }
}
