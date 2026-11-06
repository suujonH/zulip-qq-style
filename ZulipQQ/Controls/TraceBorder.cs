using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace ZulipQQ.Controls;

/// <summary>
/// 沿圆角矩形边缘描出的边框：从左下角开始向上，顺时针绕一圈；<see cref="Progress"/> 为已描出的比例（0～1）。
/// 贴表情的动画用（仿 QQ），盖在消息气泡上。
/// </summary>
public sealed class TraceBorder : Control
{
    public static readonly StyledProperty<double> ProgressProperty =
        AvaloniaProperty.Register<TraceBorder, double>(nameof(Progress));

    public static readonly StyledProperty<IBrush?> StrokeProperty =
        AvaloniaProperty.Register<TraceBorder, IBrush?>(nameof(Stroke));

    public static readonly StyledProperty<double> StrokeThicknessProperty =
        AvaloniaProperty.Register<TraceBorder, double>(nameof(StrokeThickness), 2);

    public static readonly StyledProperty<double> RadiusProperty =
        AvaloniaProperty.Register<TraceBorder, double>(nameof(Radius), 8);

    static TraceBorder()
    {
        AffectsRender<TraceBorder>(ProgressProperty, StrokeProperty, StrokeThicknessProperty, RadiusProperty);
        IsHitTestVisibleProperty.OverrideDefaultValue<TraceBorder>(false);
    }

    public double Progress
    {
        get => GetValue(ProgressProperty);
        set => SetValue(ProgressProperty, value);
    }

    public IBrush? Stroke
    {
        get => GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public double StrokeThickness
    {
        get => GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    public double Radius
    {
        get => GetValue(RadiusProperty);
        set => SetValue(RadiusProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var progress = Math.Clamp(Progress, 0, 1);
        if (Stroke is not { } stroke || progress <= 0)
            return;
        // 描边画在边框线的中心：向内收半个线宽，整条线都在控件内
        var half = StrokeThickness / 2;
        var w = Bounds.Width - StrokeThickness;
        var h = Bounds.Height - StrokeThickness;
        if (w <= 0 || h <= 0)
            return;
        var r = Math.Min(Radius, Math.Min(w, h) / 2);
        var corner = new Size(r, r);

        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            // 左下角（圆角上方）开始：左边向上 → 上边 → 右边向下 → 下边向左，回到起点
            g.BeginFigure(new Point(half, half + h - r), false);
            g.LineTo(new Point(half, half + r));
            g.ArcTo(new Point(half + r, half), corner, 0, false, SweepDirection.Clockwise);
            g.LineTo(new Point(half + w - r, half));
            g.ArcTo(new Point(half + w, half + r), corner, 0, false, SweepDirection.Clockwise);
            g.LineTo(new Point(half + w, half + h - r));
            g.ArcTo(new Point(half + w - r, half + h), corner, 0, false, SweepDirection.Clockwise);
            g.LineTo(new Point(half + r, half + h));
            g.ArcTo(new Point(half, half + h - r), corner, 0, false, SweepDirection.Clockwise);
            g.EndFigure(false);
        }

        // 只画出前 progress 的长度：一段实线加一段足够长的空白（虚线长度以线宽为单位）
        var perimeter = 2 * (w + h) - 8 * r + 2 * Math.PI * r;
        var pen = new Pen(stroke, StrokeThickness, lineCap: PenLineCap.Round)
        {
            DashStyle = progress >= 1 ? null : new DashStyle([perimeter * progress / StrokeThickness, perimeter / StrokeThickness * 2], 0),
        };
        context.DrawGeometry(null, pen, geometry);
    }
}
