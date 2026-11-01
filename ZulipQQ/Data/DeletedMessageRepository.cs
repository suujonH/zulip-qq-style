using System.Collections.Generic;
using ZulipQQ.Core;
using ZulipQQ.Models;

namespace ZulipQQ.Data;

/// <summary>
/// 被删除的消息在本机的副本：删除时消息缓存中的内容（不含表情回应和小部件）。同一条消息只保存一次。
/// </summary>
public sealed class DeletedMessageRepository(LocalDb db, long accountId)
{
    /// <summary>保存被删除的消息。</summary>
    public void Add(ChatMessage m) =>
        db.Execute("""
            INSERT INTO deleted_message(account_id, message_id, conv_key, sender_id, sender_name, avatar_url, content,
              timestamp, last_edit_timestamp, is_me_message, topic, stream_id)
            VALUES(@a, @m, @k, @s, @n, @u, @c, @t, @e, @me, @tp, @st)
            ON CONFLICT(account_id, message_id) DO NOTHING;
            """,
            ("@a", accountId), ("@m", m.Id), ("@k", m.Key.ToStorageString()), ("@s", m.SenderId), ("@n", m.SenderName),
            ("@u", m.AvatarUrl), ("@c", m.ContentHtml), ("@t", m.Timestamp), ("@e", m.LastEditTimestamp),
            ("@me", m.IsMeMessage ? 1 : 0), ("@tp", m.Topic), ("@st", m.StreamId));

    /// <summary>某会话中被删除的消息，按 id 升序；<see cref="ChatMessage.IsDeleted"/> 为 true，已读。</summary>
    public List<ChatMessage> Load(ConversationKey key) =>
        db.Query("""
            SELECT message_id, sender_id, sender_name, avatar_url, content, timestamp, last_edit_timestamp, is_me_message,
              topic, stream_id
            FROM deleted_message WHERE account_id = @a AND conv_key = @k ORDER BY message_id;
            """,
            r => new ChatMessage
            {
                Id = r.GetInt64(0),
                // 保存的会话键中话题是小写的，显示用原始大小写的话题名
                Key = key,
                SenderId = r.GetInt64(1),
                SenderName = r.GetString(2),
                AvatarUrl = r.IsDBNull(3) ? null : r.GetString(3),
                ContentHtml = r.GetString(4),
                Timestamp = r.GetInt64(5),
                LastEditTimestamp = r.IsDBNull(6) ? null : r.GetInt64(6),
                IsMeMessage = r.GetInt64(7) != 0,
                Topic = r.GetString(8),
                StreamId = r.IsDBNull(9) ? null : r.GetInt64(9),
                Flags = ["read"],
                IsDeleted = true,
            },
            ("@a", accountId), ("@k", key.ToStorageString()));
}
