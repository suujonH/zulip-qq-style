using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using ZulipQQ.Core;

namespace ZulipQQ.Controls;

/// <summary>显示 <see cref="Source"/>，其中与 <see cref="Highlight"/> 相同的部分（不区分大小写）用品牌色显示。</summary>
public class HighlightTextBlock : TextBlock
{
    public static readonly StyledProperty<string?> SourceProperty =
        AvaloniaProperty.Register<HighlightTextBlock, string?>(nameof(Source));

    public static readonly StyledProperty<string?> HighlightProperty =
        AvaloniaProperty.Register<HighlightTextBlock, string?>(nameof(Highlight));

    protected override Type StyleKeyOverride => typeof(TextBlock);

    public string? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public string? Highlight
    {
        get => GetValue(HighlightProperty);
        set => SetValue(HighlightProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SourceProperty || change.Property == HighlightProperty)
            Rebuild();
    }

    private void Rebuild()
    {
        var inlines = new InlineCollection();
        foreach (var (text, isMatch) in HighlightText.Split(Source ?? "", Highlight?.Trim()))
        {
            var run = new Run(text);
            if (isMatch)
                run.Bind(TextElement.ForegroundProperty, this.GetResourceObservable("BrandBrush"));
            inlines.Add(run);
        }
        Inlines = inlines;
    }
}
