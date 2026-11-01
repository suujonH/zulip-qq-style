using System.Collections.Generic;

namespace ZulipQQ.Data;

/// <summary>
/// 消息编辑前的各个版本（服务器渲染的 HTML）。来自消息的 edit_history 和 update_message 事件；
/// 同一次编辑（相同的编辑时间）只保存一次，先保存的为准。
/// </summary>
public sealed class MessageVersionRepository(LocalDb db, long accountId)
{
    /// <summary>编辑前的各个版本，从最早（原始内容）到最近。</summary>
    public List<string> Load(long messageId) =>
        db.Query("SELECT content FROM message_version WHERE account_id = @a AND message_id = @m ORDER BY edited_at;",
            r => r.GetString(0), ("@a", accountId), ("@m", messageId));

    /// <summary>保存在 <paramref name="editedAt"/>（Unix 秒）被编辑替换掉的内容。</summary>
    public void Add(long messageId, long editedAt, string content) =>
        db.Execute("""
            INSERT INTO message_version(account_id, message_id, edited_at, content) VALUES(@a, @m, @t, @c)
            ON CONFLICT(account_id, message_id, edited_at) DO NOTHING;
            """, ("@a", accountId), ("@m", messageId), ("@t", editedAt), ("@c", content));
}
