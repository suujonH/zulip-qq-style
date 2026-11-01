using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace ZulipQQ.Controls;

/// <summary>本机设置保存在哪里。</summary>
public enum SettingScope
{
    None,

    /// <summary>ZulipQQ 程序级设置，所有账号共用。</summary>
    App,

    /// <summary>ZulipQQ 账号级设置，只属于该账号。</summary>
    Account,
}

/// <summary>
/// 设置项的来源标签（开发者模式的“显示设置来源”），放在设置项的最下方：
/// <see cref="Zulip"/> 为对应的 Zulip 配置名（多个用空格分隔，每个一个标签），<see cref="Local"/> 为 ZulipQQ 本机设置的范围。
/// 默认隐藏；设置窗口在开启时给根元素加 <c>showSource</c> 类显示，样式见 SettingsPages.axaml。
/// </summary>
public sealed class SettingSource : WrapPanel
{
    public static readonly StyledProperty<string?> ZulipProperty =
        AvaloniaProperty.Register<SettingSource, string?>(nameof(Zulip));

    public static readonly StyledProperty<SettingScope> LocalProperty =
        AvaloniaProperty.Register<SettingSource, SettingScope>(nameof(Local));

    public string? Zulip
    {
        get => GetValue(ZulipProperty);
        set => SetValue(ZulipProperty, value);
    }

    public SettingScope Local
    {
        get => GetValue(LocalProperty);
        set => SetValue(LocalProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ZulipProperty || change.Property == LocalProperty)
            Build();
    }

    private void Build()
    {
        Children.Clear();
        foreach (var name in (Zulip ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            Children.Add(CreateTag("zulip", "Icon.SourceCode", name));
        if (Local != SettingScope.None)
            Children.Add(CreateTag("local", "Icon.Setting", Local == SettingScope.App ? "ZulipQQ 设置 · 全局" : "ZulipQQ 设置 · 账号"));
    }

    private static Border CreateTag(string kind, string icon, string text)
    {
        var border = new Border
        {
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                Children =
                {
                    new TdIcon { IconKey = icon, Width = 12, Height = 12, VerticalAlignment = VerticalAlignment.Center },
                    new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center },
                },
            },
        };
        border.Classes.Add(kind);
        return border;
    }
}
