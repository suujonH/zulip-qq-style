using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using ZulipQQ.Core;
using ZulipQQ.ViewModels;

namespace ZulipQQ.Views;

public partial class ContactsPageView : UserControl
{
    // 频道颜色预设（TDesign 色板）
    private static readonly string[] ChannelColors =
        ["#0052D9", "#2BA471", "#E37318", "#D54941", "#8E56DD", "#029CD4", "#E851B3", "#7A7A7A"];

    public ContactsPageView()
    {
        InitializeComponent();
        SectionScroller.AddHandler(ContextRequestedEvent, OnContextRequested);
    }

    private ContactsPageViewModel? Vm => DataContext as ContactsPageViewModel;

    private MainViewModel? Main => TopLevel.GetTopLevel(this)?.DataContext as MainViewModel;

    private void OnFriendManagerClick(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is Window owner && Vm is { } vm)
            FriendManagerWindow.ShowSingle(owner, vm.Session);
    }

    // 单击显示资料（经典模式没有右侧面板，弹出名片），双击打开聊天
    private void OnItemTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not Control { DataContext: ViewModelBase item } control || Vm is not { } vm)
            return;
        if (Main is { IsClassicMode: true })
            ChatView.ShowCard(close => vm.CreateCard(item, close), control, PlacementMode.RightEdgeAlignedTop);
        else
            vm.Select(item);
    }

    private void OnItemDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: ViewModelBase item })
            Vm?.Open(item);
    }

    /// <summary>分组标题和条目的右键菜单。</summary>
    private void OnContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (Vm is not { } vm || Main is not { } main || e.Source is not Visual source)
            return;

        ContextMenu? menu = null;
        Control? anchor = null;
        var ancestors = source.GetSelfAndVisualAncestors().OfType<Control>().ToList();
        if (ancestors.OfType<Border>().FirstOrDefault(b => b.Classes.Contains("contact")) is { } row)
        {
            anchor = row;
            menu = row.DataContext switch
            {
                FriendItemViewModel friend => FriendMenu(vm, friend),
                TopicItemViewModel topic => TopicMenu(main, vm, topic.Key, row),
                _ => null,
            };
        }
        else if (ancestors.OfType<Button>().FirstOrDefault(b => b.Classes.Contains("row")) is
                 { DataContext: ContactSectionViewModel section } header)
        {
            anchor = header;
            menu = SectionMenu(main, vm, section, header);
        }

        if (menu is null || menu.Items.Count == 0 || anchor is null)
            return;
        e.Handled = true;
        menu.Open(anchor);
    }

    private static ContextMenu FriendMenu(ContactsPageViewModel vm, FriendItemViewModel friend)
    {
        var menu = new ContextMenu();
        menu.Items.Add(ConversationMenus.Item("发消息", () => vm.Open(friend)));
        return menu;
    }

    // 话题：发消息、复制链接、置顶、群消息设置、关注，以及有权限的话题管理操作
    private static ContextMenu TopicMenu(MainViewModel main, ContactsPageViewModel vm, TopicKey key, Visual anchor)
    {
        var session = main.Session;
        var menu = new ContextMenu();
        menu.Items.Add(ConversationMenus.Item("发消息", () => _ = main.OpenChatAsync(key)));
        menu.Items.Add(ConversationMenus.CopyLinkItem(main, key, anchor));
        menu.Items.Add(new Separator());
        var pinned = session.Conversations.Get(key)?.IsPinned ?? false;
        menu.Items.Add(ConversationMenus.Item(pinned ? "取消置顶" : "设为置顶", () => session.Conversations.SetPinned(key, !pinned)));
        menu.Items.Add(ConversationMenus.LevelSubmenu(session.Conversations.GetLevel(key), level => _ = vm.SetLevelAsync(key, level)));
        menu.Items.Add(ConversationMenus.FollowItem(main, key));
        ConversationMenus.AppendManagement(menu.Items, main, key);
        return menu;
    }

    private static ContextMenu? SectionMenu(MainViewModel main, ContactsPageViewModel vm, ContactSectionViewModel section, Visual anchor)
    {
        var menu = new ContextMenu();
        switch (section.Kind)
        {
            case ContactSectionKind.AllFriends:
                menu.Items.Add(ConversationMenus.Item("添加分组", vm.ShowAddGroupDialog));
                break;
            case ContactSectionKind.FriendGroup:
                menu.Items.Add(ConversationMenus.Item("添加分组", vm.ShowAddGroupDialog));
                menu.Items.Add(ConversationMenus.Item("重命名分组", section.BeginRename));
                menu.Items.Add(ConversationMenus.Item("删除分组", () => _ = vm.DeleteGroupAsync(section)));
                break;
            case ContactSectionKind.Channel when main.Session.Channels.Get(section.Id) is { } channel:
                var id = channel.StreamId;
                menu.Items.Add(ConversationMenus.Item(channel.PinToTop ? "取消置顶频道" : "置顶频道",
                    () => _ = vm.SetChannelPropertyAsync(id, "pin_to_top", !channel.PinToTop)));
                menu.Items.Add(ConversationMenus.Item(channel.IsMuted ? "取消频道静音" : "频道静音",
                    () => _ = vm.SetChannelPropertyAsync(id, "is_muted", !channel.IsMuted)));
                var colors = new MenuItem { Header = "频道颜色" };
                foreach (var color in ChannelColors)
                {
                    var item = ConversationMenus.Item("", () => _ = vm.SetChannelPropertyAsync(id, "color", color));
                    item.Header = ColorSwatch(color, string.Equals(color, channel.Color, System.StringComparison.OrdinalIgnoreCase));
                    colors.Items.Add(item);
                }
                menu.Items.Add(colors);
                menu.Items.Add(new Separator());
                menu.Items.Add(ConversationMenus.Item("复制频道链接", () => _ = ConversationMenus.CopyAsync(anchor,
                    ZulipLinks.ChannelLink(main.Session.Client.RealmUrl, id, channel.Name), main)));
                menu.Items.Add(ConversationMenus.Item("退订频道", () => _ = vm.UnsubscribeAsync(channel), isDanger: true));
                break;
            default:
                return null;
        }
        return menu;
    }

    // 色块加颜色值；当前颜色加粗
    private static Control ColorSwatch(string color, bool isCurrent) =>
        new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children =
            {
                new Border
                {
                    Width = 16, Height = 16, CornerRadius = new CornerRadius(3),
                    Background = new SolidColorBrush(Color.Parse(color)),
                    VerticalAlignment = VerticalAlignment.Center,
                },
                new TextBlock
                {
                    Text = color, VerticalAlignment = VerticalAlignment.Center,
                    FontWeight = isCurrent ? FontWeight.Bold : FontWeight.Normal,
                },
            },
        };
}
