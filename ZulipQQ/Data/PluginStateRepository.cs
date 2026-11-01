using System;
using System.Collections.Generic;

namespace ZulipQQ.Data;

/// <summary>
/// 插件的启用状态与开启确认（程序级）。没有记录的插件默认停用；
/// 只有启用且确认时的指纹与当前文件一致的插件才会加载。
/// </summary>
public sealed class PluginStateRepository(LocalDb db)
{
    public Dictionary<string, bool> LoadAll()
    {
        var result = new Dictionary<string, bool>();
        foreach (var (id, enabled) in db.Query("SELECT plugin_id, enabled FROM plugin_state;", r => (r.GetString(0), r.GetInt64(1) != 0)))
            result[id] = enabled;
        return result;
    }

    public void SetEnabled(string pluginId, bool enabled) =>
        db.Execute("""
            INSERT INTO plugin_state(plugin_id, enabled) VALUES(@id, @e)
            ON CONFLICT(plugin_id) DO UPDATE SET enabled = excluded.enabled;
            """, ("@id", pluginId), ("@e", enabled ? 1 : 0));

    /// <summary>各插件确认开启时的指纹。</summary>
    public Dictionary<string, string> LoadApprovals()
    {
        var result = new Dictionary<string, string>();
        foreach (var (id, fingerprint) in db.Query("SELECT plugin_id, fingerprint FROM plugin_approval;", r => (r.GetString(0), r.GetString(1))))
            result[id] = fingerprint;
        return result;
    }

    /// <summary>用户确认开启：记录指纹并启用。</summary>
    public void Approve(string pluginId, string fingerprint) => db.InTransaction(() =>
    {
        db.Execute("""
            INSERT INTO plugin_approval(plugin_id, fingerprint, approved_at) VALUES(@id, @f, @t)
            ON CONFLICT(plugin_id) DO UPDATE SET fingerprint = excluded.fingerprint, approved_at = excluded.approved_at;
            """, ("@id", pluginId), ("@f", fingerprint), ("@t", DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        SetEnabled(pluginId, true);
    });
}
