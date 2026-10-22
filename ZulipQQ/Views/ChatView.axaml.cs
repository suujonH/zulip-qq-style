using System;
using System.Collections.Specialized;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ZulipQQ.Controls;
using ZulipQQ.Core;
using ZulipQQ.Services;
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
    private const double SidebarMinChatWidth = 640;

    private readonly DispatcherTimer _viewportTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private ChatViewModel? _vm;
    private Window? _host;

    private ScrollRequest? _pendingScroll;
    private (object Item, double Delta)? _anchor;

    // 第一次滚动定位之前不报告可见范围，避免在初始位置把首屏以外的消息误判为已看到
    private bool _firstScrollDone;

    // 最近一次滚动后是否停在底部
    private bool _atBottom;

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
        MessageScroller.ScrollChanged += OnScrollChanged;
        MessageScroller.SizeChanged += (_, _) => UpdateSizes();
        SizeChanged += (_, _) => UpdateSizes();
        LayoutUpdated += OnLayoutUpdated;
        MessageList.AddHandler(MessageContent.LinkClickedEvent, OnLinkClicked);
        TitleText.PointerReleased += OnTitlePointerReleased;
        MorePanelDismiss.PointerPressed += (_, _) => _vm?.CloseMorePanelCommand.Execute(null);
        MessageList.AddHandler(PointerReleasedEvent, OnMessagePointerReleased, RoutingStrategies.Bubble, handledEventsToo: true);
        // 隧道阶段处理：SelectableTextBlock 自带的“Copy”菜单会在冒泡阶段先处理
        MessageList.AddHandler(ContextRequestedEvent, OnMessageContextRequested, RoutingStrategies.Tunnel);
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
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
        _atBottom = false;
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

    // 停在底部时，内容变高（贴表情、图片加载完成）或视口变矮（输入区变高）后仍然停在底部
    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_firstScrollDone && _atBottom && _pendingScroll is null && _anchor is null
            && (e.ExtentDelta.Y > 0 || e.ViewportDelta.Y < 0))
        {
            MessageScroller.ScrollToEnd();
            return;
        }
        _atBottom = MessageScroller.Offset.Y >= MessageScroller.Extent.Height - MessageScroller.Viewport.Height - BottomTolerance;
        ScheduleViewport();
    }

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
        // 右侧栏只在显示后消息区仍不小于 640 时显示
        SidebarView.IsVisible = _vm?.Sidebar is not null && Bounds.Width - SidebarView.Width >= SidebarMinChatWidth;
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
        if (e.Target is MentionTarget mention)
        {
            var vm = _vm;
            ShowCard(close => vm.CreateUserCard(mention.UserId, close), e.Source as Control ?? MessageList,
                PlacementMode.RightEdgeAlignedTop);
            return;
        }
        var item = (e.Source as StyledElement)?.DataContext as MessageItemViewModel;
        _ = _vm.HandleLinkAsync(item, e.Target, TopLevel.GetTopLevel(this));
    }

    private void OnTitlePointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_vm is { } vm && e.InitialPressMouseButton == MouseButton.Left)
            ShowCard(vm.CreateTitleCard, TitleText, PlacementMode.BottomEdgeAlignedLeft);
    }

    /// <summary>弹出名片；名片中点击“发消息”后关闭。没有名片（例如多人私聊的标题）时不弹出。</summary>
    internal static void ShowCard(Func<Action, ViewModelBase?> create, Control anchor, PlacementMode placement)
    {
        var flyout = new Flyout { Placement = placement };
        flyout.FlyoutPresenterClasses.Add("card");
        if (create(() => flyout.Hide()) is not { } card)
            return;
        flyout.Content = card;
        flyout.ShowAt(anchor);
    }

    // 文件拖到聊天区：图片进入待发送列表，其它文件上传后直接发送
    private void OnDragOver(object? sender, DragEventArgs e) =>
        e.DragEffects = _vm?.Composer.HasSendPermission == true && e.DataTransfer.Contains(DataFormat.File)
            ? DragDropEffects.Copy
            : DragDropEffects.None;

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (_vm is null || e.DataTransfer.TryGetFiles() is not { Length: > 0 } files)
            return;
        _vm.Composer.AddFiles(files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList());
    }

    /// <summary>
    /// 消息右键菜单：第一行是快捷表情回应（最后一个“+”打开表情选择器），然后是复制、收藏、引用。
    /// 不提供删除和撤回。发送中的本地回显没有菜单。
    /// </summary>
    private void OnMessageContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (_vm is null || FindBubble(e.Source as Visual) is not { DataContext: MessageItemViewModel { Id: > 0 } item } bubble)
            return;
        e.Handled = true;
        var vm = _vm;
        var menu = new ContextMenu();

        var reactions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        foreach (var emoji in vm.Session.Emoji.QuickReactions)
        {
            Control content = emoji.ImageUrl is { } url
                ? new RemoteImage { Source = url, Width = 22, Height = 22, CornerRadius = 0 }
                : new TextBlock { Text = emoji.Text, FontSize = 18, FontFamily = new FontFamily("Segoe UI Emoji") };
            var button = ReactionButton(content, $":{emoji.Name}:");
            button.Click += (_, _) =>
            {
                menu.Close();
                _ = vm.ReactAsync(item, emoji);
            };
            reactions.Children.Add(button);
        }
        var more = ReactionButton(new TdIcon { Icon = this.FindResource("Icon.Add") as TdIconData, Width = 16, Height = 16 }, "更多表情");
        more.Click += (_, _) =>
        {
            menu.Close();
            ShowReactionPicker(vm, item, bubble);
        };
        reactions.Children.Add(more);
        menu.Items.Add(new MenuItem { Header = reactions, StaysOpenOnClick = true });
        menu.Items.Add(new Separator());

        menu.Items.Add(MenuItem("复制", () => _ = CopyAsync(item, bubble)));
        menu.Items.Add(MenuItem(item.IsStarred ? "取消收藏" : "收藏", () => _ = vm.ToggleStarAsync(item)));
        menu.Items.Add(MenuItem("引用", () => vm.Composer.Quote(item)));
        menu.Open(bubble);
    }

    private static Button ReactionButton(Control content, string tip)
    {
        var button = new Button { Content = content, Width = 32, Height = 32, Padding = new Thickness(0) };
        button.Theme = (Avalonia.Styling.ControlTheme?)Application.Current?.FindResource("FlatButton");
        ToolTip.SetTip(button, tip);
        return button;
    }

    private static MenuItem MenuItem(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }

    private void ShowReactionPicker(ChatViewModel vm, MessageItemViewModel item, Control anchor)
    {
        var picker = new EmojiPickerViewModel(vm.Session.Emoji);
        var flyout = new Flyout { Content = new EmojiPickerView { DataContext = picker }, Placement = PlacementMode.TopEdgeAlignedLeft };
        picker.Picked += (_, emoji) =>
        {
            flyout.Hide();
            _ = vm.ReactAsync(item, emoji);
        };
        flyout.ShowAt(anchor);
    }

    /// <summary>
    /// 复制：气泡中有选中的文字时复制选中部分；只有图片的消息复制原图；否则复制纯文本。
    /// </summary>
    private async Task CopyAsync(MessageItemViewModel item, Control bubble)
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
            return;
        try
        {
            var selected = bubble.GetVisualDescendants().OfType<SelectableTextBlock>()
                .Select(t => t.SelectedText).FirstOrDefault(s => !string.IsNullOrEmpty(s));
            if (selected is not null)
            {
                await clipboard.SetTextAsync(selected);
                return;
            }

            var images = bubble.GetVisualDescendants().OfType<RemoteImage>().ToList();
            if (item.IsBare && images.Count == 1 && !bubble.GetVisualDescendants().OfType<FileCard>().Any()
                && await ImageCache.Shared.GetAsync(images[0].Href ?? images[0].Source ?? "", null) is { } bitmap)
            {
                await clipboard.SetBitmapAsync(bitmap);
                return;
            }
            await clipboard.SetTextAsync(ZulipHtml.ToPlainText(item.ContentHtml));
        }
        catch (Exception ex)
        {
            Log.Warn("复制失败", ex);
        }
    }

    private Border? FindBubble(Visual? source)
    {
        for (var v = source; v is not null && v != MessageList; v = v.GetVisualParent())
        {
            if (v is Border border && border.Classes.Contains("bubble"))
                return border;
        }
        return null;
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
            if (v is Control { DataContext: MessageItemViewModel msg } c && c.Classes.Contains("sender"))
            {
                var vm = _vm;
                ShowCard(close => vm.CreateUserCard(msg.Message.SenderId, close), c, PlacementMode.RightEdgeAlignedTop);
                return;
            }
            if (v is Border border && border.Classes.Contains("bubble") && border.DataContext is MessageItemViewModel m)
            {
                if (m.IsMutedHidden)
                    m.Reveal();
                else
                    _vm.ShowTime(m.Id);
                return;
            }
        }
    }
}
