using System;
using System.Collections.Generic;
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
    long LastLoginAt);

public sealed class AccountRepository(LocalDb db)
{
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

    public void Delete(long id) => db.Execute("DELETE FROM account WHERE id = @id;", ("@id", id));

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
