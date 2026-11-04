using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace ZulipQQ.Controls;

/// <summary>
/// 多选模式中消息左侧的复选框位置：与同一行中气泡（Border.bubble）的垂直中心对齐；
/// 气泡上方的昵称 / 时间行、下方的表情回应等不影响位置。找不到气泡时靠上。
/// 必须放在父面板的子元素中气泡所在元素之后：父面板按顺序排列子元素，排列本控件时气泡的位置已经是新的。
/// </summary>
public sealed class BubbleCheckSlot : Decorator
{
    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Child is not { IsVisible: true } child)
            return finalSize;
        var height = child.DesiredSize.Height;
        var top = 0.0;
        if (Parent is Visual parent && FindBubble(parent) is { } bubble
            && bubble.TranslatePoint(new Point(0, bubble.Bounds.Height / 2), parent) is { } center)
        {
            // 本控件在父面板中从顶部开始（Bounds 可能还是上一次的，用 Margin 计算）
            top = Math.Clamp(center.Y - Margin.Top - height / 2, 0, Math.Max(0, finalSize.Height - height));
        }
        child.Arrange(new Rect(0, top, finalSize.Width, height));
        return finalSize;
    }

    private Border? FindBubble(Visual parent)
    {
        foreach (var v in parent.GetVisualDescendants())
        {
            if (v is Border b && b.Classes.Contains("bubble") && !this.IsVisualAncestorOf(b))
                return b;
        }
        return null;
    }
}
