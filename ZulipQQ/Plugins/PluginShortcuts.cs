using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Avalonia.Input;
using ZulipQQ.Data;

namespace ZulipQQ.Plugins;

/// <summary>
/// 插件的快捷操作，在“设置 &gt; 插件”中统一设置：
/// message_menus 中声明了 <c>"shortcut"</c> 的菜单项绑定到对单条消息的鼠标手势（修饰键 + 按键 + 单击 / 双击，例如 ctrl+left_click、
/// middle_double），menus 中声明了 <c>"shortcut"</c> 的菜单项绑定到键盘快捷键（例如 Ctrl+Shift+T，只在 ZulipQQ 的窗口中有效）。
/// 绑定保存在程序设置 plugin_shortcuts 中：<c>{"键": "手势"}</c>，空字符串表示不绑定；没有保存过的菜单项使用 plugin.json 中的默认值。
/// 一个手势只绑定一个菜单项。
/// </summary>
public static class PluginShortcuts
{
    public const string SettingKey = "plugin_shortcuts";

    private static readonly string[] Buttons = ["left", "middle", "right"];

    // 宿主已经使用的手势：选中消息 / 选择文字、打开资料卡、右键菜单、Shift 扩展选择
    private static readonly HashSet<string> Reserved = ["left_click", "left_double", "right_click", "right_double", "shift+left_click"];

    // ---- 鼠标手势：[ctrl+][alt+][shift+]{left|middle|right}_{click|double} ----

    /// <summary>按下的按键、次数与修饰键对应的手势；按住 Win 键或不是单击 / 双击时为 null。</summary>
    public static string? MouseGesture(MouseButton button, int clickCount, KeyModifiers modifiers)
    {
        var name = button switch
        {
            MouseButton.Left => "left",
            MouseButton.Middle => "middle",
            MouseButton.Right => "right",
            _ => null,
        };
        if (name is null || clickCount is < 1 or > 2 || modifiers.HasFlag(KeyModifiers.Meta))
            return null;
        var prefix = (modifiers.HasFlag(KeyModifiers.Control) ? "ctrl+" : "") + (modifiers.HasFlag(KeyModifiers.Alt) ? "alt+" : "")
                     + (modifiers.HasFlag(KeyModifiers.Shift) ? "shift+" : "");
        return $"{prefix}{name}_{(clickCount == 1 ? "click" : "double")}";
    }

    /// <summary>规范化鼠标手势（ctrl_click、alt_click 等同于 ctrl+left_click、alt+left_click）；格式错误时为 null。</summary>
    public static string? NormalizeMouse(string gesture)
    {
        gesture = gesture switch
        {
            "ctrl_click" => "ctrl+left_click",
            "alt_click" => "alt+left_click",
            _ => gesture,
        };
        var parts = gesture.Split('+');
        var last = parts[^1].Split('_');
        if (last.Length != 2 || !Buttons.Contains(last[0]) || last[1] is not ("click" or "double"))
            return null;
        var mods = parts[..^1];
        if (mods.Any(m => m is not ("ctrl" or "alt" or "shift")) || mods.Distinct().Count() != mods.Length)
            return null;
        var modifiers = (mods.Contains("ctrl") ? KeyModifiers.Control : 0) | (mods.Contains("alt") ? KeyModifiers.Alt : 0)
                        | (mods.Contains("shift") ? KeyModifiers.Shift : 0);
        var button = last[0] switch
        {
            "left" => MouseButton.Left,
            "middle" => MouseButton.Middle,
            _ => MouseButton.Right,
        };
        return MouseGesture(button, last[1] == "click" ? 1 : 2, modifiers);
    }

    /// <summary>可以绑定：格式正确且不是宿主已经使用的手势。</summary>
    public static bool IsAllowedMouse(string gesture) => NormalizeMouse(gesture) is { } g && !Reserved.Contains(g);

    /// <summary>显示用，例如“Ctrl+左键双击”；空字符串为“无”。</summary>
    public static string MouseLabel(string gesture)
    {
        if (NormalizeMouse(gesture) is not { } g)
            return "无";
        var parts = g.Split('+');
        var last = parts[^1].Split('_');
        var mods = parts[..^1].Select(m => m switch
        {
            "ctrl" => "Ctrl+",
            "alt" => "Alt+",
            _ => "Shift+",
        });
        var button = last[0] switch
        {
            "left" => "左键",
            "middle" => "中键",
            _ => "右键",
        };
        return string.Concat(mods) + button + (last[1] == "click" ? "单击" : "双击");
    }

    // ---- 键盘快捷键：Avalonia KeyGesture 的写法，例如 Ctrl+Shift+T ----

    /// <summary>按下的键对应的快捷键；只按了修饰键，或没有 Ctrl / Alt 的普通键（F1～F24 除外）时为 null。</summary>
    public static string? KeyGestureOf(Key key, KeyModifiers modifiers)
    {
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin
            or Key.None or Key.System)
            return null;
        modifiers &= KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Shift;
        if ((modifiers & (KeyModifiers.Control | KeyModifiers.Alt)) == 0 && key is not (>= Key.F1 and <= Key.F24))
            return null;
        return new KeyGesture(key, modifiers).ToString();
    }

    /// <summary>规范化键盘快捷键；格式错误或不能绑定时为 null。</summary>
    public static string? NormalizeKey(string gesture)
    {
        try
        {
            var parsed = KeyGesture.Parse(gesture);
            return KeyGestureOf(parsed.Key, parsed.KeyModifiers);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            return null;
        }
    }

    // ---- 绑定 ----

    /// <summary>保存绑定用的键：消息菜单为“插件 id:菜单 id”，插件菜单为“插件 id:menu:菜单 id”。</summary>
    public static string KeyOf(NativePlugin plugin, PluginMenu menu, bool messageMenu = true) =>
        messageMenu ? $"{plugin.Id}:{menu.Id}" : $"{plugin.Id}:menu:{menu.Id}";

    /// <summary>菜单中可以绑定快捷操作的项（子菜单展开）。</summary>
    public static IEnumerable<PluginMenu> Bindable(IEnumerable<PluginMenu> menus) =>
        menus.SelectMany(m => m.IsGroup ? Bindable(m.Children) : m.Shortcut is not null ? [m] : []);

    public static Dictionary<string, string> Load(SettingRepository settings)
    {
        try
        {
            return settings.Get(SettingKey) is { } json ? JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? [] : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static void Save(SettingRepository settings, IReadOnlyDictionary<string, string> bindings) =>
        settings.Set(SettingKey, JsonSerializer.Serialize(bindings));

    /// <summary>菜单项当前绑定的手势 / 快捷键（规范化后；没有绑定时为空字符串）。</summary>
    public static string Effective(IReadOnlyDictionary<string, string> bindings, NativePlugin plugin, PluginMenu menu, bool messageMenu = true)
    {
        var value = bindings.TryGetValue(KeyOf(plugin, menu, messageMenu), out var saved) ? saved : menu.Shortcut ?? "";
        if (value.Length == 0)
            return "";
        return (messageMenu ? (IsAllowedMouse(value) ? NormalizeMouse(value) : null) : NormalizeKey(value)) ?? "";
    }

    /// <summary>
    /// 绑定到鼠标手势的消息菜单项：按插件顺序取第一个（默认手势相同时前面的插件优先）。只在消息菜单当前显示的插件中查找。
    /// </summary>
    public static (NativePlugin Plugin, PluginMenu Menu)? Find(IEnumerable<NativePlugin> plugins, IReadOnlyDictionary<string, string> bindings,
        string gesture)
    {
        foreach (var plugin in plugins)
        {
            foreach (var menu in Bindable(plugin.ActiveMessageMenus))
            {
                if (Effective(bindings, plugin, menu) == gesture)
                    return (plugin, menu);
            }
        }
        return null;
    }

    /// <summary>绑定到键盘快捷键的插件菜单项（只在已开启的插件中查找）。</summary>
    public static (NativePlugin Plugin, PluginMenu Menu)? FindMenu(IEnumerable<NativePlugin> plugins,
        IReadOnlyDictionary<string, string> bindings, string gesture)
    {
        foreach (var plugin in plugins)
        {
            foreach (var menu in Bindable(plugin.ActiveMenus))
            {
                if (Effective(bindings, plugin, menu, messageMenu: false) == gesture)
                    return (plugin, menu);
            }
        }
        return null;
    }
}
