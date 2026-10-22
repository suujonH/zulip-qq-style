using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using ZulipQQ.Core;
using ZulipQQ.Zulip;

namespace ZulipQQ.Services;

/// <summary>聊天相关的服务器操作。本分支只有标记已读，发送、上传、星标、表情回应在后续阶段加入。</summary>
public sealed class ChatService(ZulipSession session)
{
    private const int FlagBatchSize = 1000;
    private const int NarrowBatchSize = 5000;

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
}
