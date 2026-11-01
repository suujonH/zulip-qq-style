using System.Text.Json;
using System.Text.Json.Nodes;

namespace PluginShared;

/// <summary>读取 JSON 的小工具。</summary>
internal static class JsonUtil
{
    public static string Str(JsonObject obj, string name) => obj[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";

    /// <summary>从回复中取出 JSON 对象（允许包在 ```json 代码块中或前后有文字）；没有时返回 null。</summary>
    public static JsonObject? ExtractJson(string reply)
    {
        var start = reply.IndexOf('{');
        var end = reply.LastIndexOf('}');
        if (start < 0 || end <= start)
            return null;
        try
        {
            return JsonNode.Parse(reply[start..(end + 1)]) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
