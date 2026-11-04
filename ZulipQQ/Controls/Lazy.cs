using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Metadata;

namespace ZulipQQ.Controls;

/// <summary>
/// 按需创建的内容：<see cref="When"/> 第一次为 true 时才用 <see cref="Template"/> 创建子控件，之后只切换显示。
/// 用于消息模板中很少显示的部分（编辑版本、重发按钮等）：消息列表每次创建气泡时不必创建、设置样式和绑定它们。
/// </summary>
public class Lazy : Decorator
{
    public static readonly StyledProperty<bool> WhenProperty =
        AvaloniaProperty.Register<Lazy, bool>(nameof(When));

    public bool When
    {
        get => GetValue(WhenProperty);
        set => SetValue(WhenProperty, value);
    }

    /// <summary>子控件的模板；数据为本控件的 DataContext（子控件同样继承它）。</summary>
    [Content]
    public IDataTemplate? Template { get; set; }

    static Lazy()
    {
        // 没有内容时不占位
        IsVisibleProperty.OverrideDefaultValue<Lazy>(false);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != WhenProperty)
            return;
        if (When && Child is null && Template is not null)
            Child = Template.Build(DataContext);
        IsVisible = When;
    }
}
