using System;
using System.Linq;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Styling;
using Avalonia.Threading;
using ZulipQQ.Core;

namespace ZulipQQ.Controls.MarkdownEditing;

/// <summary>
/// 输入框中的引用消息（<see cref="QuoteBlock"/>）：默认收起，只显示“发送者：原文第一行”，单击展开查看发送后的样子（只读）。
/// 标题右侧是编辑、跳转到原消息、删除。整段原文在编辑器中是一个元素：光标不会停在中间，退格 / 删除整段删除。
/// 展开 / 收起时箭头转动，展开的内容淡入。
/// <para>
/// <c>editing</c> 为 true 时只是编辑中的引用的标题栏（见 <see cref="QuoteTitleGenerator"/>）：原文在它下面的行中编辑
/// （与整个输入框一样按渲染或源码显示），右侧是跳转与删除，单击收起并结束编辑。
/// </para>
/// </summary>
internal sealed class QuotePanel : Border
{
    private static readonly Cursor HandCursor = new(StandardCursorType.Hand);
    private static readonly TimeSpan AnimationDuration = TimeSpan.FromMilliseconds(160);

    private readonly Action _toggle;
    private bool _pressed;

    public QuotePanel(MarkdownEditor editor, QuoteBlock quote, double width, bool editing = false)
    {
        _toggle = editing ? () => editor.CollapseQuote(quote) : () => editor.ToggleQuote(quote);
        var expanded = editing || editor.IsQuoteExpanded(quote);
        var body = editor.PlainText(editor.Document.GetText(quote.BodyStart, quote.BodyEnd - quote.BodyStart)).Trim('\n');
        var preview = body.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";
        var fontSize = editor.Size("FontSizeSmall");
        var secondary = editor.Brush("TextSecondaryBrush");
        var theme = editor.TryFindResource("FlatButton", editor.ActualThemeVariant, out var t) ? t as ControlTheme : null;

        Button IconButton(string icon, string tip, Action action)
        {
            var button = new Button
            {
                Theme = theme, Focusable = false, Cursor = HandCursor, Width = 22, Height = 22, Padding = new Thickness(0),
                VerticalAlignment = VerticalAlignment.Top,
                Content = new TdIcon { IconKey = icon, Width = 14, Height = 14, Foreground = secondary },
            };
            ToolTip.SetTip(button, tip);
            button.Click += (_, _) => action();
            return button;
        }

        var header = new DockPanel();
        void AddRight(Control control)
        {
            DockPanel.SetDock(control, Dock.Right);
            header.Children.Add(control);
        }
        AddRight(IconButton("Icon.Close", "删除引用", () => editor.DeleteQuote(quote)));
        if (quote.MessageId is not null)
            AddRight(IconButton("Icon.ArrowRightUp", "跳转到原消息", () => editor.JumpToQuote(quote)));
        if (!editing)
            AddRight(IconButton("Icon.Edit", "编辑引用", () => editor.EditQuote(quote)));

        // 箭头向右为收起，转到向下为展开
        var animate = !editing && editor.TakeQuoteAnimation(quote);
        var chevron = new TdIcon
        {
            IconKey = "Icon.ChevronRight", Width = 12, Height = 12,
            Foreground = secondary, VerticalAlignment = VerticalAlignment.Center,
            RenderTransform = Rotation(expanded != animate),
        };

        var toggle = new Button
        {
            Theme = theme, Focusable = false, Cursor = HandCursor, Padding = new Thickness(4, 2),
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left,
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 4,
                Children =
                {
                    chevron,
                    new TextBlock
                    {
                        Text = $"{quote.SenderName}：{preview}", FontSize = fontSize, Foreground = secondary,
                        TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
                    },
                },
            },
        };
        ToolTip.SetTip(toggle, expanded ? "收起" : "展开");
        toggle.Click += (_, _) => _toggle();
        header.Children.Add(toggle);

        var content = new StackPanel { Children = { header } };
        if (expanded && !editing)
        {
            // 发送后的样子；还没有取到（或取不到）时显示原文的文字
            Control view = editor.RenderedQuote(quote) is { } html
                ? new MessageContent { Html = html, Margin = new Thickness(8, 2, 8, 4) }
                : new TextBlock
                {
                    Text = body, FontSize = fontSize, Foreground = editor.Brush("TextPrimaryBrush"),
                    TextWrapping = TextWrapping.Wrap, Margin = new Thickness(20, 2, 8, 4),
                };
            content.Children.Add(view);
            if (animate)
            {
                // 从上方稍微滑下并淡入（只改绘制，不改大小：大小变化会让输入框重建这一行）
                view.Opacity = 0;
                view.RenderTransform = TransformOperations.Parse("translateY(-6px)");
                view.Transitions =
                [
                    new DoubleTransition { Property = OpacityProperty, Duration = AnimationDuration },
                    new TransformOperationsTransition { Property = RenderTransformProperty, Duration = AnimationDuration, Easing = new CubicEaseOut() },
                ];
                AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(() =>
                {
                    view.Opacity = 1;
                    view.RenderTransform = TransformOperations.Parse("none");
                });
            }
        }
        if (animate)
        {
            chevron.Transitions = [new TransformOperationsTransition { Property = RenderTransformProperty, Duration = AnimationDuration }];
            AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(() => chevron.RenderTransform = Rotation(expanded));
        }

        Width = width;
        Margin = new Thickness(0, 2);
        Padding = new Thickness(2);
        CornerRadius = new CornerRadius(6);
        // 编辑中的卡片底色由背景层画（包括下面的原文行）
        Background = editing ? Brushes.Transparent : editor.Brush("QuoteCardBrush");
        // 文字区的 I 形光标不用在面板上
        Cursor = Cursor.Default;
        Child = content;

        // 底部与文字基线对齐（面板高度在这里就能确定，放进输入框后再按实际大小更新）
        Measure(new Size(width, double.PositiveInfinity));
    }

    private static ITransform Rotation(bool expanded) => TransformOperations.Parse(expanded ? "rotate(90deg)" : "rotate(0deg)");

    protected override Size MeasureOverride(Size availableSize)
    {
        var size = base.MeasureOverride(availableSize);
        TextBlock.SetBaselineOffset(this, size.Height + Margin.Top);
        return size;
    }

    // 按钮以外的地方：按下不交给编辑区（否则光标会按点击位置移进引用、显示原文进入编辑），单击只展开 / 收起。
    // 只有编辑按钮进入编辑
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (e.Handled)
            return;
        e.Handled = true;
        _pressed = e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && e.ClickCount == 1;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_pressed)
            return;
        _pressed = false;
        e.Handled = true;
        if (e.InitialPressMouseButton == MouseButton.Left && new Rect(Bounds.Size).Contains(e.GetPosition(this)))
            _toggle();
    }
}
