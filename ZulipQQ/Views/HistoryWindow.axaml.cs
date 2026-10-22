using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;
using ZulipQQ.Controls;
using ZulipQQ.Core;
using ZulipQQ.Services;
using ZulipQQ.ViewModels;

namespace ZulipQQ.Views;

/// <summary>聊天记录窗口：每个会话最多一个，已打开时激活。滚动到底部附近时加载更早的消息。</summary>
public partial class HistoryWindow : Window, ISessionWindow
{
    private const double LoadMoreDistance = 300;

    private static readonly Dictionary<ConversationKey, HistoryWindow> Open = [];

    private HistoryViewModel? _vm;

    public HistoryWindow()
    {
        InitializeComponent();
        // 文件卡片由代码构造，不能写在 XAML 模板中
        DataTemplates.Add(new FuncDataTemplate<HistoryFileItem>((item, _) => FileRow(item)));
        Scroller.ScrollChanged += (_, _) => CheckLoadMore();
        FilterButton.Click += (_, _) => ShowFilterMenu();
        AddHandler(MessageContent.LinkClickedEvent, OnLinkClicked);
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
    }

    /// <summary>打开会话的聊天记录；<paramref name="query"/> 不为 null 时预填搜索词。</summary>
    public static void Show(Window? owner, MainViewModel main, ConversationKey key, string? query = null)
    {
        if (Open.TryGetValue(key, out var existing))
        {
            if (existing.WindowState == WindowState.Minimized)
                existing.WindowState = WindowState.Normal;
            if (query is not null)
                existing._vm?.SetQuery(query);
            existing.Activate();
            return;
        }

        var vm = new HistoryViewModel(main.Session, main, key, query);
        var window = new HistoryWindow { DataContext = vm };
        window._vm = vm;
        // 文件卡片从这里取下载状态
        MessageContent.SetFiles(window, main.Session.Files);
        vm.MainWindowRequested += OnMainWindowRequested;
        vm.Rekeyed += window.OnRekeyed;
        Open[key] = window;
        WindowPlacement.CenterOn(window, owner);
        window.Show();
        vm.Start();
        Dispatcher.UIThread.Post(() => window.SearchBox.Focus());
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        if (_vm is null)
            return;
        _vm.MainWindowRequested -= OnMainWindowRequested;
        _vm.Rekeyed -= OnRekeyed;
        _vm.Close();
        if (Open.TryGetValue(_vm.Key, out var w) && w == this)
            Open.Remove(_vm.Key);
    }

    // 话题被移动或改名：索引换成新键。新键已有窗口时（极少见）保留那一个的索引
    private void OnRekeyed(object? sender, ConversationKey oldKey)
    {
        if (Open.TryGetValue(oldKey, out var w) && w == this)
            Open.Remove(oldKey);
        if (_vm is not null)
            Open.TryAdd(_vm.Key, this);
    }

    private static void OnMainWindowRequested(object? sender, EventArgs e) => App.Current.ShowMainWindow();

    // Esc 关闭窗口；菜单、弹出层打开时由它们自己处理
    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
            return;
        e.Handled = true;
        Close();
    }

    private void CheckLoadMore()
    {
        if (_vm is not null && Scroller.Extent.Height - Scroller.Viewport.Height - Scroller.Offset.Y < LoadMoreDistance)
            _vm.LoadOlder();
    }

    private Control FileRow(HistoryFileItem item)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto"), Margin = new Thickness(20, 8), Background = Avalonia.Media.Brushes.Transparent };
        grid.Classes.Add("historyRow");
        var card = new FileCard(item.Link, item.IsVideo) { HorizontalAlignment = HorizontalAlignment.Left };
        var locate = new Button { Command = item.LocateCommand, Margin = new Thickness(8, 0, 0, 0) };
        locate.Classes.Add("locate");
        ToolTip.SetTip(locate, "定位到聊天");
        locate.Content = new TdIcon { Icon = this.FindResource("Icon.ChatBubble") as TdIconData };
        Grid.SetColumn(locate, 1);
        grid.Children.Add(card);
        grid.Children.Add(locate);
        return grid;
    }

    /// <summary>筛选菜单：按成员（子菜单列出成员）、按日期（服务器支持时）、清除筛选。</summary>
    private async void ShowFilterMenu()
    {
        if (_vm is not { } vm)
            return;
        IReadOnlyList<ChatMemberItem> members;
        try
        {
            members = await vm.GetMembersAsync();
        }
        catch (Exception ex)
        {
            if (vm.Session.IsDisposed)
                return;
            Log.Warn("加载成员失败", ex);
            vm.ShowToast("加载成员失败");
            return;
        }

        var menu = new ContextMenu { Placement = PlacementMode.BottomEdgeAlignedRight };
        var byMember = new MenuItem { Header = "按成员" };
        foreach (var member in members)
        {
            var item = ConversationMenus.Item(member.DisplayName, () => vm.FilterBySender(member.UserId, member.DisplayName));
            item.ToggleType = MenuItemToggleType.Radio;
            item.IsChecked = vm.SenderFilter?.Id == member.UserId;
            byMember.Items.Add(item);
        }
        menu.Items.Add(byMember);
        if (vm.CanFilterByDate)
            menu.Items.Add(ConversationMenus.Item("按日期…", ShowDatePicker));
        if (vm.HasFilter)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(ConversationMenus.Item("清除筛选", () => vm.ClearFilterCommand.Execute(null)));
        }
        menu.Open(FilterButton);
    }

    private void ShowDatePicker()
    {
        if (_vm is not { } vm)
            return;
        var calendar = new Calendar
        {
            DisplayDateEnd = DateTime.Today,
            SelectedDate = vm.DateFilter,
            DisplayDate = vm.DateFilter ?? DateTime.Today,
        };
        var flyout = new Flyout { Content = calendar, Placement = PlacementMode.BottomEdgeAlignedRight };
        calendar.SelectedDatesChanged += (_, _) =>
        {
            if (calendar.SelectedDate is not { } date)
                return;
            flyout.Hide();
            vm.FilterByDate(date);
        };
        flyout.ShowAt(FilterButton);
    }

    private void OnLinkClicked(object? sender, MessageLinkEventArgs e)
    {
        if (_vm is not { } vm)
            return;
        e.Handled = true;
        switch (e.Target)
        {
            case MentionTarget mention:
                ChatView.ShowCard(
                    close => vm.Session.Users.Get(mention.UserId) is null
                        ? null
                        : new UserCardViewModel(vm.Session, mention.UserId, k => _ = vm.OpenChatAsync(k, null), close),
                    e.Source as Control ?? Scroller, PlacementMode.RightEdgeAlignedTop);
                break;
            case ImageTarget image:
                var (images, index) = vm.CollectImages(new InlineImage(image.Src, image.Href), MessageIdOf(e.Source));
                ImageViewerWindow.Show(this, vm.Session, images, index);
                break;
            case UrlTarget url:
                _ = MessageLinks.OpenUrlAsync(vm.Session, vm.OpenChatAsync, url.Href);
                break;
            case FileTarget file:
                _ = MessageLinks.HandleFileAsync(vm.Session, file, vm.Key, MessageIdOf(e.Source) ?? 0, this, vm.ShowToast);
                break;
        }
    }

    private static long? MessageIdOf(object? source) => (source as StyledElement)?.DataContext switch
    {
        HistoryMessageItem m => m.MessageId,
        HistoryImageItem i => i.MessageId,
        HistoryFileItem f => f.MessageId,
        _ => null,
    };
}
