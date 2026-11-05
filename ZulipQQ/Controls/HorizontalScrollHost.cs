using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;

namespace ZulipQQ.Controls;

/// <summary>
/// 表格、代码块的横向滚动：滚动条常时显示，占自己的一行，排在内容下方而不是盖在内容上。
/// <paramref name="reserveBar"/> 为 false 时，内容放得下就不显示滚动条，也不占高度。
/// 是否显示在 Measure 时就决定，高度不会在排版之后再变（消息列表的滚动位置依赖消息的高度）。
/// </summary>
internal sealed class HorizontalScrollHost : Panel
{
    private readonly Control _content;
    private readonly bool _reserveBar;
    private readonly ScrollBar _bar;
    // 滚动条所在的一行：不用外层（输入框）的 I 形光标。滚动条禁用时不参与命中测试，要靠这一行的底色接住鼠标
    private readonly Border _barRow;
    private bool _dragging;

    public HorizontalScrollHost(Control content, bool reserveBar)
    {
        _content = content;
        _reserveBar = reserveBar;
        Viewer = new ScrollViewer
        {
            Content = content,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        _bar = new ScrollBar
        {
            Orientation = Orientation.Horizontal,
            Visibility = ScrollBarVisibility.Visible,
            AllowAutoHide = false,
            Classes = { "alwaysVisible" },
        };
        _barRow = new Border { Background = Brushes.Transparent, Cursor = Cursor.Default, Child = _bar };
        _bar.ValueChanged += (_, e) =>
        {
            if (Math.Abs(Viewer.Offset.X - e.NewValue) > 0.5)
                Viewer.Offset = new Vector(e.NewValue, 0);
        };
        Viewer.ScrollChanged += (_, _) => SyncBar();
        // 里面的文字第一次得到焦点时 ScrollViewer 会把它滚动到可见，点击时内容就跳了
        ScrollViewer.SetBringIntoViewOnFocusChange(Viewer, false);
        // 输入框的单元格由光标带动滚动（MarkdownTablePanel.EnsureCaretVisible）；这里的文字块没有光标，跟随鼠标
        if (!reserveBar)
        {
            AddHandler(PointerPressedEvent, (_, e) => _dragging = e.GetPosition(Viewer).Y < Viewer.Bounds.Height, RoutingStrategies.Tunnel, handledEventsToo: true);
            AddHandler(PointerReleasedEvent, (_, _) => _dragging = false, RoutingStrategies.Tunnel, handledEventsToo: true);
            AddHandler(PointerMovedEvent, OnPointerDragged, RoutingStrategies.Tunnel, handledEventsToo: true);
        }
        Children.Add(Viewer);
        Children.Add(_barRow);
    }

    public ScrollViewer Viewer { get; }

    // 拖选文字时鼠标到了可视区外：滚动相应的距离
    private void OnPointerDragged(object? sender, PointerEventArgs e)
    {
        if (!_dragging)
            return;
        var x = e.GetPosition(Viewer).X;
        var beyond = x < 0 ? x : Math.Max(0, x - Viewer.Viewport.Width);
        if (beyond != 0)
            Viewer.Offset = new Vector(Math.Clamp(Viewer.Offset.X + beyond, 0, _bar.Maximum), 0);
    }

    private void SyncBar()
    {
        _bar.Maximum = Math.Max(0, Viewer.Extent.Width - Viewer.Viewport.Width);
        _bar.ViewportSize = Viewer.Viewport.Width;
        // 点滚动条的空白处翻一页
        _bar.LargeChange = Viewer.Viewport.Width;
        _bar.Value = Viewer.Offset.X;
        _bar.IsEnabled = _bar.Maximum > 0;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var unbounded = availableSize.WithHeight(double.PositiveInfinity);
        Viewer.Measure(unbounded);
        // ScrollViewer 横向可滚动时，内容按不限宽度测量，这里得到的是完整的宽度
        _barRow.IsVisible = _reserveBar || _content.DesiredSize.Width > availableSize.Width + 0.5;
        _barRow.Measure(unbounded);
        return new Size(Viewer.DesiredSize.Width, Viewer.DesiredSize.Height + _barRow.DesiredSize.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var height = Viewer.DesiredSize.Height;
        Viewer.Arrange(new Rect(0, 0, finalSize.Width, height));
        _barRow.Arrange(new Rect(0, height, finalSize.Width, _barRow.DesiredSize.Height));
        return finalSize;
    }
}
