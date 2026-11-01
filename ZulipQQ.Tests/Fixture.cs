using System.Text.Json;

namespace ZulipQQ.Tests;

/// <summary>读取测试夹具（来自测试服务器的真实响应，敏感字段已替换为 REDACTED）。</summary>
internal static class Fixture
{
    public static string Text(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    public static JsonElement Json(string name)
    {
        using var doc = JsonDocument.Parse(Text(name));
        return doc.RootElement.Clone();
    }
}
