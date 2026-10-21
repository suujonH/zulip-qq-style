using System.Collections.Generic;
using Microsoft.Data.Sqlite;

namespace ZulipQQ.Data;

/// <summary>下载到默认目录的文件记录。</summary>
/// <param name="UploadPath">以 <c>/user_uploads/</c> 开头的路径（不含域名）。</param>
/// <param name="ConvKey">所属会话的存储字符串。</param>
/// <param name="DownloadedAt">Unix 秒。</param>
/// <param name="LastOpenedAt">Unix 秒；从未打开为 null。</param>
public sealed record DownloadRecord(
    string UploadPath,
    string LocalPath,
    string FileName,
    long Size,
    long MessageId,
    string ConvKey,
    long DownloadedAt,
    long? LastOpenedAt);

public sealed class DownloadRepository(LocalDb db, long accountId)
{
    private const string Columns =
        "upload_path, local_path, file_name, size, message_id, conv_key, downloaded_at, last_opened_at";

    public DownloadRecord? Find(string uploadPath)
    {
        var rows = db.Query($"SELECT {Columns} FROM downloaded_file WHERE account_id = @a AND upload_path = @p;", Map,
            ("@a", accountId), ("@p", uploadPath));
        return rows.Count > 0 ? rows[0] : null;
    }

    public void Upsert(DownloadRecord r) =>
        db.Execute("""
            INSERT INTO downloaded_file(account_id, upload_path, local_path, file_name, size, message_id, conv_key, downloaded_at, last_opened_at)
            VALUES(@a, @p, @local, @name, @size, @mid, @conv, @at, @opened)
            ON CONFLICT(account_id, upload_path) DO UPDATE SET
              local_path = excluded.local_path, file_name = excluded.file_name, size = excluded.size,
              message_id = excluded.message_id, conv_key = excluded.conv_key, downloaded_at = excluded.downloaded_at,
              last_opened_at = excluded.last_opened_at;
            """,
            ("@a", accountId), ("@p", r.UploadPath), ("@local", r.LocalPath), ("@name", r.FileName), ("@size", r.Size),
            ("@mid", r.MessageId), ("@conv", r.ConvKey), ("@at", r.DownloadedAt), ("@opened", r.LastOpenedAt));

    public void MarkOpened(string uploadPath, long openedAt) =>
        db.Execute("UPDATE downloaded_file SET last_opened_at = @t WHERE account_id = @a AND upload_path = @p;",
            ("@a", accountId), ("@p", uploadPath), ("@t", openedAt));

    public void Delete(string uploadPath) =>
        db.Execute("DELETE FROM downloaded_file WHERE account_id = @a AND upload_path = @p;", ("@a", accountId), ("@p", uploadPath));

    /// <summary>最近使用：按最近打开时间（没有则按下载时间）降序。</summary>
    public List<DownloadRecord> Recent(int limit) =>
        db.Query($"SELECT {Columns} FROM downloaded_file WHERE account_id = @a ORDER BY COALESCE(last_opened_at, downloaded_at) DESC LIMIT @n;",
            Map, ("@a", accountId), ("@n", limit));

    private static DownloadRecord Map(SqliteDataReader r) => new(
        r.GetString(0),
        r.GetString(1),
        r.GetString(2),
        r.GetInt64(3),
        r.GetInt64(4),
        r.GetString(5),
        r.GetInt64(6),
        r.IsDBNull(7) ? null : r.GetInt64(7));
}
