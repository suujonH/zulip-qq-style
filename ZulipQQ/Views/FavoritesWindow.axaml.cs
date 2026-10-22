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
public partial class FavoritesWindow : Window, ISessionWindow
{
    private const double LoadMoreDistance = 300;

    private static FavoritesWindow? _current;

    private FavoritesViewModel? _vm;

    public FavoritesWindow()
    {
        InitializeComponent();
        Scroller.ScrollChanged += (_, _) => CheckLoadMore();
        AddHandler(MessageContent.LinkClickedEvent, OnLinkClicked);
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
    }

    /// <summary>打开收藏窗口；已打开时把它还原并激活。</summary>
    public static void ShowSingle(Window? owner, MainViewModel main)
    {
        if (_current is not null)
        {
            if (_current.WindowState == WindowState.Minimized)
                _current.WindowState = WindowState.Normal;
            _current.Activate();
            return;
        }

        var vm = new FavoritesViewModel(main.Session, main);
        var window = new FavoritesWindow { DataContext = vm, _vm = vm };
        vm.MainWindowRequested += OnMainWindowRequested;
        vm.CopyRequested += window.OnCopyRequested;
        _current = window;
        // 与聊天记录窗口相同：不设 Owner，避免“定位到聊天”后挡住主窗口；手动居中于主窗口
        WindowPlacement.CenterOn(window, owner);
        window.Show();
        vm.Start();
        Dispatcher.UIThread.Post(() => window.SearchBox.Focus());
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        if (_vm is not null)
        {
            _vm.MainWindowRequested -= OnMainWindowRequested;
            _vm.CopyRequested -= OnCopyRequested;
            _vm.Close();
        }
        if (_current == this)
            _current = null;
    }

    private static void OnMainWindowRequested(object? sender, EventArgs e) => App.Current.ShowMainWindow();

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
