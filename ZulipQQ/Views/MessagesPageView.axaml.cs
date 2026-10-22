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

    public MessagesPageView()
    {
        InitializeComponent();
        MainList.AddHandler(ContextRequestedEvent, OnContextRequested);
        HelperList.AddHandler(ContextRequestedEvent, OnContextRequested);
        // 右键只弹出菜单，不选中（不打开）会话
        MainList.AddHandler(PointerPressedEvent, SuppressRightPress, RoutingStrategies.Tunnel);
        HelperList.AddHandler(PointerPressedEvent, SuppressRightPress, RoutingStrategies.Tunnel);
    }

    private static void SuppressRightPress(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(sender as Visual).Properties.IsRightButtonPressed)
            e.Handled = true;
    }

    /// <summary>
    /// 会话右键菜单：置顶、标为已读 / 未读、免打扰级别、话题操作、从消息列表中移除。群助手条目只有置顶。
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
        if (!item.IsHelper)
        {
            if (vm.CanMarkRead(item))
                menu.Items.Add(ConversationMenus.Item("标为已读", () => _ = vm.MarkReadAsync(item)));
            if (vm.CanMarkUnread(item))
                menu.Items.Add(ConversationMenus.Item("标为未读", () => _ = vm.MarkUnreadAsync(item)));
            menu.Items.Add(new Separator());
            if (MessagesPageViewModel.HasGroupLevels(item))
            {
                menu.Items.Add(ConversationMenus.LevelSubmenu(item.Level, level => _ = vm.SetLevelAsync(item, level)));
            }
            else
            {
                var silent = ConversationMenus.Item("消息免打扰",
                    () => _ = vm.SetLevelAsync(item, item.Level == DndLevel.Notify ? DndLevel.Silent : DndLevel.Notify));
                silent.ToggleType = MenuItemToggleType.CheckBox;
                silent.IsChecked = item.Level != DndLevel.Notify;
                menu.Items.Add(silent);
            }

            if (item.Key is TopicKey topic && TopLevel.GetTopLevel(this)?.DataContext is MainViewModel main)
            {
                menu.Items.Add(ConversationMenus.FollowItem(main, topic));
                menu.Items.Add(ConversationMenus.CopyLinkItem(main, topic, container));
                ConversationMenus.AppendManagement(menu.Items, main, topic);
            }
            menu.Items.Add(new Separator());
            menu.Items.Add(ConversationMenus.Item("从消息列表中移除", () => _ = vm.RemoveAsync(item)));
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

    // 切换主列表 / 群助手子列表时，键盘焦点移到显示中的列表（隐藏的一层已被禁用）
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MessagesPageViewModel.IsHelperOpen) || _vm is null)
            return;
        var target = _vm.IsHelperOpen ? HelperList : MainList;
        // 等样式把目标层重新启用后再移动焦点
        Dispatcher.UIThread.Post(() =>
        {
            // ListBox 本身不接收焦点，焦点放在选中项的容器上；没有选中项时，
            // 子列表取第一项，主列表取群助手条目（刚才点击的位置，避免列表滚动）
            var index = target.SelectedIndex;
            if (index < 0)
                index = target == MainList ? _vm.Conversations.ToList().FindIndex(c => c.IsHelper) : 0;
            if (index >= 0 && target.ContainerFromIndex(index) is { } container)
                container.Focus(NavigationMethod.Directional);
        }, DispatcherPriority.Input);
    }
}
