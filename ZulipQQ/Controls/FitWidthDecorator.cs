using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace ZulipQQ.Controls;

/// <summary>
/// 内容比可用宽度宽时按比例缩小（不放大）。对话框都是固定宽度，经典模式的主窗口比它们窄，
/// 缩小后整个对话框可见、按钮可点；高度按缩小后的比例使用可用高度。
/// </summary>
public class FitWidthDecorator : Decorator
{
    private double _scale = 1;

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Child is not { } child)
            return default;
        child.Measure(new Size(double.PositiveInfinity, availableSize.Height));
        var width = child.DesiredSize.Width;
        _scale = width > availableSize.Width && width > 0 ? availableSize.Width / width : 1;
        if (_scale < 1)
            child.Measure(new Size(double.PositiveInfinity, availableSize.Height / _scale));
        else
            child.Measure(availableSize);
        return new Size(child.DesiredSize.Width * _scale, child.DesiredSize.Height * _scale);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Child is not { } child)
            return finalSize;
        child.Arrange(new Rect(0, 0, finalSize.Width / _scale, finalSize.Height / _scale));
        child.RenderTransformOrigin = RelativePoint.TopLeft;
        child.RenderTransform = Math.Abs(_scale - 1) < 0.001 ? null : new ScaleTransform(_scale, _scale);
        return finalSize;
    }
}
