using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Data.Sqlite;

namespace ZulipQQ.Data;

/// <summary>
/// 本地 SQLite 数据库，只保存 Zulip 没有的数据。所有调用都在 UI 线程上同步执行（数据量很小）。
/// </summary>
public sealed class LocalDb : IDisposable
{
    /// <summary>迁移脚本，下标 + 1 即版本号。已发布的脚本不能修改，只能追加。</summary>
    private static readonly string[] Migrations =
    [
        """
        CREATE TABLE account(
          id INTEGER PRIMARY KEY AUTOINCREMENT,
          realm_url TEXT NOT NULL,
          email TEXT NOT NULL,
          user_id INTEGER NOT NULL,
          full_name TEXT NOT NULL,
          avatar_url TEXT,
          api_key BLOB NOT NULL,
          allow_invalid_cert INTEGER NOT NULL DEFAULT 0,
          auto_login INTEGER NOT NULL DEFAULT 0,
          last_login_at INTEGER NOT NULL,
          UNIQUE(realm_url, email));
        CREATE TABLE app_setting(key TEXT PRIMARY KEY, value TEXT NOT NULL);
        CREATE TABLE account_setting(account_id INTEGER NOT NULL, key TEXT NOT NULL, value TEXT NOT NULL,
          PRIMARY KEY(account_id, key));
        CREATE TABLE conversation_state(
          account_id INTEGER NOT NULL,
          conv_key TEXT NOT NULL,
          title_cache TEXT NOT NULL DEFAULT '',
          pinned_at INTEGER,
          dnd_level INTEGER,
          in_list INTEGER NOT NULL DEFAULT 0,
          remark TEXT,
          last_message_id INTEGER NOT NULL DEFAULT 0,
          last_message_time INTEGER NOT NULL DEFAULT 0,
          last_sender_id INTEGER NOT NULL DEFAULT 0,
          last_preview TEXT NOT NULL DEFAULT '',
          PRIMARY KEY(account_id, conv_key));
        CREATE TABLE contact_group(
          id INTEGER PRIMARY KEY AUTOINCREMENT,
          account_id INTEGER NOT NULL,
          name TEXT NOT NULL,
          sort_order INTEGER NOT NULL);
        CREATE TABLE contact_group_member(
          account_id INTEGER NOT NULL, user_id INTEGER NOT NULL, group_id INTEGER NOT NULL,
          PRIMARY KEY(account_id, user_id));
        CREATE TABLE user_remark(account_id INTEGER NOT NULL, user_id INTEGER NOT NULL, remark TEXT NOT NULL,
          PRIMARY KEY(account_id, user_id));
        CREATE TABLE downloaded_file(
          account_id INTEGER NOT NULL,
          upload_path TEXT NOT NULL,
          local_path TEXT NOT NULL,
          file_name TEXT NOT NULL,
          size INTEGER NOT NULL,
          message_id INTEGER NOT NULL,
          conv_key TEXT NOT NULL,
          downloaded_at INTEGER NOT NULL,
          last_opened_at INTEGER,
          PRIMARY KEY(account_id, upload_path));
        CREATE TABLE local_draft(account_id INTEGER NOT NULL, conv_key TEXT NOT NULL, content TEXT NOT NULL,
          updated_at INTEGER NOT NULL, PRIMARY KEY(account_id, conv_key));
        CREATE TABLE plugin_state(plugin_id TEXT PRIMARY KEY, enabled INTEGER NOT NULL);
        """,
    ];

    private readonly SqliteConnection _connection;
    private SqliteTransaction? _transaction;

    private LocalDb(SqliteConnection connection)
    {
        _connection = connection;
    }

    /// <summary>程序使用的数据库，由 App 在启动时打开。</summary>
    public static LocalDb? Instance { get; set; }

    public static int SchemaVersion => Migrations.Length;

    /// <summary>打开数据库并执行尚未执行的迁移。</summary>
    public static LocalDb Open(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Pooling = false,
        }.ToString());
        connection.Open();

        var db = new LocalDb(connection);
        db.Execute("PRAGMA journal_mode=WAL;");
        db.Execute("PRAGMA foreign_keys=ON;");
        db.Migrate();
        return db;
    }

    public int UserVersion => (int)Scalar<long>("PRAGMA user_version;");

    public int Execute(string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Create(sql, parameters);
        return command.ExecuteNonQuery();
    }

    public List<T> Query<T>(string sql, Func<SqliteDataReader, T> map, params (string Name, object? Value)[] parameters)
    {
        using var command = Create(sql, parameters);
        using var reader = command.ExecuteReader();
        var result = new List<T>();
        while (reader.Read())
            result.Add(map(reader));
        return result;
    }

    /// <summary>第一行第一列；没有结果或为 NULL 时返回 default。</summary>
    public T? Scalar<T>(string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Create(sql, parameters);
        var value = command.ExecuteScalar();
        if (value is null or DBNull)
            return default;
        return (T)Convert.ChangeType(value, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>在事务中执行；嵌套调用时只有最外层开启事务。</summary>
    public void InTransaction(Action action)
    {
        if (_transaction is not null)
        {
            action();
            return;
        }

        using var transaction = _connection.BeginTransaction();
        _transaction = transaction;
        try
        {
            action();
            transaction.Commit();
        }
        finally
        {
            _transaction = null;
        }
    }

    public void Dispose() => _connection.Dispose();

    private SqliteCommand Create(string sql, (string Name, object? Value)[] parameters)
    {
        var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = _transaction;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }

    private void Migrate()
    {
        for (var version = UserVersion; version < Migrations.Length; version++)
        {
            var target = version + 1;
            InTransaction(() =>
            {
                Execute(Migrations[target - 1]);
                Execute($"PRAGMA user_version = {target};");
            });
        }
    }
}
