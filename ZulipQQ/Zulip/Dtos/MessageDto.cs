using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ZulipQQ.Zulip.Dtos;

/// <summary>一条消息（<c>GET /messages</c>、<c>message</c> 事件）。</summary>
public sealed record MessageDto
{
    public long Id { get; init; }
    public long SenderId { get; init; }
    public string SenderFullName { get; init; } = "";
    public string SenderEmail { get; init; } = "";
    public string? AvatarUrl { get; init; }

    /// <summary>服务器渲染的 HTML（<c>apply_markdown=false</c> 时为 Markdown 原文）。</summary>
    public string Content { get; init; } = "";

    /// <summary>Unix 秒。</summary>
    public long Timestamp { get; init; }

    /// <summary><c>"stream"</c> 或 <c>"private"</c>。</summary>
    public string Type { get; init; } = "";

    public long? StreamId { get; init; }

    /// <summary>话题名；私聊为空字符串。</summary>
    [JsonPropertyName("subject")]
    public string Subject { get; init; } = "";

    /// <summary>频道消息为频道名（字符串）；私聊为 <c>[{id,email,full_name}]</c>，包含自己。</summary>
    public JsonElement DisplayRecipient { get; init; }

    public List<string> Flags { get; init; } = [];
    public List<ReactionDto> Reactions { get; init; } = [];
    public long? LastEditTimestamp { get; init; }
    public bool IsMeMessage { get; init; }
    public JsonElement Submessages { get; init; }

    /// <summary>搜索时服务器返回的带高亮的内容。</summary>
    public string? MatchContent { get; init; }
    public string? MatchSubject { get; init; }

    public bool IsStream => Type == "stream";

    /// <summary>私聊的全部参与者 id（包含自己）；频道消息返回空列表。</summary>
    public IReadOnlyList<long> RecipientUserIds()
    {
        if (DisplayRecipient.ValueKind != JsonValueKind.Array)
            return [];
        List<long> ids = [];
        foreach (var r in DisplayRecipient.EnumerateArray())
        {
            if (r.TryGetProperty("id", out var id))
                ids.Add(id.GetInt64());
        }
        return ids;
    }
}

public sealed record ReactionDto
{
    public string EmojiName { get; init; } = "";
    public string EmojiCode { get; init; } = "";
    public string ReactionType { get; init; } = "";
    public long UserId { get; init; }
}

public sealed record GetMessagesResult
{
    public long Anchor { get; init; }
    public bool FoundNewest { get; init; }
    public bool FoundOldest { get; init; }
    public bool FoundAnchor { get; init; }
    public bool HistoryLimited { get; init; }
    public List<MessageDto> Messages { get; init; } = [];
}

public sealed record FlagsNarrowResult
{
    public int ProcessedCount { get; init; }
    public int UpdatedCount { get; init; }
    public long? FirstProcessedId { get; init; }
    public long? LastProcessedId { get; init; }
    public bool FoundOldest { get; init; }
    public bool FoundNewest { get; init; }
}

public sealed record SendMessageResult
{
    public long Id { get; init; }
}

public sealed record UploadResult
{
    /// <summary>形如 <c>/user_uploads/…</c> 的相对地址。</summary>
    public string Url { get; init; } = "";
    public string? Filename { get; init; }
}

/// <summary>要发送的消息。频道消息设置 <see cref="StreamId"/> 与 <see cref="Topic"/>，私聊设置 <see cref="UserIds"/>。</summary>
public sealed record OutgoingMessage(
    long? StreamId,
    string? Topic,
    IReadOnlyList<long>? UserIds,
    string Content,
    string? QueueId = null,
    string? LocalId = null);

/// <summary>
/// <c>GET /messages</c> 的查询参数。<see cref="Anchor"/> 为 <c>newest</c>、<c>oldest</c>、<c>first_unread</c>、消息 id，
/// 或 <c>date</c>（此时 <see cref="AnchorDate"/> 为 ISO 8601 时间，服务器 feature level ≥ 445）。
/// </summary>
public sealed record MessageQuery(
    IReadOnlyList<NarrowTerm> Narrow,
    string Anchor,
    int NumBefore,
    int NumAfter,
    bool IncludeAnchor = true,
    bool ApplyMarkdown = true,
    string? AnchorDate = null);
