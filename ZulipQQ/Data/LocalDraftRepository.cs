using System.Collections.Generic;
using ZulipQQ.Core;

namespace ZulipQQ.Data;

/// <summary>本地草稿，只在 Zulip 用户关闭了草稿同步时使用。</summary>
public sealed class LocalDraftRepository(LocalDb db, long accountId)
{
    /// <summary>会话键 → 草稿内容。无法解析的键跳过。</summary>
    public Dictionary<ConversationKey, string> LoadAll()
    {
        var result = new Dictionary<ConversationKey, string>();
        foreach (var (key, content) in db.Query("SELECT conv_key, content FROM local_draft WHERE account_id = @a;",
                     r => (r.GetString(0), r.GetString(1)), ("@a", accountId)))
        {
            try
            {
                result[ConversationKey.Parse(key)] = content;
            }
            catch (System.FormatException)
            {
            }
        }
        return result;
    }

    public void Set(ConversationKey key, string content, long updatedAt) =>
        db.Execute("""
            INSERT INTO local_draft(account_id, conv_key, content, updated_at) VALUES(@a, @k, @c, @t)
            ON CONFLICT(account_id, conv_key) DO UPDATE SET content = excluded.content, updated_at = excluded.updated_at;
            """, ("@a", accountId), ("@k", key.ToStorageString()), ("@c", content), ("@t", updatedAt));

    public void Delete(ConversationKey key) =>
        db.Execute("DELETE FROM local_draft WHERE account_id = @a AND conv_key = @k;", ("@a", accountId), ("@k", key.ToStorageString()));

    public void DeleteAll() => db.Execute("DELETE FROM local_draft WHERE account_id = @a;", ("@a", accountId));
}
