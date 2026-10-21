using System.Collections.Generic;

namespace ZulipQQ.Data;

/// <summary>插件的启用状态（程序级）。没有记录的插件默认启用。</summary>
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
}
