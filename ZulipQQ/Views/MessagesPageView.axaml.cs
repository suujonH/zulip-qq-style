using System;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ZulipQQ.Core;
using ZulipQQ.ViewModels;

namespace ZulipQQ.Views;

public partial class MessagesPageView : UserControl
{
    private MessagesPageViewModel? _vm;

    // 指针在同一个会话上停留这么久后预载它的消息（移动指针去点击通常要 200～300ms）
    private readonly DispatcherTimer _hoverTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private ConversationItemViewModel? _hoverItem;

    public MessagesPageView()
    {
        InitializeComponent();
        _hoverTimer.Tick += (_, _) =>
        {
            _hoverTimer.Stop();
            if (_hoverItem is { } item)
                _vm?.Prefetch(item);
        };
        foreach (var list in new[] { MainList, HelperList })
        {
            list.AddHandler(PointerMovedEvent, OnListPointerMoved, RoutingStrategies.Bubble, handledEventsToo: true);
            list.PointerExited += (_, _) => SetHoverItem(null);
        }
        MainList.AddHandler(ContextRequestedEvent, OnContextRequested);
        HelperList.AddHandler(ContextRequestedEvent, OnContextRequested);
        MainList.AddHandler(PointerPressedEvent, OnListPointerPressed, RoutingStrategies.Tunnel);
        HelperList.AddHandler(PointerPressedEvent, OnListPointerPressed, RoutingStrategies.Tunnel);
    }

    private void OnListPointerMoved(object? sender, PointerEventArgs e) =>
        SetHoverItem((e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext as ConversationItemViewModel);

    // 指针移到另一个会话上时重新计时
    private void SetHoverItem(ConversationItemViewModel? item)
    {
        if (item == _hoverItem)
            return;
        _hoverItem = item;
        _hoverTimer.Stop();
        if (item is not null)
            _hoverTimer.Start();
    }

    // 右键只弹出菜单，不选中（不打开）会话。左键点击已选中的会话时选中项不变、不会触发打开，
    // 这里直接再打开一次：会话在独立窗口中时把被遮住或最小化的窗口带到前台。
    // 左键按下会话夹条目时直接打开会话夹，不让列表选中它
    private void OnListPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(sender as Visual);
        if (point.Properties.IsRightButtonPressed)
        {
            e.Handled = true;
            return;
        }
        if (!point.Properties.IsLeftButtonPressed
            || (e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true) is not { DataContext: ConversationItemViewModel item } container)
            return;
        if (item.IsFolder)
        {
            e.Handled = true;
            _vm?.EnterFolder(item);
        }
        else if (container.IsSelected)
        {
            _vm?.Reopen(item);
        }
    }

    /// <summary>
    /// 会话右键菜单：置顶、标为已读 / 未读、免打扰级别、从消息列表中移除；群组的标为未读、复制链接与话题操作在最下方的“更多设置”中。会话夹条目只有置顶。
    /// </summary>
    private void OnContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (_vm is null || (e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true) is not
                { DataContext: ConversationItemViewModel item } container)
            return;
        e.Handled = true;
        var vm = _vm;
        var menu = new ContextMenu();
        menu.Items.Add(ConversationMenus.Item(item.IsPinned ? "取消置顶" : "置顶", () => vm.TogglePin(item)));
        if (!item.IsFolder)
        {
            if (vm.CanMarkRead(item))
                menu.Items.Add(ConversationMenus.Item("标为已读", () => _ = vm.MarkReadAsync(item)));
            // 群组的“标为未读”放在“更多设置”中
            var topic = item.Key as TopicKey;
            var main = TopLevel.GetTopLevel(this)?.DataContext as MainViewModel;
            Action? markUnread = vm.CanMarkUnread(item) ? () => _ = vm.MarkUnreadAsync(item) : null;
            if (markUnread is not null && (topic is null || main is null))
                menu.Items.Add(ConversationMenus.Item("标为未读", markUnread));
            menu.Items.Add(new Separator());
            if (MessagesPageViewModel.HasGroupLevels(item))
            {
                menu.Items.Add(ConversationMenus.LevelSubmenu(item.Level, level => vm.SetLevel(item, level)));
            }
            else
            {
                // 仿 QQ：普通菜单项（带图标），文字随状态切换
                var notify = item.Level == DndLevel.Notify;
                menu.Items.Add(ConversationMenus.Item(notify ? "设置免打扰" : "取消免打扰",
                    () => vm.SetLevel(item, notify ? DndLevel.Silent : DndLevel.Notify)));
            }
            menu.Items.Add(new Separator());
            // 已经总是在独立窗口中打开时不需要这一项
            if (main is { UsesChatWindows: false })
                menu.Items.Add(ConversationMenus.Item("打开独立聊天窗口", () => main.OpenInWindow(item.Key)));
            menu.Items.Add(ConversationMenus.Item("从消息列表中移除", () => _ = vm.RemoveAsync(item)));
            if (topic is not null && main is not null)
            {
                menu.Items.Add(new Separator());
                menu.Items.Add(ConversationMenus.MoreSubmenu(main, topic, container, markUnread));
            }
        }
        menu.Open(container);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null)
            _vm.PropertyChanged -= OnViewModelPropertyChanged;
        _vm = DataContext as MessagesPageViewModel;
        if (_vm is not null)
            _vm.PropertyChanged += OnViewModelPropertyChanged;
    }

    // 切换主列表 / 会话夹子列表时，键盘焦点移到显示中的列表（隐藏的一层已被禁用）
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MessagesPageViewModel.IsHelperOpen) || _vm is null)
            return;
        var target = _vm.IsHelperOpen ? HelperList : MainList;
        // 等样式把目标层重新启用后再移动焦点
        Dispatcher.UIThread.Post(() =>
        {
            // ListBox 本身不接收焦点，焦点放在选中项的容器上；没有选中项时，
            // 子列表取第一项，主列表取刚才打开的会话夹条目（刚才点击的位置，避免列表滚动）
            var index = target.SelectedIndex;
            if (index < 0)
                index = target == MainList ? _vm.Conversations.ToList().FindIndex(c => c.Key.Equals(_vm.OpenFolder)) : 0;
            if (index >= 0 && target.ContainerFromIndex(index) is { } container)
                container.Focus(NavigationMethod.Directional);
        }, DispatcherPriority.Input);
    }
}
