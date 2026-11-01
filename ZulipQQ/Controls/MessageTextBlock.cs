using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Input.Platform;
using ZulipQQ.Core;
using ZulipQQ.Services;

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

    // 段落文字（行内控件为一个 ￼）与各段的来源，复制时转回 Markdown
    private readonly StringBuilder _source = new();
    private readonly List<InlinePiece> _pieces = [];

    protected override Type StyleKeyOverride => typeof(SelectableTextBlock);

    /// <summary>标题段落的 Markdown 前缀（如 “## ”）：从段落开头复制时加上。</summary>
    internal string? MarkdownPrefix { get; set; }

    /// <summary>列表项的第一个段落：项目符号（“•” 或 “1.”），跨段落复制时从段落开头复制的加在前面。</summary>
    internal string? ListMarker { get; set; }

    /// <summary>文字的长度（行内控件为一个字符）。</summary>
    internal int TextLength => (Inlines is { Count: > 0 } inlines ? inlines.Text : Text)?.Length ?? 0;

    /// <summary>代码块：选中的文字复制为代码（<see cref="Text"/> 为代码原文）。</summary>
    internal bool IsCodeBlock { get; init; }

    /// <summary>代码块的语言（围栏的标注），可以为空。</summary>
    internal string? CodeLanguage { get; init; }

    /// <summary>选中部分的 Markdown（与选中的文字对应，格式转为 Markdown 符号）。</summary>
    public string SelectedMarkdown
    {
        get
        {
            var start = Math.Min(SelectionStart, SelectionEnd);
            var end = Math.Max(SelectionStart, SelectionEnd);
            if (IsCodeBlock)
                return InlineMarkdown.FromCode(Text ?? "", CodeLanguage, start, end);
            var markdown = InlineMarkdown.FromPieces(_source.ToString(), _pieces, start, end);
            return start == 0 && MarkdownPrefix is { } prefix ? prefix + markdown : markdown;
        }
    }

    /// <summary>登记 [start, end) 区间的文字为链接。</summary>
    internal void AddLink(int start, int end, MessageLinkTarget target)
    {
        if (end > start)
            _links.Add((start, end, target));
    }

    /// <summary>登记追加到段落末尾的文字及其来源；<paramref name="piece"/> 为 null 时复制为原文（换行等）。</summary>
    internal void AddSource(string text, InlinePiece? piece)
    {
        _source.Append(text);
        if (piece is not null)
            _pieces.Add(piece);
    }

    // 行内控件在文字布局中占一个字符
    private static int InlineLength(Inline inline) => inline is Run run ? run.Text?.Length ?? 0 : 1;

    // 选中范围内的图片盖上选择色（选择色画在文字背后，会被图片挡住）
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != SelectionStartProperty && change.Property != SelectionEndProperty)
            return;
        var start = Math.Min(SelectionStart, SelectionEnd);
        var end = Math.Max(SelectionStart, SelectionEnd);
        var offset = 0;
        foreach (var inline in Inlines ?? [])
        {
            if (inline is InlineUIContainer { Child: RemoteImage image })
                image.SelectionOverlay = offset >= start && offset < end ? SelectionBrush : null;
            offset += InlineLength(inline);
        }
    }

    /// <summary>复制选中的部分：文字与 Markdown 一起放到剪贴板。</summary>
    public async Task CopySelectionAsync(IClipboard clipboard)
    {
        try
        {
            await MarkdownClipboard.SetAsync(clipboard, SelectedText, SelectedMarkdown);
        }
        catch (Exception ex)
        {
            Log.Warn("复制失败", ex);
        }
    }

    // Ctrl+C 连同格式复制（SelectableTextBlock 自己的复制只有文字）
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (SelectionStart != SelectionEnd
            && Application.Current?.PlatformSettings?.HotkeyConfiguration is { } hotkeys && hotkeys.Copy.Any(g => g.Matches(e))
            && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            e.Handled = true;
            _ = CopySelectionAsync(clipboard);
            return;
        }
        base.OnKeyDown(e);
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

    // 连续点击提及时每次都当作单击（每次插入一个 @），不选中文字；其它链接仍可以双击选词
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        if (e.ClickCount >= 2 && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed
            && LinkAt(e.GetPosition(this)) is AnyMentionTarget)
        {
            e.Handled = true;
            return;
        }
        base.OnPointerPressed(e);
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

    /// <summary>位置（相对本控件）对应的文字位置：在上方时为开头，在下方时为末尾。</summary>
    internal int TextPositionAt(Point point)
    {
        if (point.Y < 0)
            return 0;
        if (point.Y >= Bounds.Height)
            return TextLength;
        var p = new Point(point.X - Padding.Left, point.Y - Padding.Top);
        return TextLayout.HitTestPoint(in p).TextPosition;
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
