using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
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
using ZulipQQ.Plugins;
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

    // 第一次滚动定位之前不报告可见范围
    private bool _firstScrollDone;

    // 最近一次滚动后是否停在底部
    private bool _atBottom;

    // 链接点击之后的同一次鼠标抬起不再当作点击气泡
    private bool _suppressTap;

    // 鼠标在气泡上停留 1 秒后显示精确时间；显示后指针移出这条消息时隐藏
    private readonly DispatcherTimer _timeTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private MessageItemViewModel? _hoverItem;
    private MessageItemViewModel? _timeItem;

    // 右键按住的气泡
    private MessageItemViewModel? _pressedItem;
    // 按下时识别出的插件快捷操作（手势已绑定）：抬起时执行；右键时不弹出右键菜单
    private (string Gesture, MouseButton Button, MessageItemViewModel Item)? _shortcut;

    // 最近一次按下的连击次数：双击头像时第二次抬起不再弹出资料卡
    private int _pressClickCount;

    // 跨气泡选择：从一条消息的文字开始拖动到其它消息时，两条之间（含两端）的消息整条选中，复制为“名字: 时间 / 内容”。
    // 拖动中记下起点的消息和文字块（文字选择跟随指针，回到起点消息时从按下的位置恢复为普通的文字选择）；
    // 选中的消息保留到下一次左键按下，右键菜单的“复制”和 Ctrl+C 复制它们
    private MessageItemViewModel? _rangeOrigin;
    private SelectableTextBlock? _rangeBlock;
    private int _rangeAnchor;
    private MessageItemViewModel? _rangeTarget;
    private readonly List<MessageItemViewModel> _range = [];

    // 同一气泡中拖动到其它段落（列表项等）时，起点到指针之间的文字块一起选中；保留到下一次左键按下
    private MessageContent? _acrossContent;

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
        // 右侧栏群公告中的链接
        SidebarView.AddHandler(MessageContent.LinkClickedEvent, OnLinkClicked);
        TitleText.PointerReleased += OnTitlePointerReleased;
        MorePanelDismiss.PointerPressed += (_, _) => _vm?.CloseMorePanelCommand.Execute(null);
        MessageList.AddHandler(PointerPressedEvent, (_, e) => _pressClickCount = e.ClickCount,
            RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerPressedEvent, OnRangePointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        MessageList.AddHandler(PointerPressedEvent, OnRangeBlockPressed, RoutingStrategies.Bubble, handledEventsToo: true);
        MessageList.AddHandler(PointerMovedEvent, OnRangePointerMoved, RoutingStrategies.Tunnel);
        MessageList.AddHandler(KeyDownEvent, OnRangeKeyDown, RoutingStrategies.Tunnel);
        MessageList.AddHandler(PointerReleasedEvent, OnMessagePointerReleased, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(PointerPressedEvent, OnBubblePointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerReleasedEvent, OnBubblePointerReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerCaptureLostEvent, (_, _) => SetPressed(null), RoutingStrategies.Bubble, handledEventsToo: true);
        _timeTimer.Tick += (_, _) =>
        {
            _timeTimer.Stop();
            ShowTime(_hoverItem);
        };
        MessageList.AddHandler(PointerMovedEvent, OnTimePointerMoved, RoutingStrategies.Bubble, handledEventsToo: true);
        // 右键菜单等弹出层盖住消息时也会触发移出，指针仍在消息列表范围内时保持显示
        MessageList.PointerExited += (_, e) =>
        {
            if (!new Rect(MessageScroller.Bounds.Size).Contains(e.GetPosition(MessageScroller)))
                HideTime();
        };
        // 隧道阶段处理：SelectableTextBlock 自带的“Copy”菜单会在冒泡阶段先处理
        MessageList.AddHandler(ContextRequestedEvent, OnMessageContextRequested, RoutingStrategies.Tunnel);
        MessageList.AddHandler(DoubleTappedEvent, OnMessageDoubleTapped);
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
            // 多选模式下焦点不一定在聊天中，在窗口上处理 Esc；对话框处理过的 Esc 不会冒泡到这里
            _host.AddHandler(KeyDownEvent, OnHostKeyDown);
            // 挂载前窗口已经在前台时不会再收到 Activated
            if (_host.IsActive)
                vm.OnHostActivated();
        }

        // 会话在视图挂载之前已经打开完成
        if (vm.InitialScroll is { } initial)
            RequestScroll(initial);
        UpdateSizes();
    }

    // 标题栏“群文件”（仅话题）：打开文件窗口的群文件视图并选中这个话题
    private void OnGroupFilesClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_vm is { } vm)
            FilesWindow.ShowSingle(_host, vm.Main, FilesView.Group, vm.Key);
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
            _host.RemoveHandler(KeyDownEvent, OnHostKeyDown);
            if (_vm.HostWindow == _host)
                _vm.HostWindow = null;
        }
        _host = null;
        _vm = null;
        HideTime();
        SetPressed(null);
        _rangeOrigin = null;
        _rangeBlock = null;
        _rangeTarget = null;
        SetRange([]);
        _acrossContent = null;
        _pendingScroll = null;
        _anchor = null;
        _firstScrollDone = false;
        _atBottom = false;
        _viewportTimer.Stop();
    }

    private void OnHostActivated(object? sender, EventArgs e) => _vm?.OnHostActivated();

    private void OnHostKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || _vm is not { IsMultiSelect: true } vm)
            return;
        e.Handled = true;
        vm.ExitMultiSelect();
    }

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

    // 投票 / 待办卡片的添加输入框显示时获得焦点（布局后才能获得焦点）
    private void OnWidgetInputPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == IsVisibleProperty && e.NewValue is true && sender is TextBox box)
            Dispatcher.UIThread.Post(() => box.Focus(), DispatcherPriority.Loaded);
    }

    private void OnLinkClicked(object? sender, MessageLinkEventArgs e)
    {
        if (_vm is null)
            return;
        e.Handled = true;
        _suppressTap = true;
        Dispatcher.UIThread.Post(() => _suppressTap = false);
        if (e.Target is AnyMentionTarget target)
        {
            // 点击提及：插入到输入框的光标处；输入框不能编辑时，用户提及显示资料卡
            if (_vm.InsertMention(target) || target is not MentionTarget mention)
                return;
            var vm = _vm;
            ShowCard(close => vm.CreateUserCard(mention.UserId, close), e.Source as Control ?? MessageList,
                PlacementMode.RightEdgeAlignedTop);
            return;
        }
        var item = (e.Source as StyledElement)?.DataContext as MessageItemViewModel;
        if (e.Target is ImageTarget image)
        {
            var (images, index) = _vm.CollectImages(image, item?.Id);
            ImageViewerWindow.Show(TopLevel.GetTopLevel(this) as Window, _vm.Session, images, index);
            return;
        }
        _ = _vm.HandleLinkAsync(item, e.Target, TopLevel.GetTopLevel(this));
    }

    private void OnTitlePointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_vm is { } vm && e.InitialPressMouseButton == MouseButton.Left)
            ShowCard(vm.CreateTitleCard, TitleText, PlacementMode.BottomEdgeAlignedLeft);
    }

    /// <summary>
    /// 聊天中用户（消息的头像和昵称、群聊成员）的右键菜单：发送消息、@TA、查看资料、屏蔽此人 / 取消屏蔽。
    /// @TA 只在输入框可编辑且不是自己时显示，屏蔽不能用于自己。已被删除的用户没有菜单。
    /// </summary>
    internal static void ShowUserMenu(ChatViewModel vm, long userId, Control anchor, PlacementMode cardPlacement)
    {
        if (!vm.HasUser(userId))
            return;
        var menu = new ContextMenu();
        menu.Items.Add(ConversationMenus.Item("发送消息", () => vm.OpenDirectChat(userId)));
        if (vm.CanMention(userId))
            menu.Items.Add(ConversationMenus.Item("@TA", () => vm.Mention(userId)));
        menu.Items.Add(ConversationMenus.Item("查看资料", () => ShowCard(close => vm.CreateUserCard(userId, close), anchor, cardPlacement)));
        if (vm.CanMuteUser(userId))
        {
            var muted = vm.IsUserMuted(userId);
            menu.Items.Add(new Separator());
            menu.Items.Add(ConversationMenus.Item(muted ? "取消屏蔽" : "屏蔽此人", () => _ = vm.SetUserMutedAsync(userId, !muted)));
        }
        menu.Open(anchor);
    }

    /// <summary>事件源所在的成员行（数据为 <see cref="ChatMemberItem"/> 的控件）。</summary>
    internal static Control? FindMember(Visual? source, Visual stop)
    {
        for (var v = source; v is not null && v != stop; v = v.GetVisualParent())
        {
            if (v is Control { DataContext: ChatMemberItem } c)
                return c;
        }
        return null;
    }

    /// <summary>事件源所在的消息头像或昵称。</summary>
    private Control? FindSender(Visual? source)
    {
        for (var v = source; v is not null && v != MessageList; v = v.GetVisualParent())
        {
            if (v is Control { DataContext: MessageItemViewModel } c && c.Classes.Contains("sender"))
                return c;
        }
        return null;
    }

    private void OnMessageDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (_vm is { IsMultiSelect: false } vm && FindSender(e.Source as Visual) is { DataContext: MessageItemViewModel msg })
        {
            e.Handled = true;
            vm.OpenDirectChat(msg.Message.SenderId);
        }
    }

    /// <summary>
    /// 弹出名片；名片中点击“发消息”后关闭。没有名片（例如多人私聊的标题）时不弹出。
    /// <paramref name="passThrough"/> 为 true 时，点击名片外关闭名片的同时这次点击照常生效（用于双击头像）。
    /// </summary>
    internal static void ShowCard(Func<Action, ViewModelBase?> create, Control anchor, PlacementMode placement,
        bool passThrough = false)
    {
        var flyout = new Flyout { Placement = placement, OverlayDismissEventPassThrough = passThrough };
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
    /// 消息右键菜单：第一行是快捷表情回应（最后一个“+”打开表情选择器），然后是复制、收藏、引用、转发、多选、复制链接，
    /// 以及编辑、编辑历史、已读成员、从这里标为未读、提醒我（编辑等没有权限时不显示）。
    /// 不提供删除和撤回。发送中的本地回显没有菜单；编辑框中使用输入框自己的菜单。
    /// </summary>
    private void OnMessageContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        // 右键手势绑定了插件的快捷操作：抬起时执行，不弹出菜单
        if (_shortcut is { Button: MouseButton.Right })
        {
            e.Handled = true;
            return;
        }
        if (_vm is { IsMultiSelect: false } chat && FindSender(e.Source as Visual) is { DataContext: MessageItemViewModel msg } senderControl)
        {
            e.Handled = true;
            ShowUserMenu(chat, msg.Message.SenderId, senderControl, PlacementMode.RightEdgeAlignedTop);
            return;
        }
        if (_vm is null || (e.Source as Visual)?.FindAncestorOfType<TextBox>(includeSelf: true) is not null
            || FindBubble(e.Source as Visual) is not { DataContext: MessageItemViewModel { Id: > 0, Message.IsDeleted: false } item } bubble)
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
        // 右键在图片上：另存为这张图片
        if ((e.Source as Visual)?.FindAncestorOfType<RemoteImage>(includeSelf: true) is { Source: { } src, Href: { } href }
            && TopLevel.GetTopLevel(this) is { } top)
            menu.Items.Add(MenuItem("另存为", () => _ = vm.SaveImageAsAsync(new ImageTarget(src, href), top)));
        menu.Items.Add(MenuItem(item.IsStarred ? "取消收藏" : "收藏", () => _ = vm.ToggleStarAsync(item)));
        menu.Items.Add(MenuItem("引用", () => vm.Composer.Quote(item)));
        menu.Items.Add(MenuItem("转发", () => vm.Forward(item)));
        menu.Items.Add(MenuItem("多选", () => vm.BeginMultiSelect(item)));
        menu.Items.Add(MenuItem("复制链接", () => _ = ConversationMenus.CopyAsync(this, vm.MessageLink(item.Id), vm.Host)));

        menu.Items.Add(new Separator());
        if (vm.CanEdit(item))
            menu.Items.Add(MenuItem("编辑", () => _ = vm.BeginEditAsync(item)));
        if (vm.CanShowEditHistory(item))
            menu.Items.Add(MenuItem("编辑历史", () => vm.ShowEditHistory(item)));
        if (vm.CanShowReadReceipts)
            menu.Items.Add(MenuItem("已读成员", () => vm.ShowReadReceipts(item)));
        menu.Items.Add(MenuItem("从这里标为未读", () => _ = vm.MarkUnreadFromAsync(item)));
        var remind = new MenuItem { Header = "提醒我" };
        remind.Items.Add(MenuItem("1 小时后", () => _ = vm.RemindAsync(item, DateTime.Now.AddHours(1))));
        remind.Items.Add(MenuItem("明天 9:00", () => _ = vm.RemindAsync(item, DateTime.Today.AddDays(1).AddHours(9))));
        remind.Items.Add(MenuItem("自定义…", () => _ = vm.RemindCustomAsync(item)));
        menu.Items.Add(remind);
        // 插件声明的消息菜单（例如翻译）：统一放在“插件”子菜单中，插件之间用分隔线隔开
        var plugins = PluginHost.Instance.Plugins.Where(p => p.ActiveMessageMenus.Count > 0).ToList();
        if (plugins.Count > 0)
        {
            var pluginGroup = new MenuItem { Header = "插件" };
            foreach (var plugin in plugins)
            {
                if (pluginGroup.Items.Count > 0)
                    pluginGroup.Items.Add(new Separator());
                foreach (var pluginMenu in plugin.ActiveMessageMenus)
                    pluginGroup.Items.Add(PluginMenuItem(plugin, pluginMenu, item, vm));
            }
            menu.Items.Add(new Separator());
            menu.Items.Add(pluginGroup);
        }
        menu.Open(bubble);
    }

    private static Button ReactionButton(Control content, string tip)
    {
        var button = new Button { Content = content, Width = 32, Height = 32, Padding = new Thickness(0) };
        button.Theme = (Avalonia.Styling.ControlTheme?)Application.Current?.FindResource("FlatButton");
        ToolTip.SetTip(button, tip);
        return button;
    }

    // 插件的消息菜单项；子菜单递归生成
    private static MenuItem PluginMenuItem(NativePlugin plugin, PluginMenu pluginMenu, MessageItemViewModel item, ChatViewModel vm)
    {
        if (!pluginMenu.IsGroup)
            return MenuItem(pluginMenu.Name, () => PluginHost.Instance.InvokeMessageMenu(plugin, pluginMenu.Id, item.Message, vm.Session));
        var group = new MenuItem { Header = pluginMenu.Name };
        foreach (var child in pluginMenu.Children)
            group.Items.Add(PluginMenuItem(plugin, child, item, vm));
        return group;
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
    /// 复制：消息在跨气泡选择中时复制所有选中的消息（只有文字）；气泡中有选中的文字时复制选中部分（连同格式）；
    /// 否则复制整条消息：只有图片的消息为原图，其它为纯文本，并附上消息的 Markdown 原文（粘贴到输入框发送后与原消息相同）。
    /// </summary>
    private async Task CopyAsync(MessageItemViewModel item, Control bubble)
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
            return;
        try
        {
            if (_range.Contains(item))
            {
                await clipboard.SetTextAsync(RangeText());
                return;
            }
            if (bubble.GetVisualDescendants().OfType<MessageContent>().Select(c => c.SelectedAcross()).FirstOrDefault(s => s is not null) is { } across)
            {
                await MarkdownClipboard.SetAsync(clipboard, across.Text, across.Markdown);
                return;
            }
            if (bubble.GetVisualDescendants().OfType<MessageTextBlock>().FirstOrDefault(t => t.SelectionStart != t.SelectionEnd) is { } block)
            {
                await block.CopySelectionAsync(clipboard);
                return;
            }

            var markdown = await RawContentAsync(item);
            var images = bubble.GetVisualDescendants().OfType<RemoteImage>().ToList();
            if (item.IsBare && images.Count == 1 && !bubble.GetVisualDescendants().OfType<FileCard>().Any()
                && await ImageCache.Shared.GetAsync(images[0].Href ?? images[0].Source ?? "", null, RealmImage.GetClient(images[0])) is { } bitmap)
            {
                await MarkdownClipboard.SetAsync(clipboard, "", markdown, bitmap);
                return;
            }
            await MarkdownClipboard.SetAsync(clipboard, ZulipHtml.ToPlainText(item.ContentHtml), markdown);
        }
        catch (Exception ex)
        {
            Log.Warn("复制失败", ex);
        }
    }

    // 消息的 Markdown 原文；本地回显（还没有 id）或获取失败时为 null，只复制文字
    private async Task<string?> RawContentAsync(MessageItemViewModel item)
    {
        if (_vm is not { } vm || item.Id <= 0)
            return null;
        try
        {
            return await vm.Session.Chat.GetRawContentAsync(item.Id);
        }
        catch (Exception ex)
        {
            if (!vm.Session.IsDisposed)
                Log.Warn($"获取消息原文失败：{item.Id}", ex);
            return null;
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

    // 左键按下（聊天视图中任意位置）清除跨气泡选择；在气泡的文字上按下时开始记录。右键保留选择，用于右键菜单的“复制”
    private void OnRangePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || RedirectPressToText(e))
            return;
        SetRange([]);
        _acrossContent?.ClearSelection();
        _acrossContent = null;
        _rangeOrigin = null;
        _rangeBlock = null;
        _rangeTarget = null;
        if (_vm is { IsMultiSelect: false } && e.ClickCount == 1 && e.Source is SelectableTextBlock block
            && FindBubble(block) is { DataContext: MessageItemViewModel item })
        {
            _rangeOrigin = item;
            _rangeTarget = item;
            _rangeBlock = block;
        }
    }

    /// <summary>
    /// 在气泡的空白处（内边距、段落之间、不可选的文字上）左键按下：改为在气泡中最近的文字块上最近的位置按下，
    /// 与直接按在文字上一样放置光标、拖动选择、双击选词。按在按钮、图片、文件卡片上时不处理。
    /// </summary>
    private bool RedirectPressToText(PointerPressedEventArgs e)
    {
        if (_vm is not { IsMultiSelect: false } || e.Source is not Visual source || FindBubble(source) is not { } bubble
            || TopLevel.GetTopLevel(this) is not { } top)
            return false;
        for (Visual? v = source; v is not null && v != bubble; v = v.GetVisualParent())
        {
            if (v is SelectableTextBlock or Button or TextBox or ScrollBar or FileCard or RemoteImage { Href: not null })
                return false;
        }
        var point = e.GetPosition(bubble);
        SelectableTextBlock? nearest = null;
        var target = default(Point);
        var best = double.MaxValue;
        foreach (var block in bubble.GetVisualDescendants().OfType<SelectableTextBlock>())
        {
            if (!block.IsEffectivelyVisible || block.Bounds.Width <= 0 || block.Bounds.Height <= 0
                || block.TranslatePoint(default, bubble) is not { } origin)
                continue;
            // 按下位置夹到文字块的内容区内（底边减 1，避免落到下一行之外）
            var local = new Point(point.X - origin.X, point.Y - origin.Y);
            var padding = block.Padding;
            var x = Math.Clamp(local.X, padding.Left, Math.Max(padding.Left, block.Bounds.Width - padding.Right));
            var y = Math.Clamp(local.Y, padding.Top, Math.Max(padding.Top, block.Bounds.Height - padding.Bottom - 1));
            var distance = (local.X - x) * (local.X - x) + (local.Y - y) * (local.Y - y);
            if (distance < best)
            {
                best = distance;
                nearest = block;
                target = new Point(x, y);
            }
        }
        if (nearest is null || nearest.TranslatePoint(target, top) is not { } at)
            return false;
        // 按下事件已处理，焦点不会自动移动；先给文字块焦点（Ctrl+C 复制选中的文字）
        e.Handled = true;
        nearest.Focus(NavigationMethod.Pointer, e.KeyModifiers);
        nearest.RaiseEvent(new PointerPressedEventArgs(nearest, e.Pointer, top, at, e.Timestamp,
            e.GetCurrentPoint(top).Properties, e.KeyModifiers, e.ClickCount));
        return true;
    }

    // 文字块处理按下之后（光标已放到按下的位置）记下位置，跨气泡时把文字块的选择收起到这里
    private void OnRangeBlockPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_rangeBlock is { } block && e.Source == block)
            _rangeAnchor = block.SelectionStart;
    }

    // 拖动到其它消息上时整条选中两条之间的消息，文字块不再处理这次移动；
    // 回到起点消息时交还给文字块，从按下的位置重新选择。在起点消息中拖动到其它段落时，起点到指针之间的段落一起选中
    private void OnRangePointerMoved(object? sender, PointerEventArgs e)
    {
        if (_vm is null || _rangeOrigin is not { } origin || _rangeBlock is not { } block
            || !e.GetCurrentPoint(MessageList).Properties.IsLeftButtonPressed)
            return;
        // 指针在两条消息之间（时间分隔等）时保持上一次的范围
        _rangeTarget = MessageAt(e.GetPosition(MessageList).Y) ?? _rangeTarget;
        var messages = _vm.Items.OfType<MessageItemViewModel>().ToList();
        var from = messages.IndexOf(origin);
        var to = _rangeTarget is null ? -1 : messages.IndexOf(_rangeTarget);
        var content = block.FindAncestorOfType<MessageContent>();
        if (from < 0 || to < 0 || from == to)
        {
            if (_range.Count > 0)
            {
                SetRange([]);
                block.SelectionStart = _rangeAnchor;
                block.SelectionEnd = _rangeAnchor;
            }
            if (from >= 0 && from == to && block is MessageTextBlock text && content is not null)
            {
                _acrossContent = content;
                e.Handled = content.SelectAcross(text, _rangeAnchor, e.GetPosition(content));
            }
            return;
        }
        SetRange(messages.GetRange(Math.Min(from, to), Math.Abs(to - from) + 1));
        // 文字选择跟随指针：起点消息从按下处选到末尾 / 开头，中间的消息全选，终点消息选到指针处（复制仍是整条消息）
        var forward = to > from;
        if (block is MessageTextBlock originText)
            content?.SelectFrom(originText, _rangeAnchor, forward);
        foreach (var m in _range.Where(m => m != origin && m != _rangeTarget))
        {
            foreach (var c in ContentsOf(m))
                c.SelectAll();
        }
        if (ContentsOf(_rangeTarget!).FirstOrDefault() is { } target)
            target.SelectTo(e.GetPosition(target), forward);
        e.Handled = true;
    }

    // 有跨气泡选择或气泡中多个段落的选择时 Ctrl+C 复制它们（文字块自己的复制只复制它选中的文字）
    private void OnRangeKeyDown(object? sender, KeyEventArgs e)
    {
        var across = _range.Count == 0 ? _acrossContent?.SelectedAcross() : null;
        if (_range.Count == 0 && across is null || Application.Current?.PlatformSettings?.HotkeyConfiguration is not { } hotkeys
            || !hotkeys.Copy.Any(g => g.Matches(e)) || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
            return;
        e.Handled = true;
        _ = CopyRangeAsync(clipboard, across);
    }

    private async Task CopyRangeAsync(IClipboard clipboard, (string Text, string Markdown)? across)
    {
        try
        {
            if (across is { } a)
                await MarkdownClipboard.SetAsync(clipboard, a.Text, a.Markdown);
            else
                await clipboard.SetTextAsync(RangeText());
        }
        catch (Exception ex)
        {
            Log.Warn("复制失败", ex);
        }
    }

    // 选中的消息按时间线顺序，消息之间空一行；已不在时间线中的（被删除、移走）跳过
    private string RangeText() =>
        string.Join("\n\n", _vm?.Items.OfType<MessageItemViewModel>().Where(_range.Contains).Select(m => m.RangeCopyText) ?? []);

    /// <summary>纵坐标（相对 <see cref="MessageList"/>）所在的消息行；不在任何消息行上时为 null。</summary>
    private MessageItemViewModel? MessageAt(double y)
    {
        foreach (var item in _vm!.Items)
        {
            if (item is not MessageItemViewModel m || MessageList.ContainerFromItem(item) is not { } container
                || container.Bounds.Height <= 0)
                continue;
            var top = ContentY(container);
            if (y < top)
                return null;
            if (y < top + container.Bounds.Height)
                return m;
        }
        return null;
    }

    // 选中的消息底色变为选中色；移出范围的消息取消文字选择
    private void SetRange(IReadOnlyList<MessageItemViewModel> items)
    {
        foreach (var m in _range.Except(items))
        {
            m.IsRangeSelected = false;
            foreach (var content in ContentsOf(m))
                content.ClearSelection();
        }
        foreach (var m in items)
            m.IsRangeSelected = true;
        _range.Clear();
        _range.AddRange(items);
    }

    private IEnumerable<MessageContent> ContentsOf(MessageItemViewModel item) =>
        MessageList.ContainerFromItem(item)?.GetVisualDescendants().OfType<MessageContent>() ?? [];

    // 左键按下气泡：变为选中色（按下其它地方时取消）；右键按住气泡：变为选中色，抬起后恢复。多选模式下不处理
    private void OnBubblePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_vm is null)
            return;
        var item = _vm.IsMultiSelect ? null : FindBubble(e.Source as Visual)?.DataContext as MessageItemViewModel;
        var kind = e.GetCurrentPoint(this).Properties.PointerUpdateKind;
        var button = kind switch
        {
            PointerUpdateKind.LeftButtonPressed => MouseButton.Left,
            PointerUpdateKind.MiddleButtonPressed => MouseButton.Middle,
            PointerUpdateKind.RightButtonPressed => MouseButton.Right,
            _ => MouseButton.None,
        };
        // 插件的快捷操作（修饰键 + 按键 + 单击 / 双击，绑定了才生效）
        _shortcut = item is { Id: > 0, Message.IsDeleted: false } && PluginShortcuts.MouseGesture(button, e.ClickCount, e.KeyModifiers) is { } gesture
                    && PluginHost.Instance.HasShortcut(gesture)
            ? (gesture, button, item)
            : null;
        switch (kind)
        {
            case PointerUpdateKind.LeftButtonPressed:
                _vm.SelectMessage(item?.Id);
                break;
            case PointerUpdateKind.RightButtonPressed:
                if (item is not { IsSelected: true })
                    _vm.SelectMessage(null);
                SetPressed(item);
                break;
        }
    }

    private void OnBubblePointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton == MouseButton.Right)
            SetPressed(null);
    }

    private void SetPressed(MessageItemViewModel? item)
    {
        if (_pressedItem == item)
            return;
        if (_pressedItem is { } old)
            old.IsPressed = false;
        _pressedItem = item;
        if (item is not null)
            item.IsPressed = true;
    }

    // 指针进入另一个气泡时重新计时；显示的时间不改变气泡的位置，指针仍在这条消息中时保持显示
    private void OnTimePointerMoved(object? sender, PointerEventArgs e)
    {
        var source = e.Source as Visual;
        if (_timeItem is not null && MessageItemAt(source) != _timeItem)
            HideTime();
        var item = FindBubble(source)?.DataContext as MessageItemViewModel;
        if (item == _hoverItem)
            return;
        _hoverItem = item;
        _timeTimer.Stop();
        if (item is not null && item != _timeItem)
            _timeTimer.Start();
    }

    private void ShowTime(MessageItemViewModel? item)
    {
        if (item is null || item == _timeItem)
            return;
        if (_timeItem is { } old)
            old.IsTimeShown = false;
        _timeItem = item;
        item.IsTimeShown = true;
    }

    private void HideTime()
    {
        _timeTimer.Stop();
        _hoverItem = null;
        if (_timeItem is { } old)
            old.IsTimeShown = false;
        _timeItem = null;
    }

    // 视觉元素所在的消息（消息行中任意位置）
    private MessageItemViewModel? MessageItemAt(Visual? source)
    {
        for (var v = source; v is not null && v != MessageList; v = v.GetVisualParent())
        {
            if (v is Control { DataContext: MessageItemViewModel m })
                return m;
        }
        return null;
    }

    // 点击隐藏的消息（被屏蔽的用户）：显示内容。拖动选择文字、点击链接时不处理。
    // 左键单击头像弹出资料卡（与右键菜单的“查看资料”相同；自己的资料卡没有备注和分组），双击发消息；点击昵称没有反应。
    // 多选模式下消息内容不响应鼠标（见模板），点击整行切换勾选
    private void OnMessagePointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        // 跨气泡拖动选择结束：不当作点击
        var rangeDragged = _rangeOrigin is not null && (_range.Count > 0 || _acrossContent?.SelectedAcross() is not null);
        _rangeOrigin = null;
        _rangeBlock = null;
        _rangeTarget = null;
        var shortcut = _shortcut;
        _shortcut = null;
        if (rangeDragged || _vm is null || _suppressTap)
            return;
        // 插件的快捷操作：按下时识别的手势，在同一个按键抬起时执行
        if (shortcut is { } s && s.Button == e.InitialPressMouseButton)
        {
            PluginHost.Instance.InvokeShortcut(s.Gesture, s.Item.Message, _vm.Session);
            e.Handled = true;
            return;
        }
        if (e.InitialPressMouseButton != MouseButton.Left)
            return;
        if (_vm.IsMultiSelect)
        {
            if ((e.Source as StyledElement)?.DataContext is MessageItemViewModel picked)
                _vm.ToggleChecked(picked);
            return;
        }
        if (e.Source is SelectableTextBlock { } text && text.SelectionStart != text.SelectionEnd)
            return;
        for (var v = e.Source as Visual; v is not null && v != MessageList; v = v.GetVisualParent())
        {
            if (v is Button or TextBox)
                return;
            if (v is Control { DataContext: MessageItemViewModel sent } c && c.Classes.Contains("sender"))
            {
                var senderId = sent.Message.SenderId;
                if (c is Avatar && _pressClickCount == 1 && _vm.HasUser(senderId))
                {
                    var vm = _vm;
                    ShowCard(close => vm.CreateUserCard(senderId, close), c, PlacementMode.RightEdgeAlignedTop, passThrough: true);
                }
                return;
            }
            if (v is Border border && border.Classes.Contains("bubble") && border.DataContext is MessageItemViewModel m)
            {
                if (m.IsMutedHidden)
                    m.Reveal();
                return;
            }
        }
    }
}
