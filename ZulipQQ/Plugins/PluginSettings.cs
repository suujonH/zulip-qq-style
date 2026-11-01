using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ZulipQQ.Data;

namespace ZulipQQ.Plugins;

/// <summary>
/// 插件设置的值：保存在程序级设置 <c>plugin_settings:&lt;id&gt;</c> 中（JSON 对象），密码类的值用 DPAPI 加密。
/// 插件通过 get_settings 读取解密后的 JSON，不能修改。
/// </summary>
public static class PluginSettings
{
    private const string EncryptedPrefix = "dpapi:";

    public static string StorageKey(string pluginId) => "plugin_settings:" + pluginId;

    /// <summary>读取（解密、补全默认值）。没有保存过时为全部默认值。</summary>
    public static JsonObject Load(SettingRepository settings, PluginManifest manifest)
    {
        JsonObject? saved = null;
        if (settings.Get(StorageKey(manifest.Id)) is { } json)
        {
            try
            {
                saved = JsonNode.Parse(json) as JsonObject;
            }
            catch (JsonException)
            {
            }
        }
        var result = Normalize(manifest.Settings, saved);
        Transform(manifest.Settings, result, Decrypt);
        return result;
    }

    /// <summary>保存（按清单整理后加密）。</summary>
    public static void Save(SettingRepository settings, PluginManifest manifest, JsonObject values)
    {
        var stored = Normalize(manifest.Settings, values);
        Transform(manifest.Settings, stored, Encrypt);
        settings.Set(StorageKey(manifest.Id), stored.ToJsonString());
    }

    /// <summary>只保留清单中的项，类型不对的取默认值。</summary>
    public static JsonObject Normalize(IReadOnlyList<PluginSettingField> fields, JsonObject? values)
    {
        var result = new JsonObject();
        foreach (var f in fields.Where(f => f.Type != PluginSettingType.Button))
            result[f.Key] = values is not null && values.TryGetPropertyValue(f.Key, out var v) ? f.Normalize(v) : f.DefaultValue();
        return result;
    }

    // 对所有密码项（含列表中的）的值做转换
    private static void Transform(IReadOnlyList<PluginSettingField> fields, JsonObject values, Func<string, string> transform)
    {
        foreach (var f in fields)
        {
            if (f.Type == PluginSettingType.Password && values[f.Key] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0)
                values[f.Key] = transform(s);
            else if (f.Type == PluginSettingType.List && values[f.Key] is JsonArray rows)
            {
                foreach (var row in rows)
                {
                    if (row is JsonObject obj)
                        Transform(f.Fields, obj, transform);
                }
            }
        }
    }

    private static string Encrypt(string plain) =>
        EncryptedPrefix + Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), null, DataProtectionScope.CurrentUser));

    // 无法解密（换了 Windows 账户）时为空
    private static string Decrypt(string stored)
    {
        if (!stored.StartsWith(EncryptedPrefix, StringComparison.Ordinal))
            return stored;
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(stored[EncryptedPrefix.Length..]), null,
                DataProtectionScope.CurrentUser));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return "";
        }
    }
}

/// <summary>
/// 插件通过 set_settings_feedback 回复设置窗口：<c>{"key": "设置项", "row": 行号, "text": "文字", "options": ["候选"]}</c>。
/// row 为 zq_on_settings_button 收到的行号（顶层的设置项不写）；text 显示在设置项下方，options 为 tags 设置项的候选。
/// </summary>
public sealed record PluginSettingsFeedback(string Key, long? Row, string? Text, IReadOnlyList<string>? Options)
{
    /// <summary>tags 某一项的按钮（item_button）的结果：<see cref="Item"/> 为该项，<see cref="Ok"/> 为成功或失败，<see cref="Text"/> 为失败的原因。</summary>
    public string? Item { get; init; }

    /// <summary>item 按钮的结果；没有 status 时为 null。</summary>
    public bool? Ok { get; init; }

    /// <summary>格式错误或没有 key 时返回 null。</summary>
    public static PluginSettingsFeedback? Parse(string? json)
    {
        JsonObject? obj;
        try
        {
            obj = string.IsNullOrEmpty(json) ? null : JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
        if (obj?["key"] is not JsonValue k || !k.TryGetValue<string>(out var key) || key.Length == 0)
            return null;
        long? row = obj["row"] is JsonValue r && r.TryGetValue<long>(out var n) ? n : null;
        var text = obj["text"] is JsonValue t && t.TryGetValue<string>(out var s) ? s : null;
        var options = obj["options"] is JsonArray a
            ? a.OfType<JsonValue>().Select(v => v.TryGetValue<string>(out var o) ? o.Trim() : "").Where(o => o.Length > 0).Distinct().ToList()
            : null;
        var item = obj["item"] is JsonValue i && i.TryGetValue<string>(out var itemText) && itemText.Length > 0 ? itemText : null;
        bool? ok = obj["status"] is JsonValue st && st.TryGetValue<string>(out var status)
            ? status switch { "ok" => true, "error" => false, _ => null }
            : null;
        return new PluginSettingsFeedback(key, row, text, options) { Item = item, Ok = ok };
    }
}
