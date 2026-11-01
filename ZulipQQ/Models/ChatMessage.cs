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

    /// <summary>
    /// 本地保存的编辑前的各个版本的 HTML，从最早（原始内容）到最近；<see cref="ContentHtml"/> 是最新版本。
    /// 组织不允许查看编辑历史时只有本机收到编辑事件时保存的版本；没有保存过时为空。
    /// </summary>
    public List<string> PreviousContents { get; set; } = [];

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

    /// <summary>已在服务器上被删除，只是本机保存的副本：聊天中显示为“已被删除”，点击后展开。</summary>
    public bool IsDeleted { get; set; }

    /// <summary>Notification Bot 消息的显示方式，由 <see cref="Services.SystemNoticeStore.Classify(ChatMessage)"/> 计算。</summary>
    public SystemNoticeKind Notice { get; set; }

    /// <summary>不在聊天中显示（插件隐藏，或已归入系统通知 / 消息提醒）。</summary>
    public bool IsHidden => IsHiddenByPlugin || Notice is SystemNoticeKind.Notice or SystemNoticeKind.Reminder;

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
