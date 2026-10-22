using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ZulipQQ.Core;
using ZulipQQ.Models;
using ZulipQQ.Zulip;
using ZulipQQ.Zulip.Dtos;

namespace ZulipQQ.Services;

/// <summary>聊天相关的服务器操作：已读、发送、上传、表情回应、星标。</summary>
public sealed class ChatService(ZulipSession session)
{
    private const int FlagBatchSize = 1000;
    private const int NarrowBatchSize = 5000;

    // 发送成功后等待 message 事件的时间；超时后主动查询这条消息替换本地回显
    private static readonly TimeSpan EchoFallbackDelay = TimeSpan.FromSeconds(10);

    private static readonly Dictionary<string, string> ContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png", [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg", [".gif"] = "image/gif",
        [".webp"] = "image/webp", [".bmp"] = "image/bmp", [".mp4"] = "video/mp4", [".webm"] = "video/webm",
        [".mov"] = "video/quicktime", [".pdf"] = "application/pdf", [".txt"] = "text/plain", [".zip"] = "application/zip",
    };

    // 发送失败的消息内容，点击重发时使用
    private readonly Dictionary<string, (ConversationKey Key, string Content)> _failed = [];

    /// <summary>标记已读：先在本地清除未读，再提交服务器（每批最多 1000 条）。</summary>
    public async Task MarkReadAsync(IReadOnlyCollection<long> ids)
    {
        session.Unread.MarkLocallyRead(ids);
        foreach (var batch in ids.Chunk(FlagBatchSize))
        {
            await session.Client.UpdateFlagsAsync(batch, "add", "read", session.Token);
            if (session.IsDisposed)
                return;
        }
    }

    /// <summary>把整个会话标记已读：按 narrow 分批提交，直到服务器处理到最新的消息。</summary>
    public async Task MarkConversationReadAsync(ConversationKey key)
    {
        session.Unread.MarkLocallyRead(key);
        var narrow = Narrow.For(key, session.SelfId, key is TopicKey t ? session.Conversations.GetTopicName(t) : null);
        narrow.Add(Narrow.Is("unread"));

        var anchor = "oldest";
        var includeAnchor = true;
        while (true)
        {
            var result = await session.Client.UpdateFlagsForNarrowAsync(
                narrow, anchor, includeAnchor, 0, NarrowBatchSize, "add", "read", session.Token);
            if (session.IsDisposed || result.FoundNewest || result.LastProcessedId is not { } last)
                return;
            anchor = last.ToString(CultureInfo.InvariantCulture);
            includeAnchor = false;
        }
    }

    /// <summary>
    /// 切换表情回应：自己已回应过同一个表情时撤销，否则添加。不做乐观更新，界面等待 reaction 事件。
    /// </summary>
    public Task ToggleReactionAsync(ChatMessage msg, string emojiName, string emojiCode, string reactionType)
    {
        var mine = msg.Reactions.Exists(r => r.UserId == session.SelfId && r.EmojiCode == emojiCode && r.ReactionType == reactionType);
        return mine
            ? session.Client.RemoveReactionAsync(msg.Id, emojiName, emojiCode, reactionType, session.Token)
            : session.Client.AddReactionAsync(msg.Id, emojiName, emojiCode, reactionType, session.Token);
    }

    /// <summary>
    /// 发送消息：先插入本地回显，再提交服务器。失败时回显标记为失败（可重发）；
    /// 成功后 10 秒内没有收到 message 事件，就主动查询这条消息替换回显（在后台进行）。
    /// 服务器接受后返回 true，因此依次 await 可以保证多条消息的顺序。
    /// </summary>
    public async Task<bool> SendAsync(ConversationKey key, string content)
    {
        var localId = Guid.NewGuid().ToString("N");
        session.Messages.AddPending(key, content, localId);
        long id;
        try
        {
            id = (await session.Client.SendMessageAsync(ToOutgoing(key, content, localId), session.Token)).Id;
        }
        catch (Exception ex)
        {
            if (session.IsDisposed)
                return false;
            Log.Warn($"发送消息失败：{key}", ex);
            _failed[localId] = (key, content);
            session.Messages.MarkFailed(localId);
            return false;
        }

        session.Forget(EnsureEchoAsync(localId, id), "查询已发送的消息");
        return true;
    }

    private async Task EnsureEchoAsync(string localId, long id)
    {
        try
        {
            await Task.Delay(EchoFallbackDelay, session.Token);
            if (session.IsDisposed || !session.Messages.IsPending(localId))
                return;
            var dto = await session.Client.GetMessageAsync(id, ct: session.Token);
            if (session.IsDisposed)
                return;
            var msg = ChatMessage.FromDto(dto, ConversationKey.FromMessage(dto, session.SelfId));
            session.Messages.OnNewMessage(msg, localId);
            session.Conversations.OnNewMessage(msg);
        }
        catch (Exception ex)
        {
            if (!session.IsDisposed)
                Log.Warn($"查询已发送的消息失败：{id}", ex);
        }
    }

    /// <summary>重发失败的消息：移除失败的回显，用同样的内容重新发送。</summary>
    public Task RetryAsync(string localId)
    {
        // 超时的请求可能在服务器上迟到处理，事件已替换回显时不再重发
        if (!_failed.Remove(localId, out var failed) || !session.Messages.IsPending(localId))
            return Task.CompletedTask;
        session.Messages.RemovePending(localId);
        return SendAsync(failed.Key, failed.Content);
    }

    /// <summary>上传本地文件，返回服务器地址。<paramref name="progress"/> 报告 0～1 的进度（在调用线程上回调）。</summary>
    public async Task<UploadResult> UploadAsync(string path, IProgress<double>? progress = null, System.Threading.CancellationToken ct = default)
    {
        await using var stream = File.OpenRead(path);
        var total = stream.Length;
        var bytes = progress is null ? null : new Progress<long>(done => progress.Report(total > 0 ? (double)done / total : 0));
        using var linked = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(ct, session.Token);
        var contentType = ContentTypes.GetValueOrDefault(Path.GetExtension(path), "application/octet-stream");
        return await session.Client.UploadFileAsync(stream, Path.GetFileName(path), contentType, bytes, linked.Token);
    }

    /// <summary>收藏或取消收藏（星标）。不做乐观更新，界面等待 update_message_flags 事件。</summary>
    public Task SetStarredAsync(IReadOnlyCollection<long> ids, bool starred) =>
        session.Client.UpdateFlagsAsync(ids, starred ? "add" : "remove", "starred", session.Token);

    /// <summary>消息的 Markdown 原文（引用、转发用）。</summary>
    public async Task<string> GetRawContentAsync(long messageId) =>
        (await session.Client.GetMessageAsync(messageId, applyMarkdown: false, session.Token)).Content;

    private OutgoingMessage ToOutgoing(ConversationKey key, string content, string localId) => key switch
    {
        TopicKey t => new OutgoingMessage(t.StreamId, session.Conversations.GetTopicName(t), null, content, session.QueueId, localId),
        DmKey d => new OutgoingMessage(null, null, d.IsSelfDm ? [session.SelfId] : [.. d.OtherUserIds], content, session.QueueId, localId),
        _ => throw new ArgumentException("不支持的会话", nameof(key)),
    };
}
