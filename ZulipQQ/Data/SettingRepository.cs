using System.Collections.Generic;

namespace ZulipQQ.Data;

/// <summary>
/// 键值设置。<paramref name="accountId"/> 为 null 时读写程序级设置（app_setting），否则读写该账号的设置（account_setting）。
/// </summary>
public sealed class SettingRepository(LocalDb db, long? accountId)
{
    public string? Get(string key)
    {
        var rows = accountId is { } a
            ? db.Query("SELECT value FROM account_setting WHERE account_id = @a AND key = @k;", r => r.GetString(0), ("@a", a), ("@k", key))
            : db.Query("SELECT value FROM app_setting WHERE key = @k;", r => r.GetString(0), ("@k", key));
        return rows.Count > 0 ? rows[0] : null;
    }

    public void Set(string key, string value)
    {
        if (accountId is { } a)
        {
            db.Execute("""
                INSERT INTO account_setting(account_id, key, value) VALUES(@a, @k, @v)
                ON CONFLICT(account_id, key) DO UPDATE SET value = excluded.value;
                """, ("@a", a), ("@k", key), ("@v", value));
        }
        else
        {
            db.Execute("""
                INSERT INTO app_setting(key, value) VALUES(@k, @v)
                ON CONFLICT(key) DO UPDATE SET value = excluded.value;
                """, ("@k", key), ("@v", value));
        }
    }

    public void Remove(string key)
    {
        if (accountId is { } a)
            db.Execute("DELETE FROM account_setting WHERE account_id = @a AND key = @k;", ("@a", a), ("@k", key));
        else
            db.Execute("DELETE FROM app_setting WHERE key = @k;", ("@k", key));
    }

    public Dictionary<string, string> GetAll()
    {
        var rows = accountId is { } a
            ? db.Query("SELECT key, value FROM account_setting WHERE account_id = @a;", r => (r.GetString(0), r.GetString(1)), ("@a", a))
            : db.Query("SELECT key, value FROM app_setting;", r => (r.GetString(0), r.GetString(1)));
        var result = new Dictionary<string, string>();
        foreach (var (k, v) in rows)
            result[k] = v;
        return result;
    }
}
