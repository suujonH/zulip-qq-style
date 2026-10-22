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

    // 群消息设置的四个级别与菜单文字（与 QQ 一致）
    private static readonly (DndLevel Level, string Text)[] GroupLevels =
    [
        (DndLevel.Notify, "允许消息提醒"),
        (DndLevel.Silent, "接收消息但不提醒"),
        (DndLevel.Assistant, "收进群助手且不提醒"),
        (DndLevel.Blocked, "屏蔽群消息"),
    ];

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

    /// <summary>会话右键菜单：置顶、标为已读 / 未读、免打扰级别、从消息列表中移除。群助手条目只有置顶。</summary>
    private void OnContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (_vm is null || (e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true) is not
                { DataContext: ConversationItemViewModel item } container)
            return;
        e.Handled = true;
        var vm = _vm;
        var menu = new ContextMenu();
        menu.Items.Add(MenuItem(item.IsPinned ? "取消置顶" : "置顶", () => vm.TogglePin(item)));
        if (!item.IsHelper)
        {
            if (vm.CanMarkRead(item))
                menu.Items.Add(MenuItem("标为已读", () => _ = vm.MarkReadAsync(item)));
            if (vm.CanMarkUnread(item))
                menu.Items.Add(MenuItem("标为未读", () => _ = vm.MarkUnreadAsync(item)));
            menu.Items.Add(new Separator());
            if (MessagesPageViewModel.HasGroupLevels(item))
            {
                var levels = new MenuItem { Header = "群消息设置" };
                foreach (var (level, text) in GroupLevels)
                {
                    var option = MenuItem(text, () => _ = vm.SetLevelAsync(item, level));
                    option.ToggleType = MenuItemToggleType.Radio;
                    option.IsChecked = item.Level == level;
                    levels.Items.Add(option);
                }
                menu.Items.Add(levels);
            }
            else
            {
                var silent = MenuItem("消息免打扰",
                    () => _ = vm.SetLevelAsync(item, item.Level == DndLevel.Notify ? DndLevel.Silent : DndLevel.Notify));
                silent.ToggleType = MenuItemToggleType.CheckBox;
                silent.IsChecked = item.Level != DndLevel.Notify;
                menu.Items.Add(silent);
            }
            menu.Items.Add(new Separator());
            menu.Items.Add(MenuItem("从消息列表中移除", () => _ = vm.RemoveAsync(item)));
        }
        menu.Open(container);
    }

    private static MenuItem MenuItem(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
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
