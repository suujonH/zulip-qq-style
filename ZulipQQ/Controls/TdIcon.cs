using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace ZulipQQ.Controls;

/// <summary>
/// 绘制 <see cref="TdIconData"/> 的图标控件。颜色继承自 Foreground，可直接放在按钮内随状态变色。
/// </summary>
public class TdIcon : Control
{
    private const double GridSize = 24;
    private const double StrokeThickness = 2;

    public static readonly StyledProperty<TdIconData?> IconProperty =
        AvaloniaProperty.Register<TdIcon, TdIconData?>(nameof(Icon));

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner<TdIcon>();

    static TdIcon()
    {
        AffectsRender<TdIcon>(IconProperty, ForegroundProperty);
    }

    public TdIconData? Icon
    {
        get => GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) => new(16, 16);

    public override void Render(DrawingContext context)
    {
        var icon = Icon;
        var brush = Foreground;
        if (icon is null || brush is null)
            return;

        var scale = Math.Min(Bounds.Width, Bounds.Height) / GridSize;
        if (scale <= 0)
            return;

        // 非正方形区域时居中绘制
        var offsetX = (Bounds.Width - GridSize * scale) / 2;
        var offsetY = (Bounds.Height - GridSize * scale) / 2;

        using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(offsetX, offsetY)))
        {
            if (icon.FillData is not null)
                context.DrawGeometry(brush, null, icon.FillData);

            if (icon.StrokeData is not null)
                context.DrawGeometry(null, new Pen(brush, StrokeThickness, lineCap: PenLineCap.Square), icon.StrokeData);
        }
    }
}
