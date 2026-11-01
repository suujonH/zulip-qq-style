using System.Text.Json.Nodes;
using ZulipQQ.Data;
using ZulipQQ.Plugins;

namespace ZulipQQ.Tests.Plugins;

/// <summary>插件权限相关：launch 白名单、设置项、附加内容、指纹、设置的加密保存。</summary>
public sealed class PluginPermissionTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("zqplugin").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private const string Manifest = """
        {
          "id": "com.zulipqq.ai",
          "name": "AI",
          "api_version": 3,
          "library": "Ai.dll",
          "permissions": ["read_message", "annotate_message", "launch", "network", "edit_message"],
          "launch": ["todo:", "https://*.google.com/maps", "http:", "C:\\Tools\\todo.exe", "notepad.exe", "a/b"],
          "settings": [
            { "key": "endpoint", "label": "EndPoint" },
            { "key": "api_key", "type": "password" },
            { "key": "level", "type": "select", "default": "x",
              "options": [ { "value": "low", "label": "低" }, { "value": "high" } ] },
            { "key": "on", "type": "bool", "default": true },
            { "key": "bad", "type": "nested" },
            { "key": "conditions", "type": "list", "default": [ { "type": "topic", "topic": "a" } ],
              "fields": [
                { "key": "type", "type": "select", "options": [ { "value": "direct" }, { "value": "topic" } ] },
                { "key": "topic", "visible_when": { "type": "topic" } },
                { "key": "token", "type": "password" }
              ] }
          ]
        }
        """;

    private static PluginManifest Parse(List<string>? warnings = null) =>
        PluginManifest.Parse(Manifest, "com.zulipqq.ai", Zq.ApiVersion, warnings is null ? null : warnings.Add);

    [Fact]
    public void Manifest_ParsesNewPermissionsLaunchAndSettings()
    {
        var warnings = new List<string>();
        var m = Parse(warnings);

        Assert.Equal(["annotate_message", "edit_message", "launch", "network", "read_message"], m.Permissions.Order());
        Assert.Equal(["todo:", "https://*.google.com/maps", "C:\\Tools\\todo.exe", "notepad.exe"], m.Launch.Select(r => r.Pattern));
        Assert.Equal(["endpoint", "api_key", "level", "on", "conditions"], m.Settings.Select(s => s.Key));
        // http: 与 a/b 两条白名单、nested 一个设置项
        Assert.Equal(3, warnings.Count);

        var level = m.Settings[2];
        Assert.Equal(PluginSettingType.Select, level.Type);
        Assert.Equal("high", level.Options[1].Label);
        // 默认值不在选项中：取第一个选项
        Assert.Equal("low", level.DefaultValue().GetValue<string>());
        Assert.True(m.Settings[3].DefaultValue().GetValue<bool>());

        var conditions = m.Settings[4];
        Assert.Equal(PluginSettingType.List, conditions.Type);
        Assert.Equal("type", conditions.Fields[1].VisibleWhen!.Value.Key);
        Assert.Equal(["topic"], conditions.Fields[1].VisibleWhen!.Value.Values);
        var row = (JsonObject)((JsonArray)conditions.DefaultValue())[0]!;
        Assert.Equal("topic", row["type"]!.GetValue<string>());
        Assert.Equal("", row["token"]!.GetValue<string>());
    }

    [Fact]
    public void Manifest_WarnsWhenLaunchWithoutPermission()
    {
        var warnings = new List<string>();
        PluginManifest.Parse("""{"id":"com.a.b","name":"x","api_version":3,"library":"a.dll","launch":["todo:"]}""", "com.a.b", 3, warnings.Add);
        Assert.Single(warnings);
    }

    [Theory]
    [InlineData("todo:", "todo://x.com/a", true)]
    [InlineData("todo:", "TODO:abc", true)]
    [InlineData("todo:", "https://todo.com", false)]
    [InlineData("https://*.google.com/maps", "https://google.com/maps", true)]
    [InlineData("https://*.google.com/maps", "https://www.google.com/maps/place?q=1", true)]
    [InlineData("https://*.google.com/maps", "https://www.google.com/mapsx", false)]
    [InlineData("https://*.google.com/maps", "https://www.google.com/", false)]
    [InlineData("https://*.google.com/maps", "https://evilgoogle.com/maps", false)]
    [InlineData("https://*.google.com/maps", "http://www.google.com/maps", false)]
    [InlineData("https://*.google.com/maps", "https://google.com@evil.com/maps", false)]
    [InlineData("https://zulip.com", "https://zulip.com/help", true)]
    [InlineData("https://zulip.com", "https://www.zulip.com/", false)]
    [InlineData("https://zulip.com", "https://zulip.com:8443/", false)]
    [InlineData("C:\\Tools\\todo.exe", "c:\\tools\\TODO.exe", true)]
    [InlineData("C:\\Tools\\todo.exe", "todo.exe", false)]
    [InlineData("notepad.exe", "notepad.exe", true)]
    [InlineData("notepad.exe", "D:\\evil\\notepad.exe", false)]
    [InlineData("notepad.exe", "file:///C:/Windows/notepad.exe", false)]
    public void LaunchRule_Matches(string pattern, string target, bool expected)
    {
        var rule = LaunchRule.TryParse(pattern, out var error);
        Assert.Null(error);
        Assert.Equal(expected, rule!.Matches(target));
    }

    [Theory]
    [InlineData("https:")]
    [InlineData("file:")]
    [InlineData("javascript:")]
    [InlineData("https://*")]
    [InlineData("tools\\a.exe")]
    public void LaunchRule_RejectsUnsafePatterns(string pattern)
    {
        Assert.Null(LaunchRule.TryParse(pattern, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void Annotation_ParsesAndLimits()
    {
        var a = PluginAnnotation.Parse("p", """{"text":" 你好 ","actions":[{"label":"","target":"https://a.com"},{"label":"x","target":""}]}""", out var error);
        Assert.Null(error);
        Assert.Equal("你好", a!.Text);
        Assert.Equal([new PluginAction("打开", "https://a.com", "")], a.Actions);
        var again = PluginAnnotation.Parse("p", a.ToJson(), out _);
        Assert.Equal(a.Text, again!.Text);
        Assert.Equal(a.Actions, again.Actions);

        Assert.Null(PluginAnnotation.Parse("p", """{"text":""}""", out error));
        Assert.Null(error);
        Assert.Null(PluginAnnotation.Parse("p", "[1]", out error));
        Assert.NotNull(error);
        var many = "{\"actions\":[" + string.Join(",", Enumerable.Repeat("""{"target":"todo:x"}""", 6)) + "]}";
        Assert.Null(PluginAnnotation.Parse("p", many, out error));
        Assert.NotNull(error);
    }

    [Fact]
    public void Fingerprint_ChangesWithAnyFile()
    {
        File.WriteAllText(Path.Combine(_dir, "plugin.json"), "{}");
        File.WriteAllText(Path.Combine(_dir, "a.dll"), "x");
        var first = PluginFingerprint.Compute(_dir);
        Assert.NotNull(first);
        Assert.Equal(first, PluginFingerprint.Compute(_dir));

        File.WriteAllText(Path.Combine(_dir, "a.dll"), "y");
        var modified = PluginFingerprint.Compute(_dir);
        Assert.NotEqual(first, modified);

        Directory.CreateDirectory(Path.Combine(_dir, "sub"));
        File.WriteAllText(Path.Combine(_dir, "sub", "b.dll"), "");
        Assert.NotEqual(modified, PluginFingerprint.Compute(_dir));
        Assert.Equal(19, PluginFingerprint.Short(modified!).Length);
    }

    [Fact]
    public void Settings_EncryptPasswordsAndApplyDefaults()
    {
        using var db = LocalDb.Open(Path.Combine(_dir, "test.db"));
        var repo = new SettingRepository(db, null);
        var m = Parse();

        var defaults = PluginSettings.Load(repo, m);
        Assert.Equal("", defaults["endpoint"]!.GetValue<string>());
        Assert.Single((JsonArray)defaults["conditions"]!);

        var values = new JsonObject
        {
            ["endpoint"] = "https://api",
            ["api_key"] = "secret",
            ["level"] = "high",
            ["unknown"] = "x",
            ["conditions"] = new JsonArray(new JsonObject { ["type"] = "direct", ["token"] = "t#1" }),
        };
        PluginSettings.Save(repo, m, values);

        var stored = repo.Get(PluginSettings.StorageKey(m.Id))!;
        Assert.DoesNotContain("secret", stored);
        // 明文中带有 Base64 不会出现的字符，避免密文碰巧包含明文
        Assert.DoesNotContain("t#1", stored);
        Assert.DoesNotContain("unknown", stored);

        var loaded = PluginSettings.Load(repo, m);
        Assert.Equal("secret", loaded["api_key"]!.GetValue<string>());
        Assert.Equal("high", loaded["level"]!.GetValue<string>());
        Assert.True(loaded["on"]!.GetValue<bool>());
        var row = (JsonObject)((JsonArray)loaded["conditions"]!)[0]!;
        Assert.Equal("t#1", row["token"]!.GetValue<string>());
        Assert.Equal("", row["topic"]!.GetValue<string>());
    }

    [Fact]
    public void Settings_ButtonsTagsAndResources()
    {
        var warnings = new List<string>();
        var m = PluginManifest.Parse("""
            {"id":"com.a.b","name":"x","api_version":3,"library":"a.dll","dependencies":["com.c.d","com.a.b","Bad Id","com.c.d"],
             "settings":[
              {"key":"model","label":"模型","type":"resource","resource":"model","plugin":"com.c.d"},
              {"key":"bad","label":"坏","type":"resource","resource":"Bad Type","plugin":"com.c.d"},
              {"key":"noplugin","label":"无插件","type":"resource","resource":"model"},
              {"key":"other","label":"其他","type":"resource","resource":"model","plugin":"com.e.f"},
              {"key":"test","label":"测试","type":"button"},
              {"key":"rows","label":"行","type":"list","fields":[
                {"key":"name","label":"名称"},{"key":"go","label":"按钮","type":"button"},
                {"key":"models","label":"模型","type":"tags","item_button":"测试"}]}]}
            """, "com.a.b", 3, warnings.Add);
        Assert.Equal(["model", "other", "test", "rows"], m.Settings.Select(f => f.Key));
        Assert.Equal(("model", "com.c.d"), (m.Settings[0].ResourceType, m.Settings[0].ResourcePlugin));
        Assert.Equal(["com.c.d"], m.Dependencies);
        var rows = m.Settings.Single(f => f.Key == "rows");
        Assert.Equal(["name", "go", "models"], rows.Fields.Select(f => f.Key));
        Assert.Equal("测试", rows.Fields[2].ItemButton);
        // 依赖自己、id 不合法；bad 的资源类型不合法、noplugin 没有 plugin；other 的插件不在 dependencies 中
        Assert.Equal(5, warnings.Count);

        var values = PluginSettings.Normalize(m.Settings, new JsonObject
        {
            ["model"] = new JsonObject { ["type"] = "model", ["plugin"] = "com.c.d", ["resource"] = "OpenAI", ["item"] = "gpt", ["x"] = 1 },
            ["test"] = "x",
            ["rows"] = new JsonArray(new JsonObject { ["name"] = "a", ["go"] = "x", ["models"] = new JsonArray(" m1 ", "m1", "", "m2", 3) }),
        });
        Assert.Equal(["model", "other", "rows"], values.Select(p => p.Key));
        Assert.Equal(new PluginResourceSelection("model", "com.c.d", "OpenAI", "gpt"), PluginResourceSelection.From((JsonObject)values["model"]!));
        var row = (JsonObject)((JsonArray)values["rows"]!)[0]!;
        Assert.Equal(["name", "models"], row.Select(p => p.Key));
        Assert.Equal(["m1", "m2"], ((JsonArray)row["models"]!).Select(v => v!.GetValue<string>()));

        // 类型不对、缺少插件、插件与设置项不同时为空对象
        var empty = PluginSettings.Normalize(m.Settings, new JsonObject
        {
            ["model"] = new JsonObject { ["type"] = "model", ["resource"] = "a" },
            ["other"] = new JsonObject { ["type"] = "model", ["plugin"] = "com.c.d", ["resource"] = "a", ["item"] = "b" },
        });
        Assert.Empty((JsonObject)empty["model"]!);
        Assert.Empty((JsonObject)empty["other"]!);
    }

    [Fact]
    public void Resources_ParseAndFeedback()
    {
        var resources = PluginResources.Parse("com.a.b", """
            [{"id":"OpenAI","items":["gpt"," gpt ","",1]},{"id":"OpenAI","name":"重复"},{"id":"","name":"空"},{"id":"b","name":"B"},2]
            """)!;
        Assert.Equal(2, resources.Count);
        Assert.Equal(("OpenAI", "OpenAI"), (resources[0].Id, resources[0].Name));
        Assert.Equal(["gpt"], resources[0].Items);
        Assert.Equal("B", resources[1].Name);
        Assert.Null(PluginResources.Parse("com.a.b", "{}"));
        Assert.Null(PluginResources.Parse("com.a.b", "["));

        var feedback = PluginSettingsFeedback.Parse("""{"key":"models","row":3,"options":["a","a"," b ",""]}""")!;
        Assert.Equal(("models", 3L, (string?)null), (feedback.Key, feedback.Row, feedback.Text));
        Assert.Equal(["a", "b"], feedback.Options!);
        var text = PluginSettingsFeedback.Parse("""{"key":"test","text":"成功"}""")!;
        Assert.Equal(((long?)null, "成功", (IReadOnlyList<string>?)null), (text.Row, text.Text, text.Options));
        Assert.Equal(((string?)null, (bool?)null), (text.Item, text.Ok));
        var failed = PluginSettingsFeedback.Parse("""{"key":"models","item":"gpt","status":"error","text":"HTTP 401：bad"}""")!;
        Assert.Equal(("gpt", (bool?)false, "HTTP 401：bad"), (failed.Item, failed.Ok, failed.Text));
        Assert.True(PluginSettingsFeedback.Parse("""{"key":"models","item":"gpt","status":"ok"}""")!.Ok);
        Assert.Null(PluginSettingsFeedback.Parse("""{"key":"models","item":"gpt","status":"x"}""")!.Ok);
        Assert.Null(PluginSettingsFeedback.Parse("""{"text":"x"}"""));
        Assert.Null(PluginSettingsFeedback.Parse("x"));
    }

    [Fact]
    public void Manifest_ParsesNestedMessageMenus()
    {
        var warnings = new List<string>();
        var m = PluginManifest.Parse("""
            {"id":"com.a.b","name":"x","api_version":3,"library":"a.dll","permissions":["read_message"],
             "menus":[{"id":1,"name":"a"},{"name":"组","children":[{"id":2,"name":"b"}]}],
             "message_menus":[{"id":1,"name":"分析"},
               {"name":"设置","children":[{"name":"工作","children":[{"id":1001,"name":"同事"},{"name":"太深","children":[{"id":9,"name":"x"}]}]},
                                          {"name":"空","children":[]}]}]}
            """, "com.a.b", 3, warnings.Add);
        Assert.Equal([new PluginMenu(1, "a")], m.Menus);
        Assert.Equal(2, m.MessageMenus.Count);
        var group = m.MessageMenus[1];
        Assert.True(group.IsGroup);
        Assert.Equal("设置", group.Name);
        var work = Assert.Single(group.Children);
        Assert.Equal([new PluginMenu(1001, "同事")], work.Children);
        // menus 中的子菜单、嵌套过深的子菜单
        Assert.Equal(2, warnings.Count);
    }

    [Fact]
    public void Manifest_ParsesMessageMenus()
    {
        var warnings = new List<string>();
        var m = PluginManifest.Parse("""
            {"id":"com.a.b","name":"x","api_version":3,"library":"a.dll","permissions":["read_message"],
             "message_menus":[{"id":1,"name":"翻译"},{"id":"x"}]}
            """, "com.a.b", 3, warnings.Add);
        Assert.Equal([new PluginMenu(1, "翻译")], m.MessageMenus);
        Assert.Single(warnings);

        warnings.Clear();
        PluginManifest.Parse("""{"id":"com.a.b","name":"x","api_version":3,"library":"a.dll","message_menus":[{"id":1,"name":"a"}]}""",
            "com.a.b", 3, warnings.Add);
        Assert.Single(warnings);
    }

    [Fact]
    public void Manifest_ParsesShortcutsAndPanels()
    {
        var warnings = new List<string>();
        var m = PluginManifest.Parse("""
            {"id":"com.a.b","name":"x","api_version":3,"library":"a.dll","permissions":["read_message"],
             "menus":[{"id":1,"name":"打开","shortcut":"ctrl+shift+t"},{"id":2,"name":"b","shortcut":"T"},{"id":3,"name":"c","shortcut":"F2"}],
             "message_menus":[{"id":1,"name":"翻译","shortcut":"middle_click"},{"id":2,"name":"b","shortcut":""},
               {"id":3,"name":"c","shortcut":"right_click"},{"id":4,"name":"d"},
               {"name":"组","children":[{"id":5,"name":"e","shortcut":"alt_click"},{"id":6,"name":"f","shortcut":"shift+alt+right_double"}]}],
             "panels":[{"kind":"topic","label":"话题场景"},{"kind":"user","label":"好友身份"},{"kind":"topic","label":"重复"},
               {"kind":"x","label":"y"},{"kind":"user"}]}
            """, "com.a.b", 3, warnings.Add);
        Assert.Equal(["Ctrl+Shift+T", "", "F2"], m.Menus.Select(x => x.Shortcut));
        Assert.Equal(["middle_click", "", "", null], m.MessageMenus.Take(4).Select(x => x.Shortcut));
        Assert.Equal(["alt+left_click", "alt+shift+right_double"], m.MessageMenus[4].Children.Select(x => x.Shortcut));
        Assert.Equal([1, 2, 3, 5, 6], PluginShortcuts.Bindable(m.MessageMenus).Select(x => x.Id));
        Assert.Equal([new PluginPanel(PluginTargetKind.Topic, "话题场景"), new PluginPanel(PluginTargetKind.User, "好友身份")], m.Panels);
        // 不能使用的快捷键 T、宿主使用的 right_click、重复 / 格式错误的面板 3 个
        Assert.Equal(5, warnings.Count);
    }

    [Theory]
    [InlineData(Avalonia.Input.MouseButton.Middle, 1, Avalonia.Input.KeyModifiers.None, "middle_click")]
    [InlineData(Avalonia.Input.MouseButton.Left, 2, Avalonia.Input.KeyModifiers.Control | Avalonia.Input.KeyModifiers.Shift, "ctrl+shift+left_double")]
    [InlineData(Avalonia.Input.MouseButton.Right, 1, Avalonia.Input.KeyModifiers.Alt, "alt+right_click")]
    [InlineData(Avalonia.Input.MouseButton.Left, 3, Avalonia.Input.KeyModifiers.None, null)]
    [InlineData(Avalonia.Input.MouseButton.Left, 1, Avalonia.Input.KeyModifiers.Meta, null)]
    public void Shortcuts_MouseGesture(Avalonia.Input.MouseButton button, int clicks, Avalonia.Input.KeyModifiers modifiers, string? expected) =>
        Assert.Equal(expected, PluginShortcuts.MouseGesture(button, clicks, modifiers));

    [Fact]
    public void Shortcuts_EffectiveBinding()
    {
        var plugin = new NativePlugin(@"plugins\com.a.b", null, null);
        var menu = new PluginMenu(1, "翻译") { Shortcut = "middle_click" };
        var key = PluginShortcuts.KeyOf(plugin, menu);
        Assert.Equal("com.a.b:1", key);
        Assert.Equal("com.a.b:menu:1", PluginShortcuts.KeyOf(plugin, menu, messageMenu: false));
        Assert.Equal("middle_click", PluginShortcuts.Effective(new Dictionary<string, string>(), plugin, menu));
        Assert.Equal("", PluginShortcuts.Effective(new Dictionary<string, string> { [key] = "" }, plugin, menu));
        // ctrl_click 的写法、宿主使用的手势
        Assert.Equal("ctrl+left_click", PluginShortcuts.Effective(new Dictionary<string, string> { [key] = "ctrl_click" }, plugin, menu));
        Assert.Equal("", PluginShortcuts.Effective(new Dictionary<string, string> { [key] = "left_click" }, plugin, menu));
        Assert.False(PluginShortcuts.IsAllowedMouse("shift+left_click"));
        Assert.True(PluginShortcuts.IsAllowedMouse("shift+right_click"));
        Assert.Equal("Ctrl+Alt+右键双击", PluginShortcuts.MouseLabel("alt+ctrl+right_double"));
        Assert.Equal("无", PluginShortcuts.MouseLabel(""));
        Assert.Equal("Ctrl+T", PluginShortcuts.NormalizeKey("ctrl+t"));
        Assert.Null(PluginShortcuts.NormalizeKey("Shift+T"));
        Assert.Null(PluginShortcuts.KeyGestureOf(Avalonia.Input.Key.LeftCtrl, Avalonia.Input.KeyModifiers.Control));
    }

    [Fact]
    public void Panel_ParsesFieldsAndEntries()
    {
        var fields = PluginPanelField.Parse(JsonNode.Parse("""
            {"fields":[{"key":"scene","label":"场景","type":"select","options":[{"value":"","label":"默认"},{"value":"work","label":"工作"},
                         {"value":"work","label":"重复"},{"value":"x"}],"value":"work"},
                       {"key":"on","label":"开关","type":"bool","value":true},
                       {"key":"empty","label":"没有选项","type":"select","options":[]},
                       {"key":"scene","label":"重复","type":"bool"},{"key":"t","label":"文字","type":"text"}]}
            """) as JsonObject);
        Assert.Equal(2, fields.Count);
        Assert.Equal([new PluginPanelOption("", "默认"), new PluginPanelOption("work", "工作")], fields[0].Options);
        Assert.Equal("work", fields[0].SelectedValue);
        Assert.True(fields[1] is { IsBool: true, IsChecked: true });
        Assert.Empty(PluginPanelField.Parse(null));

        var entries = PluginPanelEntry.Parse(JsonNode.Parse("""
            {"entries":[{"target":{"account":1,"stream":2,"topic":"发布","name":"开发 / 发布"},"text":"工作"},
                        {"target":{"account":1,"stream":2}},{"text":"x"}]}
            """) as JsonObject, PluginTargetKind.Topic);
        var entry = Assert.Single(entries);
        Assert.True(entry.Target.SameAs(PluginTarget.ForTopic(1, 2, "发布", "")));
        Assert.Equal("工作", entry.Text);

        var request = PluginPanel.Request("set", PluginTargetKind.User, PluginTarget.ForUser(1, 9, "张三"), "identity", "work/peer");
        Assert.Equal("""{"action":"set","kind":"user","target":{"account":1,"user":9,"name":"张三"},"key":"identity","value":"work/peer"}""",
            request.ToJsonString(new System.Text.Json.JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        Assert.Equal("""{"action":"list","kind":"topic"}""", PluginPanel.Request("list", PluginTargetKind.Topic).ToJsonString());
    }

    [Fact]
    public void Annotation_ParsesReplaceAndFill()
    {
        var a = PluginAnnotation.Parse("p", """{"replace":" 译文 ","actions":[{"fill":"好的"},{"label":"打开","target":"todo:x"}]}""", out var error);
        Assert.Null(error);
        Assert.Equal("译文", a!.Replace);
        Assert.Equal("", a.Text);
        Assert.Equal([new PluginAction("填入输入框", "", "", "好的"), new PluginAction("打开", "todo:x", "")], a.Actions);
        Assert.True(a.Actions[0].IsFill);
        Assert.True(a.HasCard);

        var again = PluginAnnotation.Parse("p", a.ToJson(), out _);
        Assert.Equal(a.Replace, again!.Replace);
        Assert.Equal(a.Actions, again.Actions);

        var onlyReplace = PluginAnnotation.Parse("p", """{"replace":"x"}""", out _);
        Assert.False(onlyReplace!.HasCard);
    }

    [Fact]
    public void Catalog_CoversManifestPermissions()
    {
        Assert.All(PluginManifest.EventPermissions.Values, p => Assert.True(PermissionCatalog.IsKnown(p)));
        Assert.Equal(PermissionKind.Declared, PermissionCatalog.Get("network")!.Kind);
    }
}
