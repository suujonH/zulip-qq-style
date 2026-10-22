using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using ZulipQQ.Core;
using ZulipQQ.Zulip;
using ZulipQQ.Zulip.Dtos;

namespace ZulipQQ.Services;

/// <summary>
/// 定时消息：register 时载入，scheduled_messages 事件增删改。创建与删除不做乐观更新，等待事件。只在 UI 线程上读写。
/// </summary>
public sealed class ScheduledMessageStore(ZulipSession session)
{
    private readonly Dictionary<long, ScheduledMessageDto> _items = [];

    /// <summary>定时消息有增删改（或重新载入）。</summary>
    public event EventHandler? Changed;

    public void Load(RegisterResult reg)
    {
        _items.Clear();
        foreach (var m in reg.ScheduledMessages)
            _items[m.ScheduledMessageId] = m;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void OnEvent(JsonElement ev)
    {
        switch (ev.GetProperty("op").GetString())
        {
            case "add":
                foreach (var m in ZulipJson.Deserialize<List<ScheduledMessageDto>>(ev.GetProperty("scheduled_messages")))
                    _items[m.ScheduledMessageId] = m;
                break;
            case "update":
                var updated = ZulipJson.Deserialize<ScheduledMessageDto>(ev.GetProperty("scheduled_message"));
                _items[updated.ScheduledMessageId] = updated;
                break;
            case "remove":
                _items.Remove(ev.GetProperty("scheduled_message_id").GetInt64());
                break;
            default:
                return;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>发往该会话的定时消息，按发送时间先后。</summary>
    public IReadOnlyList<ScheduledMessageDto> For(ConversationKey key) =>
        _items.Values.Where(m => key.Equals(KeyOf(m))).OrderBy(m => m.ScheduledDeliveryTimestamp).ToList();

    /// <summary>定时消息的会话；格式不认识时为 null。</summary>
    public ConversationKey? KeyOf(ScheduledMessageDto m)
    {
        if (m.Type == "stream")
            return m.To.ValueKind == JsonValueKind.Number ? new TopicKey(m.To.GetInt64(), m.Topic) : null;
        if (m.To.ValueKind != JsonValueKind.Array)
            return null;
        return DmKey.Create(m.To.EnumerateArray().Select(e => e.GetInt64()), session.SelfId);
    }

    /// <summary>在 <paramref name="deliverAt"/>（Unix 秒）发送到会话。</summary>
    public Task CreateAsync(ConversationKey key, string content, long deliverAt) =>
        session.Client.CreateScheduledMessageAsync(session.Chat.ToOutgoing(key, content), deliverAt, session.Token);

    public Task DeleteAsync(long id) => session.Client.DeleteScheduledMessageAsync(id, session.Token);
}
