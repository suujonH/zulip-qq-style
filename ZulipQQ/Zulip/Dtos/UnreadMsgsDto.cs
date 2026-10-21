using System.Collections.Generic;

namespace ZulipQQ.Zulip.Dtos;

/// <summary>register 返回的未读摘要。</summary>
public sealed record UnreadMsgsDto
{
    public int Count { get; init; }
    public List<UnreadPm> Pms { get; init; } = [];
    public List<UnreadStream> Streams { get; init; } = [];
    public List<UnreadHuddle> Huddles { get; init; } = [];
    public List<long> Mentions { get; init; } = [];
    public bool OldUnreadsMissing { get; init; }

    public sealed record UnreadPm
    {
        /// <summary>对方 id；与自己的私聊时为自己的 id。</summary>
        public long OtherUserId { get; init; }
        public List<long> UnreadMessageIds { get; init; } = [];
    }

    public sealed record UnreadStream
    {
        public long StreamId { get; init; }
        public string Topic { get; init; } = "";
        public List<long> UnreadMessageIds { get; init; } = [];
    }

    public sealed record UnreadHuddle
    {
        /// <summary>逗号分隔的全部参与者 id（包含自己）。</summary>
        public string UserIdsString { get; init; } = "";
        public List<long> UnreadMessageIds { get; init; } = [];
    }
}

/// <summary>草稿。<see cref="Type"/> 为 <c>stream</c>、<c>private</c> 或空字符串（未指定收件人）。</summary>
public sealed record DraftDto
{
    public long? Id { get; init; }
    public string Type { get; init; } = "";

    /// <summary>频道草稿为 <c>[stream_id]</c>；私聊为对方 id 列表。</summary>
    public List<long> To { get; init; } = [];

    public string Topic { get; init; } = "";
    public string Content { get; init; } = "";
    public long? Timestamp { get; init; }
}

/// <summary>Zulip 用户设置中本客户端用到的字段。</summary>
public sealed record UserSettingsDto
{
    public int ColorScheme { get; init; } = 1;
    public bool EnterSends { get; init; }
    public bool TwentyFourHourTime { get; init; }
    public bool EnableDesktopNotifications { get; init; } = true;
    public bool EnableSounds { get; init; } = true;
    public string NotificationSound { get; init; } = "";
    public bool PmContentInDesktopNotifications { get; init; } = true;
    public bool EnableStreamDesktopNotifications { get; init; }
    public bool EnableStreamAudibleNotifications { get; init; }
    public bool EnableFollowedTopicDesktopNotifications { get; init; } = true;
    public bool EnableFollowedTopicAudibleNotifications { get; init; } = true;
    public bool WildcardMentionsNotify { get; init; } = true;
    public bool EnableDraftsSynchronization { get; init; } = true;
    public bool SendReadReceipts { get; init; } = true;
    public bool SendPrivateTypingNotifications { get; init; } = true;
    public bool SendStreamTypingNotifications { get; init; } = true;
    public bool PresenceEnabled { get; init; } = true;
    public int EmailAddressVisibility { get; init; } = 1;
    public string Emojiset { get; init; } = "google";
    public bool DisplayEmojiReactionUsers { get; init; } = true;
    public int AutomaticallyFollowTopicsPolicy { get; init; }
    public int AutomaticallyUnmuteTopicsInMutedStreamsPolicy { get; init; }
    public bool StarredMessageCounts { get; init; }
}
