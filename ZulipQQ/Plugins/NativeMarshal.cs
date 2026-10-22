using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using ZulipQQ.Core;
using ZulipQQ.Models;
using ZulipQQ.Services;

namespace ZulipQQ.Plugins;

/// <summary>插件看到的会话（不可变，在 UI 线程上生成）。</summary>
public sealed record PluginConversation(int Type, long StreamId, string? StreamName, string? Topic, long[] DmUserIds)
{
    /// <summary>在 UI 线程上调用：频道名、原始话题名从会话中读取。</summary>
    public static PluginConversation From(ConversationKey key, ZulipSession session) => key switch
    {
        TopicKey t => new PluginConversation(Zq.ConvChannel, t.StreamId, session.Channels.Get(t.StreamId)?.Name ?? "",
            session.Conversations.GetTopicName(t), []),
        DmKey d => new PluginConversation(Zq.ConvDirect, 0, null, null, [.. d.OtherUserIds]),
        _ => throw new ArgumentException("不支持的会话", nameof(key)),
    };

    /// <summary>插件传入的会话（get_messages）；结构不合法时返回 null。</summary>
    public static unsafe PluginConversation? FromNative(ZqConversation* conv)
    {
        if (conv->Type == Zq.ConvChannel)
        {
            var topic = Marshal.PtrToStringUTF8(conv->Topic);
            return conv->StreamId > 0 && topic is not null ? new PluginConversation(Zq.ConvChannel, conv->StreamId, null, topic, []) : null;
        }
        if (conv->Type != Zq.ConvDirect || conv->DmUserCount < 0 || conv->DmUserCount > 1000 || (conv->DmUserCount > 0 && conv->DmUserIds == 0))
            return null;
        var ids = new long[conv->DmUserCount];
        for (var i = 0; i < ids.Length; i++)
            ids[i] = ((long*)conv->DmUserIds)[i];
        return new PluginConversation(Zq.ConvDirect, 0, null, null, ids);
    }

    /// <summary>对应的会话键。</summary>
    public ConversationKey ToKey(long selfId) =>
        Type == Zq.ConvChannel ? new TopicKey(StreamId, Topic ?? "") : DmKey.Create(DmUserIds, selfId);

    public ZqConversation ToNative(Utf8Scope scope) => new()
    {
        Type = Type,
        DmUserCount = Type == Zq.ConvDirect ? DmUserIds.Length : 0,
        StreamId = StreamId,
        StreamName = scope.Str(StreamName),
        Topic = scope.Str(Topic),
        DmUserIds = Type == Zq.ConvDirect ? scope.Longs(DmUserIds) : 0,
    };
}

/// <summary>
/// 插件看到的消息（不可变，在 UI 线程上从 <see cref="ChatMessage"/> 复制）。插件线程只读取快照，绝不读取 ChatMessage。
/// </summary>
public sealed record PluginMessage(
    uint Flags, long Id, long Timestamp, long SenderId, string SenderName, string SenderEmail,
    PluginConversation Conv, string Html, string Text)
{
    /// <summary>在 UI 线程上调用。<paramref name="historical"/> 为 true 表示通过 get_messages 获取。</summary>
    public static PluginMessage From(ChatMessage m, ZulipSession session, bool historical = false)
    {
        uint flags = 0;
        if (m.SenderId == session.SelfId)
            flags |= Zq.MsgOutgoing;
        if (m.Flags.Contains("mentioned"))
            flags |= Zq.MsgMentioned;
        if (m.Flags.Contains("stream_wildcard_mentioned") || m.Flags.Contains("topic_wildcard_mentioned") || m.Flags.Contains("wildcard_mentioned"))
            flags |= Zq.MsgWildcardMention;
        if (m.IsStarred)
            flags |= Zq.MsgStarred;
        if (m.IsRead)
            flags |= Zq.MsgRead;
        if (historical)
            flags |= Zq.MsgHistorical;
        return new PluginMessage(flags, m.Id, m.Timestamp, m.SenderId, m.SenderName, m.SenderEmail,
            PluginConversation.From(m.Key, session), m.ContentHtml, ZulipHtml.ToPlainText(m.ContentHtml));
    }

    public ZqMessage ToNative(Utf8Scope scope) => new()
    {
        StructSize = (uint)Marshal.SizeOf<ZqMessage>(),
        Flags = Flags,
        MessageId = Id,
        Timestamp = Timestamp,
        SenderId = SenderId,
        SenderName = scope.Str(SenderName),
        SenderEmail = scope.Str(SenderEmail),
        Conv = Conv.ToNative(scope),
        ContentHtml = scope.Str(Html),
        ContentText = scope.Str(Text),
    };
}

/// <summary>插件看到的用户（不可变，在 UI 线程上生成）。</summary>
public sealed record PluginUser(long Id, int Role, string FullName, string Email, bool IsBot, bool IsActive, string? Remark)
{
    public static PluginUser From(ZulipUser u, ZulipSession session) =>
        new(u.Id, u.Role, u.FullName, u.Email, u.IsBot, u.IsActive, session.Contacts.GetRemark(u.Id));

    public ZqUser ToNative(Utf8Scope scope) => new()
    {
        StructSize = (uint)Marshal.SizeOf<ZqUser>(),
        Role = Role,
        UserId = Id,
        FullName = scope.Str(FullName),
        Email = scope.Str(Email),
        IsBot = IsBot ? 1 : 0,
        IsActive = IsActive ? 1 : 0,
        Remark = scope.Str(Remark),
    };
}

/// <summary>
/// 一次回调期间分配的非托管内存（UTF-8 字符串、id 数组），Dispose 时统一释放。
/// 交给插件的指针只在回调期间有效。
/// </summary>
public sealed class Utf8Scope : IDisposable
{
    private readonly List<nint> _allocations = [];
    private readonly List<nint> _native = [];

    /// <summary>以 \0 结尾的 UTF-8 字符串；null 返回空指针。</summary>
    public nint Str(string? s)
    {
        if (s is null)
            return 0;
        var p = Marshal.StringToCoTaskMemUTF8(s);
        _allocations.Add(p);
        return p;
    }

    public unsafe nint Longs(IReadOnlyList<long> values)
    {
        if (values.Count == 0)
            return 0;
        var p = (nint)NativeMemory.Alloc((nuint)values.Count, sizeof(long));
        _native.Add(p);
        for (var i = 0; i < values.Count; i++)
            ((long*)p)[i] = values[i];
        return p;
    }

    public unsafe void Dispose()
    {
        foreach (var p in _allocations)
            Marshal.FreeCoTaskMem(p);
        foreach (var p in _native)
            NativeMemory.Free((void*)p);
        _allocations.Clear();
        _native.Clear();
    }
}

/// <summary>从插件传入的指针读取字符串。</summary>
internal static class NativeText
{
    public static string? Read(nint p) => p == 0 ? null : Marshal.PtrToStringUTF8(p);

    /// <summary>插件传入的会话 id 列表。</summary>
    public static unsafe long[] ReadLongs(long* p, int count) =>
        p == null || count <= 0 ? [] : Enumerable.Range(0, count).Select(i => p[i]).ToArray();
}
