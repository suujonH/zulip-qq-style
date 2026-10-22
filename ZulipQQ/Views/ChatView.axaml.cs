using System;
using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ZulipQQ.Controls;
using ZulipQQ.ViewModels;

namespace ZulipQQ.Views;

/// <summary>
/// 聊天视图：负责滚动。插入或删除消息时保持视口中第一条消息的位置不动；
/// 滚动请求在下一次布局完成后执行（此时新消息已有位置）；滚动停止 100ms 后向 ViewModel 报告可见范围。
/// </summary>
public partial class ChatView : UserControl
{
    private const double BottomTolerance = 20;
    private const double LoadMoreDistance = 300;

    private readonly DispatcherTimer _viewportTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private ChatViewModel? _vm;
    private Window? _host;

    private ScrollRequest? _pendingScroll;
    private (object Item, double Delta)? _anchor;

    // 第一次滚动定位之前不报告可见范围，避免在初始位置把首屏以外的消息误判为已看到
    private bool _firstScrollDone;

    // 链接点击之后的同一次鼠标抬起不再切换时间显示
    private bool _suppressTap;

    public ChatView()
    {
        InitializeComponent();
        _viewportTimer.Tick += (_, _) =>
        {
            _viewportTimer.Stop();
            ReportViewport();
        };
        MessageScroller.ScrollChanged += (_, _) => ScheduleViewport();
        MessageScroller.SizeChanged += (_, _) => UpdateSizes();
        SizeChanged += (_, _) => UpdateSizes();
        LayoutUpdated += OnLayoutUpdated;
        MessageList.AddHandler(MessageContent.LinkClickedEvent, OnLinkClicked);
        MessageList.AddHandler(PointerReleasedEvent, OnMessagePointerReleased, RoutingStrategies.Bubble, handledEventsToo: true);
        RootGrid.RowDefinitions[1].MinHeight = 100;
        RootGrid.RowDefinitions[3].MinHeight = 120;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        Disconnect();
        Connect();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Connect();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Disconnect();
    }

    private void Connect()
    {
        if (_vm is not null || DataContext is not ChatViewModel vm || TopLevel.GetTopLevel(this) is null)
            return;
        _vm = vm;
        vm.ScrollRequested += OnScrollRequested;
        vm.Items.CollectionChanged += OnItemsChanged;
        MessageContent.SetFiles(this, vm.Session.Files);

        _host = TopLevel.GetTopLevel(this) as Window;
        vm.HostWindow = _host;
        if (_host is not null)
        {
            _host.Activated += OnHostActivated;
            // 挂载前窗口已经在前台时不会再收到 Activated
            if (_host.IsActive)
                vm.OnHostActivated();
        }

        // 会话在视图挂载之前已经打开完成
        if (vm.InitialScroll is { } initial)
            RequestScroll(initial);
        UpdateSizes();
    }

    private void Disconnect()
    {
        if (_vm is null)
            return;
        _vm.ScrollRequested -= OnScrollRequested;
        _vm.Items.CollectionChanged -= OnItemsChanged;
        if (_host is not null)
        {
            _host.Activated -= OnHostActivated;
            if (_vm.HostWindow == _host)
                _vm.HostWindow = null;
        }
        _host = null;
        _vm = null;
        _pendingScroll = null;
        _anchor = null;
        _firstScrollDone = false;
        _viewportTimer.Stop();
    }

    private void OnHostActivated(object? sender, EventArgs e) => _vm?.OnHostActivated();

    private void OnScrollRequested(object? sender, ScrollRequest request) => RequestScroll(request);

    private void RequestScroll(ScrollRequest request)
    {
        _pendingScroll = request;
        _anchor = null;
        // 保证会有一次布局完成事件来执行滚动
        MessageList.InvalidateArrange();
    }

    // 记下视口中第一条（已完成布局的）条目的位置，布局后把它放回原处
    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        ScheduleViewport();
        if (_vm is null || _pendingScroll is not null || _anchor is not null || !_firstScrollDone)
            return;
        var offset = MessageScroller.Offset.Y;
        foreach (var item in _vm.Items)
        {
            if (MessageList.ContainerFromItem(item) is not { } container || container.Bounds.Height <= 0)
                continue;
            var y = ContentY(container);
            if (y + container.Bounds.Height > offset)
            {
                _anchor = (item, y - offset);
                return;
            }
        }
    }

    private void OnLayoutUpdated(object? sender, EventArgs e)
    {
        if (_vm is null)
            return;
        if (_pendingScroll is { } request)
        {
            _pendingScroll = null;
            _anchor = null;
            ExecuteScroll(request);
            _firstScrollDone = true;
            ScheduleViewport();
            return;
        }
        if (_anchor is { } anchor)
        {
            _anchor = null;
            if (MessageList.ContainerFromItem(anchor.Item) is { } container)
                SetOffset(ContentY(container) - anchor.Delta);
        }
    }

    private void ExecuteScroll(ScrollRequest request)
    {
        if (request.MessageId is not { } id)
        {
            MessageScroller.ScrollToEnd();
            return;
        }

        Control? target = null;
        foreach (var item in _vm!.Items)
        {
            var match = request.Align == ScrollAlign.TopWithDivider
                ? item is NewMessagesDividerItem
                : item is MessageItemViewModel m && m.Id == id;
            if (match)
            {
                target = MessageList.ContainerFromItem(item);
                break;
            }
        }
        // 没有分隔线时退回按消息定位
        if (target is null && request.Align == ScrollAlign.TopWithDivider)
        {
            ExecuteScroll(request with { Align = ScrollAlign.Center });
            return;
        }
        if (target is null)
            return;

        var y = ContentY(target);
        SetOffset(request.Align == ScrollAlign.TopWithDivider
            ? y - 8
            : y - (MessageScroller.Viewport.Height - target.Bounds.Height) / 2);
    }

    private void SetOffset(double y)
    {
        var max = Math.Max(0, MessageScroller.Extent.Height - MessageScroller.Viewport.Height);
        MessageScroller.Offset = new Vector(MessageScroller.Offset.X, Math.Clamp(y, 0, max));
    }

    private double ContentY(Control container) => container.TranslatePoint(default, MessageList)?.Y ?? 0;

    private void ScheduleViewport()
    {
        if (!_firstScrollDone)
            return;
        _viewportTimer.Stop();
        _viewportTimer.Start();
    }

    private void ReportViewport()
    {
        if (_vm is null || !_firstScrollDone)
            return;
        var offset = MessageScroller.Offset.Y;
        var viewport = MessageScroller.Viewport.Height;
        var extent = MessageScroller.Extent.Height;

        long top = 0, bottom = 0;
        foreach (var item in _vm.Items)
        {
            if (item is not MessageItemViewModel { Id: > 0 } m
                || MessageList.ContainerFromItem(item) is not { } container || container.Bounds.Height <= 0)
                continue;
            var y = ContentY(container);
            if (y + container.Bounds.Height > offset && y < offset + viewport)
            {
                if (top == 0)
                    top = m.Id;
                bottom = m.Id;
            }
            else if (top != 0)
            {
                break;
            }
        }

        _vm.OnViewportChanged(new ViewportInfo(top, bottom,
            IsAtBottom: offset >= extent - viewport - BottomTolerance,
            IsNearTop: offset < LoadMoreDistance,
            IsNearBottom: extent - viewport - offset < LoadMoreDistance));
    }

    private void UpdateSizes()
    {
        if (_vm is not null && MessageScroller.Bounds.Width > 0)
            _vm.BubbleMaxWidth = Math.Max(200, MessageScroller.Bounds.Width * 0.65);
        if (Bounds.Height > 0)
            RootGrid.RowDefinitions[3].MaxHeight = Math.Max(120, Bounds.Height * 0.6);
    }

    private void OnLinkClicked(object? sender, MessageLinkEventArgs e)
    {
        if (_vm is null)
            return;
        e.Handled = true;
        _suppressTap = true;
        Dispatcher.UIThread.Post(() => _suppressTap = false);
        var item = (e.Source as StyledElement)?.DataContext as MessageItemViewModel;
        _ = _vm.HandleLinkAsync(item, e.Target, TopLevel.GetTopLevel(this));
    }

    // 点击气泡：显示 / 隐藏精确时间。拖动选择文字、点击链接时不切换
    private void OnMessagePointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_vm is null || _suppressTap || e.InitialPressMouseButton != MouseButton.Left)
            return;
        if (e.Source is SelectableTextBlock { } text && text.SelectionStart != text.SelectionEnd)
            return;
        for (var v = e.Source as Visual; v is not null && v != MessageList; v = v.GetVisualParent())
        {
            if (v is Button)
                return;
            if (v is Border border && border.Classes.Contains("bubble") && border.DataContext is MessageItemViewModel m)
            {
                _vm.ShowTime(m.Id);
                return;
            }
        }
    }
}
