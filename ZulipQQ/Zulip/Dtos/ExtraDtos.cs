using System.Text.Json;

namespace ZulipQQ.Zulip.Dtos;

/// <summary>定时消息（<c>GET /scheduled_messages</c>、register 的 <c>scheduled_messages</c>）。</summary>
public sealed record ScheduledMessageDto
{
    public long ScheduledMessageId { get; init; }

    /// <summary><c>"stream"</c> 或 <c>"private"</c>。</summary>
    public string Type { get; init; } = "";

    /// <summary>频道消息为频道 id（数字）；私聊为收件人 id 数组。</summary>
    public JsonElement To { get; init; }

    public string Topic { get; init; } = "";
    public string Content { get; init; } = "";
    public string RenderedContent { get; init; } = "";

    /// <summary>Unix 秒。</summary>
    public long ScheduledDeliveryTimestamp { get; init; }

    /// <summary>到时间后发送失败。</summary>
    public bool Failed { get; init; }
}

/// <summary>常用语（saved snippet）。</summary>
public sealed record SavedSnippetDto
{
    public long Id { get; init; }
    public string Title { get; init; } = "";
    public string Content { get; init; } = "";
    public long DateCreated { get; init; }
}

/// <summary>
/// 消息编辑历史中的一个版本（<c>GET /messages/{id}/history</c>）。第一项是原始版本，
/// 之后每一项是一次编辑：改了内容时有 <see cref="PrevContent"/> 与 <see cref="ContentHtmlDiff"/>，
/// 改了话题或频道时有 <see cref="PrevTopic"/> / <see cref="PrevStream"/>。
/// </summary>
public sealed record MessageHistoryDto
{
    public string Topic { get; init; } = "";
    public string? PrevTopic { get; init; }
    public long? Stream { get; init; }
    public long? PrevStream { get; init; }
    public string Content { get; init; } = "";
    public string RenderedContent { get; init; } = "";
    public string? PrevContent { get; init; }
    public string? PrevRenderedContent { get; init; }

    /// <summary>与上一版本的差异：新增的文字在 <c>span.highlight_text_inserted</c> 中，删除的在 <c>span.highlight_text_deleted</c> 中。</summary>
    public string? ContentHtmlDiff { get; init; }

    /// <summary>编辑者；系统自动修改时为 null。</summary>
    public long? UserId { get; init; }

    public long Timestamp { get; init; }
}

/// <summary>
/// 消息的子消息（投票、待办等小部件）。第一条的 <see cref="Content"/> 是小部件定义，
/// 之后每一条是一次操作（投票、添加选项、勾选任务等）。<see cref="Content"/> 是 JSON 字符串。
/// </summary>
public sealed record SubmessageDto
{
    public long Id { get; init; }
    public long MessageId { get; init; }
    public long SenderId { get; init; }
    public string MsgType { get; init; } = "";
    public string Content { get; init; } = "";
}
