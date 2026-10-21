using System;

namespace ZulipQQ.Models;

public enum ConversationType
{
    Friend,
    Group,
    System,
}

/// <summary>会话预览前显示的红色提示。</summary>
public enum ConversationNotice
{
    None,
    AtAll,
    AtMe,
    NewFile,
}

/// <summary>
/// 消息列表中的一个会话。
/// </summary>
/// <param name="LastSender">最后一条消息的发送者；私聊或系统会话为 null，此时预览不带“发送者：”前缀。</param>
/// <param name="Draft">未发送的草稿；不为空时预览显示草稿而不是最后一条消息。</param>
public sealed record Conversation(
    long Id,
    ConversationType Type,
    string Title,
    string? LastSender,
    string LastMessage,
    DateTime LastMessageTime,
    int UnreadCount = 0,
    bool IsPinned = false,
    bool IsMuted = false,
    ConversationNotice Notice = ConversationNotice.None,
    string? Draft = null);
