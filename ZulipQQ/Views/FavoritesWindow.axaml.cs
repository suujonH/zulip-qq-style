using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using ZulipQQ.Controls;
using ZulipQQ.Core;
using ZulipQQ.Services;
using ZulipQQ.ViewModels;

namespace ZulipQQ.Views;

/// <summary>收藏窗口：只允许打开一个。滚动到底部附近时加载更早的收藏。</summary>
public partial class FavoritesWindow : Window, IAccountSwitchWindow
{
    private const double LoadMoreDistance = 300;

    private static FavoritesWindow? _current;

    private FavoritesViewModel? _vm;
    private MainViewModel? _main;

    public FavoritesWindow()
    {
        InitializeComponent();
        Scroller.ScrollChanged += (_, _) => CheckLoadMore();
        AddHandler(MessageContent.LinkClickedEvent, OnLinkClicked);
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        Accounts.AccountSelected += OnAccountSelected;
    }

    /// <summary>打开收藏窗口；已打开时把它还原并激活（打开中的是其他账号时切换到这个账号）。</summary>
    public static void ShowSingle(Window? owner, MainViewModel main)
    {
        if (_current is { } existing)
        {
            if (SessionWindow.Of(existing) != main.Session)
                existing.ShowAccount(main);
            if (existing.WindowState == WindowState.Minimized)
                existing.WindowState = WindowState.Normal;
            existing.Activate();
            return;
        }

        var window = new FavoritesWindow();
        window.Attach(main);
        _current = window;
        // 与聊天记录窗口相同：不设 Owner，避免“定位到聊天”后挡住主窗口；手动居中于主窗口
        WindowPlacement.CenterOn(window, owner);
        window.Show();
        window._vm!.Start();
        Dispatcher.UIThread.Post(() => window.SearchBox.Focus());
    }

    /// <summary>显示的账号退出：切换到另一个在线账号；没有其他在线账号时返回 false（由调用方关闭窗口）。</summary>
    public bool SwitchAway(ZulipSession ending)
    {
        if (AccountHubViewModel.Instance.Online.FirstOrDefault(a => a.Session != ending && a.Main is not null) is not { Main: { } main })
            return false;
        ShowAccount(main);
        return true;
    }

    // 窗口中切换账号：换成该账号的收藏，回到顶部
    private void ShowAccount(MainViewModel main)
    {
        Attach(main);
        Scroller.Offset = default;
        _vm!.Start();
    }

    // 绑定账号：换掉视图模型（不加载）
    private void Attach(MainViewModel main)
    {
        DetachViewModel();
        _main = main;
        var vm = new FavoritesViewModel(main.Session, main);
        _vm = vm;
        SessionWindow.Bind(this, main.Session);
        DataContext = vm;
        vm.MainWindowRequested += OnMainWindowRequested;
        vm.CopyRequested += OnCopyRequested;
        Accounts.Show(main.Session);
    }

    private void DetachViewModel()
    {
        if (_vm is null)
            return;
        _vm.MainWindowRequested -= OnMainWindowRequested;
        _vm.CopyRequested -= OnCopyRequested;
        _vm.Close();
        _vm = null;
    }

    private void OnAccountSelected(object? sender, AccountEntryViewModel entry)
    {
        if (entry.Main is { } main)
            ShowAccount(main);
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        DetachViewModel();
        if (_current == this)
            _current = null;
    }

    // 定位到聊天：主窗口切换到本窗口显示的账号
    private void OnMainWindowRequested(object? sender, EventArgs e) => App.Current.ShowMainWindow(_main);

    private async void OnCopyRequested(object? sender, string text)
    {
        if (Clipboard is not { } clipboard)
            return;
        try
        {
            await clipboard.SetTextAsync(text);
            _vm?.ShowToast("已复制");
        }
        catch (Exception ex)
        {
            Log.Warn("复制失败", ex);
        }
    }

    // Esc 关闭窗口；菜单、弹出层打开时由它们自己处理
    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
            return;
        e.Handled = true;
        Close();
    }

    private void OnItemDoubleTapped(object? sender, TappedEventArgs e)
    {
        if ((sender as Control)?.DataContext is FavoriteItem item)
            _vm?.Locate(item);
    }

    private void CheckLoadMore()
    {
        if (_vm is not null && Scroller.Extent.Height - Scroller.Viewport.Height - Scroller.Offset.Y < LoadMoreDistance)
            _vm.LoadOlder();
    }

    // 点击缩略图：在图片查看器中依次浏览列表中的全部缩略图
    private void OnLinkClicked(object? sender, MessageLinkEventArgs e)
    {
        if (_vm is not { } vm || e.Target is not ImageTarget image)
            return;
        e.Handled = true;
        var images = vm.Items.Reverse().Where(i => i.Thumbnail is not null).Select(i => (i.MessageId, i.Thumbnail!)).ToList();
        var messageId = ((e.Source as StyledElement)?.DataContext as FavoriteItem)?.MessageId;
        var (list, index) = MessageLinks.LocateImage(images, new InlineImage(image.Src, image.Href), messageId);
        ImageViewerWindow.Show(this, vm.Session, list, index);
    }
}
