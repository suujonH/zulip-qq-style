using System;
using Avalonia;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;

namespace ZulipQQ.Controls.MarkdownEditing;

/// <summary>
/// 使用 <see cref="GuardedTextView"/> 的 TextArea。
/// </summary>
internal sealed class GuardedTextArea() : TextArea(new GuardedTextView())
{
    protected override Type StyleKeyOverride => typeof(TextArea);
}

/// <summary>
/// 某一行（例如图片预览）比可见区域高时的滚动：
/// <list type="bullet">
/// <item>要显示的区域比可见区域高时，只显示它的底部（光标与文字在行的底部），可见区域固定在这一行的最下面，
/// 输入、选择时文字始终可见。AvaloniaEdit 会在显示这一行的顶部和底部之间来回滚动。</item>
/// <item>防止重入：AvaloniaEdit 12.0.0 中鼠标抬起会陷入“滚动 → 按指针位置移动光标 → 把光标滚动到可见区域 → 再滚动”的
/// 无限递归而栈溢出；上游在 Caret.BringCaretToView 中加了同样的防重入（AvaloniaEdit issue #596）。</item>
/// </list>
/// </summary>
internal sealed class GuardedTextView : TextView
{
    private bool _makingVisible;

    protected override Type StyleKeyOverride => typeof(TextView);

    public override void MakeVisible(Rect rectangle)
    {
        if (_makingVisible)
            return;
        var viewport = Bounds.Height;
        if (viewport > 0 && rectangle.Height > viewport)
            rectangle = new Rect(rectangle.X, rectangle.Bottom - viewport, rectangle.Width, viewport);
        _makingVisible = true;
        try
        {
            base.MakeVisible(rectangle);
        }
        finally
        {
            _makingVisible = false;
        }
    }
}
