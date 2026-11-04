using System;
using Avalonia;
using Avalonia.Controls;

namespace ZulipQQ.Controls;

/// <summary>
/// 从左向右重叠排列子元素（投票人头像）：每一个比前一个右移 <see cref="Step"/>，压在前一个的下面。
/// 剩余宽度不足 1.5 个子元素时，后面的子元素不显示（排列为空矩形）。期望宽度为 0，不撑大所在的容器。
/// </summary>
public class OverlapStack : Panel
{
    public static readonly StyledProperty<double> StepProperty =
        AvaloniaProperty.Register<OverlapStack, double>(nameof(Step), 12);

    static OverlapStack()
    {
        AffectsArrange<OverlapStack>(StepProperty);
    }

    /// <summary>相邻两个子元素左边缘的距离。</summary>
    public double Step
    {
        get => GetValue(StepProperty);
        set => SetValue(StepProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var height = 0d;
        foreach (var child in Children)
        {
            child.Measure(Size.Infinity);
            height = Math.Max(height, child.DesiredSize.Height);
        }
        return new Size(0, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var x = 0d;
        var fits = true;
        for (var i = 0; i < Children.Count; i++)
        {
            var child = Children[i];
            var size = child.DesiredSize;
            // 剩余宽度不足 1.5 个头像时不再显示后面的头像，避免最后一个贴着边缘
            fits = fits && x + size.Width * 1.5 <= finalSize.Width;
            // 先出现的在上面
            child.ZIndex = Children.Count - i;
            child.Arrange(fits ? new Rect(x, (finalSize.Height - size.Height) / 2, size.Width, size.Height) : default);
            x += Step;
        }
        return finalSize;
    }
}
