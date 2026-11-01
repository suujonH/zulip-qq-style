using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using PluginShared;

namespace ReplyAssistant;

/// <summary>分析时使用的场景与关系：<see cref="Relationship"/> 为 null 表示不指定关系。</summary>
internal sealed record SceneChoice(JevScene Scene, JevRelationship? Relationship)
{
    /// <summary>显示用，例如“工作 / 同事”或“工作（不指定关系）”。</summary>
    public string Label => Relationship is null ? $"{Scene.Label}（不指定关系）" : $"{Scene.Label} / {Relationship.Label}";

    /// <summary>交给模型的关系说明（jev 的 relationshipNote，不含争执模式）。</summary>
    public string Note => Relationship?.Note ?? Scene.UnspecifiedNote;
}

/// <summary>保存的话题场景。</summary>
internal sealed record TopicScene(long AccountId, long StreamId, string Topic, string Name, string Scene);

/// <summary>保存的好友身份：<see cref="Identity"/> 为“场景 id/关系 id”。</summary>
internal sealed record UserIdentity(long AccountId, long UserId, string Name, string Identity);

/// <summary>
/// 插件自己保存的数据（数据目录中的 reply-assistant.db）：话题的场景、好友的身份、分析结果。
/// 插件线程与工作线程都会访问；数据库打不开时读取为空、保存被忽略（已写入插件日志）。
/// </summary>
internal static class Store
{
    private const string Schema = """
        CREATE TABLE IF NOT EXISTS topic_scene(
            account INTEGER NOT NULL, stream INTEGER NOT NULL, topic TEXT NOT NULL COLLATE NOCASE,
            name TEXT NOT NULL, scene TEXT NOT NULL, PRIMARY KEY(account, stream, topic));
        CREATE TABLE IF NOT EXISTS user_identity(
            account INTEGER NOT NULL, user_id INTEGER NOT NULL, name TEXT NOT NULL, identity TEXT NOT NULL,
            PRIMARY KEY(account, user_id));
        CREATE TABLE IF NOT EXISTS analysis(
            account INTEGER NOT NULL, message_id INTEGER NOT NULL, content TEXT NOT NULL, updated_at INTEGER NOT NULL,
            PRIMARY KEY(account, message_id));
        """;

    private static readonly Lock Gate = new();
    private static SqliteDb? _db;
    private static bool _opened;

    public static string? GetTopicScene(long accountId, long streamId, string topic) =>
        Query("SELECT scene FROM topic_scene WHERE account = ? AND stream = ? AND topic = ?;", r => r.Text(0), accountId, streamId, topic)
            .FirstOrDefault();

    /// <summary>保存话题的场景；<paramref name="scene"/> 为空时删除（使用默认场景）。name 为空时保留原来的名称。</summary>
    public static void SetTopicScene(long accountId, long streamId, string topic, string name, string scene)
    {
        if (scene.Length == 0)
            Execute("DELETE FROM topic_scene WHERE account = ? AND stream = ? AND topic = ?;", accountId, streamId, topic);
        else
            Execute("""
                INSERT INTO topic_scene(account, stream, topic, name, scene) VALUES(?, ?, ?, ?, ?)
                ON CONFLICT(account, stream, topic) DO UPDATE SET scene = excluded.scene,
                    name = CASE WHEN excluded.name = '' THEN topic_scene.name ELSE excluded.name END;
                """, accountId, streamId, topic, name, scene);
    }

    public static List<TopicScene> TopicScenes() =>
        Query("SELECT account, stream, topic, name, scene FROM topic_scene ORDER BY name;",
            r => new TopicScene(r.Long(0), r.Long(1), r.Text(2), r.Text(3), r.Text(4)));

    public static string? GetIdentity(long accountId, long userId) =>
        Query("SELECT identity FROM user_identity WHERE account = ? AND user_id = ?;", r => r.Text(0), accountId, userId).FirstOrDefault();

    /// <summary>保存好友的身份（“场景 id/关系 id”）；为空时删除。name 为空时保留原来的名称。</summary>
    public static void SetIdentity(long accountId, long userId, string name, string identity)
    {
        if (identity.Length == 0)
            Execute("DELETE FROM user_identity WHERE account = ? AND user_id = ?;", accountId, userId);
        else
            Execute("""
                INSERT INTO user_identity(account, user_id, name, identity) VALUES(?, ?, ?, ?)
                ON CONFLICT(account, user_id) DO UPDATE SET identity = excluded.identity,
                    name = CASE WHEN excluded.name = '' THEN user_identity.name ELSE excluded.name END;
                """, accountId, userId, name, identity);
    }

    public static List<UserIdentity> Identities() =>
        Query("SELECT account, user_id, name, identity FROM user_identity ORDER BY name;",
            r => new UserIdentity(r.Long(0), r.Long(1), r.Text(2), r.Text(3)));

    /// <summary>保存一条消息的分析结果（附加内容的 JSON）。</summary>
    public static void SetAnalysis(long accountId, long messageId, JsonObject annotation) =>
        Execute("""
            INSERT INTO analysis(account, message_id, content, updated_at) VALUES(?, ?, ?, ?)
            ON CONFLICT(account, message_id) DO UPDATE SET content = excluded.content, updated_at = excluded.updated_at;
            """, accountId, messageId, annotation.ToJsonString(), DateTimeOffset.UtcNow.ToUnixTimeSeconds());

    /// <summary>这些消息中保存过分析结果的：(消息 id, 附加内容的 JSON)。</summary>
    public static List<(long MessageId, string Content)> Analyses(long accountId, IReadOnlyList<long> messageIds) =>
        messageIds.Count == 0
            ? []
            : Query($"SELECT message_id, content FROM analysis WHERE account = ? AND message_id IN ({string.Join(",", messageIds)});",
                r => (r.Long(0), r.Text(1)), accountId);

    private static SqliteDb? Db()
    {
        lock (Gate)
        {
            if (!_opened)
            {
                _opened = true;
                if (Host.GetDataDir() is { Length: > 0 } dir)
                    _db = SqliteDb.Open(Path.Combine(dir, "reply-assistant.db"), Schema);
            }
            return _db;
        }
    }

    private static void Execute(string sql, params object?[] args)
    {
        try
        {
            Db()?.Execute(sql, args);
        }
        catch (InvalidOperationException ex)
        {
            Host.Log(Zq.LogWarning, $"保存数据失败：{ex.Message}");
        }
    }

    private static List<T> Query<T>(string sql, Func<SqliteReader, T> read, params object?[] args)
    {
        try
        {
            return Db()?.Query(sql, read, args) ?? [];
        }
        catch (InvalidOperationException ex)
        {
            Host.Log(Zq.LogWarning, $"读取数据失败：{ex.Message}");
            return [];
        }
    }
}
