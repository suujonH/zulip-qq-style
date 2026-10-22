using ZulipQQ.Plugins;

namespace ZulipQQ.Tests.Plugins;

public class PluginManifestTests
{
    private const string Valid = """
        {
          "id": "com.zulipqq.keyword-filter",
          "name": "关键词过滤",
          "version": "1.0.0",
          "author": "ZulipQQ",
          "description": "按关键词屏蔽消息",
          "api_version": 1,
          "library": "KeywordFilter.dll",
          "permissions": ["read_message", "modify_message", "fly"],
          "events": { "on_message": 30000, "filter_incoming": 10000, "on_fly": 20000 },
          "menus": [ { "id": 1, "name": "打开配置目录" }, { "id": "x" } ]
        }
        """;

    [Fact]
    public void ParsesValidManifest()
    {
        var warnings = new List<string>();
        var m = PluginManifest.Parse(Valid, "com.zulipqq.keyword-filter", 1, warnings.Add);

        Assert.Equal("com.zulipqq.keyword-filter", m.Id);
        Assert.Equal("关键词过滤", m.Name);
        Assert.Equal("1.0.0", m.Version);
        Assert.Equal("KeywordFilter.dll", m.Library);
        Assert.Equal(["modify_message", "read_message"], m.Permissions.Order());
        Assert.Equal(30000, m.Events["on_message"]);
        Assert.Equal(10000, m.Events["filter_incoming"]);
        Assert.False(m.Events.ContainsKey("on_fly"));
        Assert.Equal([new PluginMenu(1, "打开配置目录")], m.Menus);
        // 未知权限、未知事件、格式错误的菜单各一条警告
        Assert.Equal(3, warnings.Count);
    }

    [Theory]
    [InlineData("\"id\": \"com.zulipqq.keyword-filter\"", "\"id\": \"Bad Id\"")]
    [InlineData("\"id\": \"com.zulipqq.keyword-filter\"", "\"id\": \"ab\"")]
    [InlineData("\"api_version\": 1", "\"api_version\": 2")]
    [InlineData("\"api_version\": 1", "\"api_version\": 0")]
    [InlineData("\"api_version\": 1,", "")]
    [InlineData("\"KeywordFilter.dll\"", "\"..\\KeywordFilter.dll\"")]
    [InlineData("\"KeywordFilter.dll\"", "\"sub/KeywordFilter.dll\"")]
    [InlineData("\"KeywordFilter.dll\"", "\"C:KeywordFilter.dll\"")]
    [InlineData("\"name\": \"关键词过滤\",", "")]
    [InlineData("\"on_message\": 30000", "\"on_message\": 9999")]
    [InlineData("\"on_message\": 30000", "\"on_message\": 40001")]
    [InlineData("\"on_message\": 30000", "\"on_message\": \"high\"")]
    public void RejectsInvalidManifest(string from, string to) =>
        Assert.Throws<FormatException>(() => PluginManifest.Parse(Valid.Replace(from, to), "com.zulipqq.keyword-filter", 1));

    [Fact]
    public void RejectsIdDifferentFromDirectory() =>
        Assert.Throws<FormatException>(() => PluginManifest.Parse(Valid, "other-dir", 1));

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    public void RejectsMalformedJson(string json) =>
        Assert.Throws<FormatException>(() => PluginManifest.Parse(json, "x", 1));

    [Fact]
    public void IgnoresNonStringPermissions()
    {
        var json = Valid.Replace("\"fly\"", "42");
        var m = PluginManifest.Parse(json, "com.zulipqq.keyword-filter", 1);
        Assert.Equal(2, m.Permissions.Count);
    }
}
