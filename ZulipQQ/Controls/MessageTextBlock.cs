using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace ZulipQQ.Controls;

/// <summary>
/// 可选择文字的段落，支持行内链接：链接按字符区间记录，点击时用文字布局命中测试找到对应目标。
/// 链接与普通文字一样参与换行，长链接不会撑出气泡。
/// </summary>
public class MessageTextBlock : SelectableTextBlock
{
    private static readonly Cursor HandCursor = new(StandardCursorType.Hand);

    private readonly List<(int Start, int End, MessageLinkTarget Target)> _links = [];
    private bool _overLink;

    protected override Type StyleKeyOverride => typeof(SelectableTextBlock);

    /// <summary>登记 [start, end) 区间的文字为链接。</summary>
    internal void AddLink(int start, int end, MessageLinkTarget target)
    {
        if (end > start)
            _links.Add((start, end, target));
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var over = _links.Count > 0 && LinkAt(e.GetPosition(this)) is not null;
        if (over == _overLink)
            return;
        _overLink = over;
        if (over)
            Cursor = HandCursor;
        else
            ClearValue(CursorProperty);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_overLink)
        {
            _overLink = false;
            ClearValue(CursorProperty);
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        // 拖动选择了文字时不当作点击
        if (e.InitialPressMouseButton != MouseButton.Left || SelectionStart != SelectionEnd || _links.Count == 0)
            return;
        if (LinkAt(e.GetPosition(this)) is { } target)
        {
            e.Handled = true;
            RaiseEvent(new MessageLinkEventArgs(MessageContent.LinkClickedEvent, target));
        }
    }

    private MessageLinkTarget? LinkAt(Point point)
    {
        var p = new Point(point.X - Padding.Left, point.Y - Padding.Top);
        var hit = TextLayout.HitTestPoint(in p);
        if (!hit.IsInside)
            return null;
        foreach (var (start, end, target) in _links)
        {
            if (hit.TextPosition >= start && hit.TextPosition < end)
                return target;
        }
        return null;
    }
}
