using System.Collections.Generic;
using System.Linq;
using Microsoft.Data.Sqlite;
using ZulipQQ.Core;

namespace ZulipQQ.Data;

/// <summary>会话的本地状态：是否在消息列表中、置顶、免打扰级别、备注、最后一条消息的预览缓存。</summary>
/// <param name="TitleCache">最近一次看到的原始话题名（TopicKey 用）。</param>
/// <param name="PinnedAt">置顶时间（Unix 毫秒）；null 表示未置顶。</param>
/// <param name="LastMessageTime">Unix 秒。</param>
public sealed record ConversationStateRow(
    ConversationKey Key,
    string TitleCache,
    long? PinnedAt,
    DndLevel? DndLevel,
    bool InList,
    string? Remark,
    long LastMessageId,
    long LastMessageTime,
    long LastSenderId,
    string LastPreview);

public sealed class ConversationStateRepository(LocalDb db, long accountId)
{
    private const string Columns =
        "conv_key, title_cache, pinned_at, dnd_level, in_list, remark, last_message_id, last_message_time, last_sender_id, last_preview";

    /// <summary>读取全部行。无法解析的键（例如旧版本格式）跳过。</summary>
    public List<ConversationStateRow> LoadAll()
    {
        var rows = db.Query($"SELECT {Columns} FROM conversation_state WHERE account_id = @a;", r => TryMap(r), ("@a", accountId));
        return rows.OfType<ConversationStateRow>().ToList();
    }

    public ConversationStateRow? Find(ConversationKey key)
    {
        var rows = db.Query($"SELECT {Columns} FROM conversation_state WHERE account_id = @a AND conv_key = @k;", r => TryMap(r),
            ("@a", accountId), ("@k", key.ToStorageString()));
        return rows.Count > 0 ? rows[0] : null;
    }

    public void Upsert(ConversationStateRow row) =>
        db.Execute("""
            INSERT INTO conversation_state(account_id, conv_key, title_cache, pinned_at, dnd_level, in_list, remark,
              last_message_id, last_message_time, last_sender_id, last_preview)
            VALUES(@a, @k, @title, @pinned, @dnd, @inlist, @remark, @mid, @mtime, @sender, @preview)
            ON CONFLICT(account_id, conv_key) DO UPDATE SET
              title_cache = excluded.title_cache, pinned_at = excluded.pinned_at, dnd_level = excluded.dnd_level,
              in_list = excluded.in_list, remark = excluded.remark, last_message_id = excluded.last_message_id,
              last_message_time = excluded.last_message_time, last_sender_id = excluded.last_sender_id,
              last_preview = excluded.last_preview;
            """,
            ("@a", accountId), ("@k", row.Key.ToStorageString()), ("@title", row.TitleCache), ("@pinned", row.PinnedAt),
            ("@dnd", row.DndLevel is { } level ? (int)level : null), ("@inlist", row.InList ? 1 : 0), ("@remark", row.Remark),
            ("@mid", row.LastMessageId), ("@mtime", row.LastMessageTime), ("@sender", row.LastSenderId),
            ("@preview", row.LastPreview));

    /// <summary>
    /// 只有 <paramref name="messageId"/> 比已保存的更新时才更新预览和标题；行不存在时先创建（不在列表中）。
    /// </summary>
    public void UpdatePreview(ConversationKey key, long messageId, long time, long senderId, string preview, string title)
    {
        db.InTransaction(() =>
        {
            EnsureRow(key, title);
            db.Execute("""
                UPDATE conversation_state SET last_message_id = @mid, last_message_time = @mtime, last_sender_id = @sender,
                  last_preview = @preview, title_cache = @title
                WHERE account_id = @a AND conv_key = @k AND last_message_id < @mid;
                """,
                ("@a", accountId), ("@k", key.ToStorageString()), ("@mid", messageId), ("@mtime", time),
                ("@sender", senderId), ("@preview", preview), ("@title", title));
        });
    }

    /// <summary>
    /// 无条件覆盖预览字段，用于删除、移动、编辑最后一条消息后重新取预览。会话已没有消息时传 0 和空字符串。
    /// </summary>
    public void ReplacePreview(ConversationKey key, long messageId, long time, long senderId, string preview)
    {
        db.InTransaction(() =>
        {
            EnsureRow(key, "");
            db.Execute("""
                UPDATE conversation_state SET last_message_id = @mid, last_message_time = @mtime, last_sender_id = @sender,
                  last_preview = @preview
                WHERE account_id = @a AND conv_key = @k;
                """,
                ("@a", accountId), ("@k", key.ToStorageString()), ("@mid", messageId), ("@mtime", time),
                ("@sender", senderId), ("@preview", preview));
        });
    }

    public void SetPinned(ConversationKey key, long? pinnedAt) => SetColumn(key, "pinned_at", pinnedAt);

    public void SetDnd(ConversationKey key, DndLevel? level) => SetColumn(key, "dnd_level", level is { } l ? (int)l : null);

    public void SetInList(ConversationKey key, bool inList) => SetColumn(key, "in_list", inList ? 1 : 0);

    public void SetRemark(ConversationKey key, string? remark) => SetColumn(key, "remark", remark);

    public void SetTitle(ConversationKey key, string title) => SetColumn(key, "title_cache", title);

    /// <summary>
    /// 话题被整体移动或改名。只改了大小写时只更新标题；新键已存在时删除旧行、保留新行；否则把旧行改为新键。
    /// </summary>
    public void Rename(ConversationKey oldKey, ConversationKey newKey, string newTitle)
    {
        var oldText = oldKey.ToStorageString();
        var newText = newKey.ToStorageString();
        db.InTransaction(() =>
        {
            if (oldText == newText)
            {
                SetTitle(newKey, newTitle);
                return;
            }

            var newExists = db.Scalar<long>("SELECT COUNT(*) FROM conversation_state WHERE account_id = @a AND conv_key = @k;",
                ("@a", accountId), ("@k", newText)) > 0;
            if (newExists)
            {
                db.Execute("DELETE FROM conversation_state WHERE account_id = @a AND conv_key = @k;", ("@a", accountId), ("@k", oldText));
                SetTitle(newKey, newTitle);
            }
            else
            {
                db.Execute("UPDATE conversation_state SET conv_key = @new, title_cache = @title WHERE account_id = @a AND conv_key = @old;",
                    ("@a", accountId), ("@old", oldText), ("@new", newText), ("@title", newTitle));
            }
        });
    }

    public void Delete(ConversationKey key) =>
        db.Execute("DELETE FROM conversation_state WHERE account_id = @a AND conv_key = @k;", ("@a", accountId), ("@k", key.ToStorageString()));

    private void EnsureRow(ConversationKey key, string title) =>
        db.Execute("INSERT OR IGNORE INTO conversation_state(account_id, conv_key, title_cache) VALUES(@a, @k, @title);",
            ("@a", accountId), ("@k", key.ToStorageString()), ("@title", title));

    private void SetColumn(ConversationKey key, string column, object? value)
    {
        db.InTransaction(() =>
        {
            EnsureRow(key, "");
            db.Execute($"UPDATE conversation_state SET {column} = @v WHERE account_id = @a AND conv_key = @k;",
                ("@a", accountId), ("@k", key.ToStorageString()), ("@v", value));
        });
    }

    private static ConversationStateRow? TryMap(SqliteDataReader r)
    {
        ConversationKey key;
        try
        {
            key = ConversationKey.Parse(r.GetString(0));
        }
        catch (System.FormatException)
        {
            return null;
        }

        return new ConversationStateRow(
            key,
            r.GetString(1),
            r.IsDBNull(2) ? null : r.GetInt64(2),
            r.IsDBNull(3) ? null : (DndLevel)r.GetInt64(3),
            r.GetInt64(4) != 0,
            r.IsDBNull(5) ? null : r.GetString(5),
            r.GetInt64(6),
            r.GetInt64(7),
            r.GetInt64(8),
            r.GetString(9));
    }
}
