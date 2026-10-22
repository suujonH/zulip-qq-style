using System.Collections.Generic;
using ZulipQQ.Core;
using ZulipQQ.Zulip.Dtos;

namespace ZulipQQ.Models;

/// <summary>消息的发送状态。只有本地回显（尚未收到服务器确认）的消息才不是 <see cref="Sent"/>。</summary>
public enum MessageSendState
{
    Sent,
    Pending,
    Failed,
}

/// <summary>
/// 一条消息。服务器上的消息 <see cref="Id"/> 为正数；本地回显的消息 <see cref="Id"/> 为负数，
/// 收到服务器的 message 事件后被真正的消息替换。只在 UI 线程上读写。
/// </summary>
public sealed class ChatMessage
{
    public long Id { get; init; }
    public ConversationKey Key { get; set; } = null!;
    public long SenderId { get; init; }
    public string SenderName { get; set; } = "";
    public string SenderEmail { get; set; } = "";
    public string? AvatarUrl { get; set; }

    /// <summary>服务器渲染的 HTML。</summary>
    public string ContentHtml { get; set; } = "";

    /// <summary>Unix 秒。</summary>
    public long Timestamp { get; init; }

    public HashSet<string> Flags { get; set; } = [];
    public List<ReactionDto> Reactions { get; set; } = [];
    public long? LastEditTimestamp { get; set; }
    public bool IsMeMessage { get; set; }

    /// <summary>话题名（原始大小写）；私聊为空字符串。</summary>
    public string Topic { get; set; } = "";

    public long? StreamId { get; set; }
    /// <summary>投票、待办等小部件的子消息，按 id 升序；submessage 事件追加。</summary>
    public List<SubmessageDto> Submessages { get; set; } = [];

    /// <summary>本地回显的标识，与发送时的 <c>local_id</c> 相同；服务器消息为 null。</summary>
    public string? LocalId { get; init; }

    public MessageSendState SendState { get; set; }

    /// <summary>被插件的 filter_incoming 拦截，不显示。</summary>
    public bool IsHiddenByPlugin { get; set; }

    public bool IsRead => Flags.Contains("read");

    public bool IsStarred => Flags.Contains("starred");

    public static ChatMessage FromDto(MessageDto dto, ConversationKey key) => new()
    {
        Id = dto.Id,
        Key = key,
        SenderId = dto.SenderId,
        SenderName = dto.SenderFullName,
        SenderEmail = dto.SenderEmail,
        AvatarUrl = dto.AvatarUrl,
        ContentHtml = dto.Content,
        Timestamp = dto.Timestamp,
        Flags = [.. dto.Flags],
        Reactions = [.. dto.Reactions],
        LastEditTimestamp = dto.LastEditTimestamp,
        IsMeMessage = dto.IsMeMessage,
        Topic = dto.Subject,
        StreamId = dto.StreamId,
        Submessages = [.. dto.Submessages],
    };
}
