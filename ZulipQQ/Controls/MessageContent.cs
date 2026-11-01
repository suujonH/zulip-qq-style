using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using ZulipQQ.Core;
using ZulipQQ.Services;

namespace ZulipQQ.Controls;

/// <summary>
/// 显示一条消息的内容：把服务器渲染的 HTML 转成 Avalonia 控件（见 <see cref="ZulipHtmlRenderer"/>）。
/// 链接、文件、图片、提及的点击通过冒泡的 <see cref="LinkClickedEvent"/> 交给聊天视图处理。
/// </summary>
public class MessageContent : Decorator
{
    public static readonly StyledProperty<string?> HtmlProperty =
        AvaloniaProperty.Register<MessageContent, string?>(nameof(Html));

    /// <summary>“/me” 消息的发送者名字；不为 null 时内容以斜体显示，并在前面加上名字。</summary>
    public static readonly StyledProperty<string?> MeSenderProperty =
        AvaloniaProperty.Register<MessageContent, string?>(nameof(MeSender));

    /// <summary>文件卡片读取下载状态用的服务，由聊天视图设置在根元素上，向下继承。</summary>
    public static readonly AttachedProperty<FileService?> FilesProperty =
        AvaloniaProperty.RegisterAttached<MessageContent, Control, FileService?>("Files", inherits: true);

    public static readonly RoutedEvent<MessageLinkEventArgs> LinkClickedEvent =
        RoutedEvent.Register<MessageContent, MessageLinkEventArgs>(nameof(LinkClicked), RoutingStrategies.Bubble);

    public string? Html
    {
        get => GetValue(HtmlProperty);
        set => SetValue(HtmlProperty, value);
    }

    public string? MeSender
    {
        get => GetValue(MeSenderProperty);
        set => SetValue(MeSenderProperty, value);
    }

    public event EventHandler<MessageLinkEventArgs>? LinkClicked
    {
        add => AddHandler(LinkClickedEvent, value);
        remove => RemoveHandler(LinkClickedEvent, value);
    }

    public static FileService? GetFiles(Control c) => c.GetValue(FilesProperty);

    public static void SetFiles(Control c, FileService? value) => c.SetValue(FilesProperty, value);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == HtmlProperty || change.Property == MeSenderProperty)
            Rebuild();
    }

    // 显示中的文字块，按内容顺序（折叠的剧透内容除外）
    private List<MessageTextBlock> TextBlocks() =>
        [.. this.GetVisualDescendants().OfType<MessageTextBlock>().Where(b => b.IsEffectivelyVisible)];

    /// <summary>
    /// 从 <paramref name="origin"/> 的 <paramref name="anchor"/> 处拖动到 <paramref name="point"/>（相对本控件）：
    /// 指针最近的文字块不是起点时，起点到指针之间的文字块一起选中（起点从按下处到末尾 / 开头，中间的整块，终点到指针处），
    /// 其它文字块取消选择，返回 true；最近的是起点时取消其它文字块的选择，返回 false，由起点自己处理。
    /// </summary>
    public bool SelectAcross(MessageTextBlock origin, int anchor, Point point)
    {
        var blocks = TextBlocks();
        var from = blocks.IndexOf(origin);
        if (from < 0)
            return false;
        var (to, position) = Nearest(blocks, point);
        if (to == from)
        {
            foreach (var block in blocks.Where(b => b != origin))
            {
                block.SelectionStart = 0;
                block.SelectionEnd = 0;
            }
            return false;
        }
        SelectRange(blocks, from, anchor, to, position);
        return true;
    }

    /// <summary>
    /// 跨气泡选择的起点消息：从 <paramref name="origin"/> 的 <paramref name="anchor"/> 处选到内容末尾（向后拖动）或开头（向前拖动）。
    /// </summary>
    public void SelectFrom(MessageTextBlock origin, int anchor, bool forward)
    {
        var blocks = TextBlocks();
        if (blocks.IndexOf(origin) is var from and >= 0)
            SelectRange(blocks, from, anchor, forward ? blocks.Count - 1 : 0, forward ? blocks[^1].TextLength : 0);
    }

    /// <summary>
    /// 跨气泡选择的终点消息：从内容开头（向后拖动）或末尾（向前拖动）选到 <paramref name="point"/>（相对本控件）最近的文字位置。
    /// </summary>
    public void SelectTo(Point point, bool forward)
    {
        var blocks = TextBlocks();
        if (blocks.Count == 0)
            return;
        var (to, position) = Nearest(blocks, point);
        SelectRange(blocks, forward ? 0 : blocks.Count - 1, forward ? 0 : blocks[^1].TextLength, to, position);
    }

    // 离 point 最近的文字块与其中最近的文字位置（point 夹到文字块范围内后命中测试）
    private (int Index, int Position) Nearest(List<MessageTextBlock> blocks, Point point)
    {
        var index = 0;
        var local = default(Point);
        var best = double.MaxValue;
        for (var i = 0; i < blocks.Count; i++)
        {
            if (blocks[i].TranslatePoint(default, this) is not { } topLeft)
                continue;
            var p = point - topLeft;
            var size = blocks[i].Bounds.Size;
            var dx = Math.Max(0, Math.Max(-p.X, p.X - size.Width));
            var dy = Math.Max(0, Math.Max(-p.Y, p.Y - size.Height));
            if (dx * dx + dy * dy < best)
            {
                best = dx * dx + dy * dy;
                index = i;
                local = p;
            }
        }
        return (index, blocks[index].TextPositionAt(local));
    }

    // 从第 from 块的 fromPos 选到第 to 块的 toPos：两块之间的整块选中，其它文字块取消选择
    private static void SelectRange(List<MessageTextBlock> blocks, int from, int fromPos, int to, int toPos)
    {
        var forward = to >= from;
        for (var i = 0; i < blocks.Count; i++)
        {
            var block = blocks[i];
            if (i == from && i == to)
            {
                block.SelectionStart = fromPos;
                block.SelectionEnd = toPos;
            }
            else if (i == from)
            {
                block.SelectionStart = fromPos;
                block.SelectionEnd = forward ? block.TextLength : 0;
            }
            else if (i == to)
            {
                block.SelectionStart = forward ? 0 : block.TextLength;
                block.SelectionEnd = toPos;
            }
            else if (i > Math.Min(from, to) && i < Math.Max(from, to))
            {
                block.SelectionStart = 0;
                block.SelectionEnd = block.TextLength;
            }
            else
            {
                block.SelectionStart = 0;
                block.SelectionEnd = 0;
            }
        }
    }

    /// <summary>全选所有文字块（跨气泡选择中间的消息）。</summary>
    public void SelectAll()
    {
        foreach (var block in TextBlocks())
        {
            block.SelectionStart = 0;
            block.SelectionEnd = block.TextLength;
        }
    }

    /// <summary>取消所有文字块的选择。</summary>
    public void ClearSelection()
    {
        foreach (var block in TextBlocks())
        {
            block.SelectionStart = 0;
            block.SelectionEnd = 0;
        }
    }

    /// <summary>
    /// 多个文字块中有选中的文字时，按顺序拼起来的文字与 Markdown（段落之间换行，列表项从开头选中时加上项目符号）；
    /// 只有一个或没有时为 null（由文字块自己复制）。
    /// </summary>
    public (string Text, string Markdown)? SelectedAcross()
    {
        var selected = TextBlocks().Where(b => b.SelectionStart != b.SelectionEnd).ToList();
        if (selected.Count < 2)
            return null;
        var text = new StringBuilder();
        var markdown = new StringBuilder();
        MessageTextBlock? previous = null;
        foreach (var block in selected)
        {
            if (previous is not null)
            {
                text.Append('\n');
                // 相邻的列表项之间只换一行，其它段落之间空一行
                markdown.Append(previous.ListMarker is not null && block.ListMarker is not null ? "\n" : "\n\n");
            }
            if (Math.Min(block.SelectionStart, block.SelectionEnd) == 0 && block.ListMarker is { } marker)
            {
                text.Append(marker).Append(' ');
                markdown.Append(marker == "•" ? "-" : marker).Append(' ');
            }
            text.Append(block.SelectedText);
            markdown.Append(block.SelectedMarkdown);
            previous = block;
        }
        return (text.ToString(), markdown.ToString());
    }

    private void Rebuild()
    {
        var html = Html ?? "";
        try
        {
            Child = ZulipHtmlRenderer.Build(html, MeSender);
        }
        catch (Exception ex)
        {
            // 渲染出错时降级为纯文本
            Log.Warn("渲染消息失败", ex);
            Child = new SelectableTextBlock { Text = SafePlainText(html), TextWrapping = TextWrapping.Wrap };
        }
    }

    private static string SafePlainText(string html)
    {
        try
        {
            return ZulipHtml.ToPlainText(html);
        }
        catch (Exception)
        {
            return html;
        }
    }
}
