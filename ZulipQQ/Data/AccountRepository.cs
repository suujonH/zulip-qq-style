using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace ZulipQQ.Data;

/// <summary>保存的登录账号。<see cref="ApiKey"/> 是 DPAPI（当前用户）加密后的 UTF-8。</summary>
public sealed record AccountRow(
    long Id,
    string RealmUrl,
    string Email,
    long UserId,
    string FullName,
    string? AvatarUrl,
    byte[] ApiKey,
    bool AllowInvalidCert,
    bool AutoLogin,
    long LastLoginAt)
{
    /// <summary>保存了 key（删除登录状态后为空，需要重新输入密码）。</summary>
    public bool HasKey => ApiKey.Length > 0;
}

public sealed class AccountRepository(LocalDb db)
{
    // 账号设置（account_setting）中的“启动”：为 0 时启动程序不登录该账号；没有保存时为开启
    private const string LoginOnStartupKey = "login_on_startup";

    private const string Columns =
        "id, realm_url, email, user_id, full_name, avatar_url, api_key, allow_invalid_cert, auto_login, last_login_at";

    /// <summary>按最近登录时间降序。</summary>
    public List<AccountRow> GetAll() =>
        db.Query($"SELECT {Columns} FROM account ORDER BY last_login_at DESC, id DESC;", Map);

    public AccountRow? Find(string realmUrl, string email)
    {
        var rows = db.Query($"SELECT {Columns} FROM account WHERE realm_url = @realm AND email = @email;", Map,
            ("@realm", realmUrl), ("@email", email));
        return rows.Count > 0 ? rows[0] : null;
    }

    public AccountRow? Get(long id)
    {
        var rows = db.Query($"SELECT {Columns} FROM account WHERE id = @id;", Map, ("@id", id));
        return rows.Count > 0 ? rows[0] : null;
    }

    /// <summary>按 (realm_url, email) 插入或更新，返回行 id。</summary>
    public long Upsert(AccountRow row)
    {
        db.Execute("""
            INSERT INTO account(realm_url, email, user_id, full_name, avatar_url, api_key, allow_invalid_cert, auto_login, last_login_at)
            VALUES(@realm, @email, @uid, @name, @avatar, @key, @cert, @auto, @last)
            ON CONFLICT(realm_url, email) DO UPDATE SET
              user_id = excluded.user_id, full_name = excluded.full_name, avatar_url = excluded.avatar_url,
              api_key = excluded.api_key, allow_invalid_cert = excluded.allow_invalid_cert,
              auto_login = excluded.auto_login, last_login_at = excluded.last_login_at;
            """,
            ("@realm", row.RealmUrl), ("@email", row.Email), ("@uid", row.UserId), ("@name", row.FullName),
            ("@avatar", row.AvatarUrl), ("@key", row.ApiKey), ("@cert", row.AllowInvalidCert ? 1 : 0),
            ("@auto", row.AutoLogin ? 1 : 0), ("@last", row.LastLoginAt));
        return db.Scalar<long>("SELECT id FROM account WHERE realm_url = @realm AND email = @email;",
            ("@realm", row.RealmUrl), ("@email", row.Email));
    }

    /// <summary>register 成功后用服务器返回的资料更新，并记录登录时间。</summary>
    public void UpdateProfile(long id, long userId, string fullName, string? avatarUrl, long lastLoginAt) =>
        db.Execute("UPDATE account SET user_id = @uid, full_name = @name, avatar_url = @avatar, last_login_at = @last WHERE id = @id;",
            ("@id", id), ("@uid", userId), ("@name", fullName), ("@avatar", avatarUrl), ("@last", lastLoginAt));

    public void SetAutoLogin(long id, bool autoLogin) =>
        db.Execute("UPDATE account SET auto_login = @auto WHERE id = @id;", ("@id", id), ("@auto", autoLogin ? 1 : 0));

    public void SetAllowInvalidCert(long id, bool allow) =>
        db.Execute("UPDATE account SET allow_invalid_cert = @cert WHERE id = @id;", ("@id", id), ("@cert", allow ? 1 : 0));

    /// <summary>主窗口账号列表中的账号（登录中的账号），按加入列表的顺序；第一个是主账号。</summary>
    public List<AccountRow> GetListed() =>
        db.Query($"SELECT {Columns} FROM account WHERE list_order IS NOT NULL ORDER BY list_order, id;", Map);

    /// <summary>登录成功：不在列表中时加到列表末尾，已在列表中时位置不变。</summary>
    public void AddToList(long id) =>
        db.Execute("""
            UPDATE account SET list_order = (SELECT COALESCE(MAX(list_order), 0) + 1 FROM account)
            WHERE id = @id AND list_order IS NULL;
            """, ("@id", id));

    /// <summary>退出账号：移出列表，下次启动不再登录。</summary>
    public void RemoveFromList(long id) => db.Execute("UPDATE account SET list_order = NULL WHERE id = @id;", ("@id", id));

    public void Delete(long id) => db.Execute("DELETE FROM account WHERE id = @id;", ("@id", id));

    /// <summary>“启动”（启用）：开启时登录该账号，启动程序时也登录（账号需在列表中且保存了 key）；没有保存时为开启。</summary>
    public bool IsLoginOnStartup(long id) => new SettingRepository(db, id).Get(LoginOnStartupKey) != "0";

    public void SetLoginOnStartup(long id, bool enabled) =>
        new SettingRepository(db, id).Set(LoginOnStartupKey, enabled ? "1" : "0");

    /// <summary>启动程序时登录的账号：列表中开启了“启动”且保存了 key 的账号。没有时显示登录窗。</summary>
    public List<AccountRow> GetStartupAccounts() => [.. GetListed().Where(a => a.HasKey && IsLoginOnStartup(a.Id))];

    /// <summary>删除登录状态：清除保存的 key 并关闭自动登录；服务器地址与邮箱保留，下次登录时关联原来的本地数据。</summary>
    public void ClearKey(long id) =>
        db.Execute("UPDATE account SET api_key = @key, auto_login = 0 WHERE id = @id;", ("@id", id), ("@key", Array.Empty<byte>()));

    /// <summary>删除账号及其所有本地数据：所有带 account_id 列的表中该账号的行，以及账号本身。</summary>
    public void DeleteWithData(long id) => db.InTransaction(() =>
    {
        // 按列名找表：之后新增的按账号保存的表也会被清理
        var tables = db.Query("SELECT name FROM sqlite_master WHERE type = 'table';", r => r.GetString(0))
            .Where(t => db.Query($"PRAGMA table_info(\"{t}\");", r => r.GetString(1)).Contains("account_id"));
        foreach (var table in tables.ToList())
            db.Execute($"DELETE FROM \"{table}\" WHERE account_id = @id;", ("@id", id));
        Delete(id);
    });

    public static byte[] ProtectKey(string apiKey) =>
        ProtectedData.Protect(Encoding.UTF8.GetBytes(apiKey), null, DataProtectionScope.CurrentUser);

    /// <exception cref="CryptographicException">不是当前 Windows 用户加密的数据。</exception>
    public static string UnprotectKey(byte[] data) =>
        Encoding.UTF8.GetString(ProtectedData.Unprotect(data, null, DataProtectionScope.CurrentUser));

    private static AccountRow Map(SqliteDataReader r) => new(
        r.GetInt64(0),
        r.GetString(1),
        r.GetString(2),
        r.GetInt64(3),
        r.GetString(4),
        r.IsDBNull(5) ? null : r.GetString(5),
        (byte[])r.GetValue(6),
        r.GetInt64(7) != 0,
        r.GetInt64(8) != 0,
        r.GetInt64(9));
}
