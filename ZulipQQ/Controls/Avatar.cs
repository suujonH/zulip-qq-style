using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace ZulipQQ.Controls;

/// <summary>
/// 文字头像：圆形色块 + 名称首字。颜色由名称稳定地映射到 TDesign 色板，同名每次启动颜色一致。
/// </summary>
public class Avatar : Control
{
    private static readonly IBrush[] Palette =
    [
        new SolidColorBrush(Color.Parse("#0052D9")),
        new SolidColorBrush(Color.Parse("#2BA471")),
        new SolidColorBrush(Color.Parse("#E37318")),
        new SolidColorBrush(Color.Parse("#D54941")),
        new SolidColorBrush(Color.Parse("#8E56DD")),
        new SolidColorBrush(Color.Parse("#029CD4")),
        new SolidColorBrush(Color.Parse("#E851B3")),
    ];

    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<Avatar, string?>(nameof(Text));

    public static readonly StyledProperty<FontFamily> FontFamilyProperty =
        TextElement.FontFamilyProperty.AddOwner<Avatar>();

    static Avatar()
    {
        AffectsRender<Avatar>(TextProperty, FontFamilyProperty);
    }

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public FontFamily FontFamily
    {
        get => GetValue(FontFamilyProperty);
        set => SetValue(FontFamilyProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) => new(40, 40);

    public override void Render(DrawingContext context)
    {
        var size = Math.Min(Bounds.Width, Bounds.Height);
        if (size <= 0)
            return;

        var text = Text ?? string.Empty;
        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
        context.DrawEllipse(Palette[StableHash(text) % Palette.Length], null, center, size / 2, size / 2);

        if (text.Length == 0)
            return;

        // 取第一个完整字符（兼容 emoji 等代理对）
        var initial = text[..StringInfo.GetNextTextElementLength(text)].ToUpperInvariant();
        var formatted = new FormattedText(initial, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(FontFamily), size * 0.42, Brushes.White);
        context.DrawText(formatted, new Point(center.X - formatted.Width / 2, center.Y - formatted.Height / 2));
    }

    // string.GetHashCode 每次进程启动都会变化，这里用固定算法保证颜色稳定
    private static int StableHash(string text)
    {
        var hash = 0;
        foreach (var ch in text)
            hash = unchecked(hash * 31 + ch);
        return hash & int.MaxValue;
    }
}
