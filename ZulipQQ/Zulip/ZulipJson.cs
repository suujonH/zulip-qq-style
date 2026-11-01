using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ZulipQQ.Zulip;

/// <summary>Zulip API 使用的 JSON 序列化配置：字段名为蛇形小写。</summary>
public static class ZulipJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DictionaryKeyPolicy = null,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        // 中文等字符原样输出，便于阅读日志；请求参数另行做 URL 编码
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new GroupSettingValueConverter() },
    };

    public static T Deserialize<T>(JsonElement element) =>
        element.Deserialize<T>(Options) ?? throw new JsonException($"无法解析 {typeof(T).Name}");

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
}
