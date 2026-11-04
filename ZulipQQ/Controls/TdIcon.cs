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

    /// <summary>按资源名（<c>Icon.*</c>）设置图标，用于由数据决定图标的场合（例如按扩展名的文件图标）。</summary>
    public static readonly StyledProperty<string?> IconKeyProperty =
        AvaloniaProperty.Register<TdIcon, string?>(nameof(IconKey));

    /// <summary>
    /// 以中心放大绘制的倍数（不影响布局，可超出自身范围）。线宽只按一半的幅度变化（放大 1.2 倍时线宽 1.1 倍），
    /// 用于悬停弹跳动画：用 RenderTransform 缩放会让线条一起变粗，看起来像是变黑了；线宽完全不变时小图标又几乎看不出动。
    /// </summary>
    public static readonly StyledProperty<double> ZoomProperty =
        AvaloniaProperty.Register<TdIcon, double>(nameof(Zoom), 1);

    private IDisposable? _iconKeyBinding;

    static TdIcon()
    {
        AffectsRender<TdIcon>(IconProperty, ForegroundProperty, ZoomProperty);
    }

    public string? IconKey
    {
        get => GetValue(IconKeyProperty);
        set => SetValue(IconKeyProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != IconKeyProperty)
            return;
        _iconKeyBinding?.Dispose();
        _iconKeyBinding = IconKey is { } key ? Bind(IconProperty, this.GetResourceObservable(key, v => v as TdIconData)) : null;
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

    public double Zoom
    {
        get => GetValue(ZoomProperty);
        set => SetValue(ZoomProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) => new(16, 16);

    public override void Render(DrawingContext context)
    {
        var icon = Icon;
        var brush = Foreground;
        if (icon is null || brush is null)
            return;

        var zoom = Zoom > 0 ? Zoom : 1;
        var scale = Math.Min(Bounds.Width, Bounds.Height) / GridSize * zoom;
        if (scale <= 0)
            return;

        // 非正方形区域时居中绘制（放大时也以中心为准）
        var offsetX = (Bounds.Width - GridSize * scale) / 2;
        var offsetY = (Bounds.Height - GridSize * scale) / 2;

        using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(offsetX, offsetY)))
        {
            if (icon.FillData is not null)
                context.DrawGeometry(brush, null, icon.FillData);

            if (icon.StrokeData is not null)
                context.DrawGeometry(null, new Pen(brush, StrokeThickness * (1 + (zoom - 1) / 2) / zoom, lineCap: PenLineCap.Square), icon.StrokeData);
        }
    }
}
