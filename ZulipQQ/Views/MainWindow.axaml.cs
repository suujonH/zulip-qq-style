using System;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using ZulipQQ.Core;
using ZulipQQ.Services;
using ZulipQQ.ViewModels;
using ZulipQQ.ViewModels.Settings;

namespace ZulipQQ.Views;

public partial class MainWindow : Window, ISessionWindow
{
    // 默认模式的初始大小与最小宽度（与 XAML 一致）；经典模式为导航栏加列表的宽度
    private const double NormalWidth = 1090;
    private const double NormalHeight = 694;
    private const double NormalMinWidth = 800;
    private const double ClassicWidth = 52 + 318 + 4;
    private const double ClassicMinWidth = 300;

    private MainViewModel? _vm;
    private bool _classic;
    private EdgeHideController? _edgeHide;

    public MainWindow()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null)
        {
            _vm.Search.HistoryRequested -= OnSearchHistoryRequested;
            _vm.ChatWindowRequested -= OnChatWindowRequested;
            _vm.PropertyChanged -= OnVmPropertyChanged;
        }
        _vm = DataContext as MainViewModel;
        if (_vm is not null)
        {
            _vm.Search.HistoryRequested += OnSearchHistoryRequested;
            _vm.ChatWindowRequested += OnChatWindowRequested;
            _vm.PropertyChanged += OnVmPropertyChanged;
            // 显示之前按保存的模式和位置布局
            _classic = _vm.IsClassicMode;
            ApplyLayout();
            RestoreBounds();
            var local = _vm.Session.Settings.Local;
            var registry = _vm.Session.Registry;
            _edgeHide?.Dispose();
            _edgeHide = new EdgeHideController(this, () => local.EdgeAutoHide);
            // 缩进边缘期间到达的消息保持未读，移回后与窗口被激活时一样标记已读
            _edgeHide.Revealed += (_, _) => registry.OnWindowRevealed(this);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _edgeHide?.Dispose();
        _edgeHide = null;
    }

    private void OnChatWindowRequested(object? sender, ChatWindowRequest e)
    {
        if (_vm is not null)
            ChatWindow.Open(_vm, e.Key, e.Existing, e.FocusMessageId);
    }

    // 切换经典模式：先保存当前模式的位置，再换布局并恢复另一模式保存的位置
    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainViewModel.IsClassicMode) || _vm is null || _vm.IsClassicMode == _classic)
            return;
        SaveBounds();
        _edgeHide?.Reveal(immediate: true);
        _classic = _vm.IsClassicMode;
        if (WindowState != WindowState.Normal)
            WindowState = WindowState.Normal;
        ApplyLayout();
        RestoreBounds();
    }

    private void ApplyLayout()
    {
        BodyGrid.ColumnDefinitions[1].Width = _classic ? GridLength.Star : new GridLength(318);
        BodyGrid.ColumnDefinitions[2].Width = _classic ? new GridLength(0) : GridLength.Star;
        MinWidth = _classic ? ClassicMinWidth : NormalMinWidth;
    }

    /// <summary>保存当前模式下窗口的位置和大小（最大化、最小化时不保存）。</summary>
    private void SaveBounds()
    {
        if (_vm is null || WindowState != WindowState.Normal || Bounds.Width <= 0)
            return;
        // 缩进边缘时保存移回后的位置
        var position = _edgeHide?.RestingPosition ?? Position;
        _vm.Session.Settings.Local.SetMainWindowBounds(_classic,
            new WindowBounds(position.X, position.Y, Bounds.Width, Bounds.Height));
    }

    // 恢复当前模式保存的位置和大小；没有保存过（或所在屏幕已不存在）时只调整大小
    private void RestoreBounds()
    {
        if (_vm is null)
            return;
        var saved = _vm.Session.Settings.Local.GetMainWindowBounds(_classic);
        Width = saved?.Width ?? (_classic ? ClassicWidth : NormalWidth);
        Height = saved?.Height ?? (double.IsNaN(Height) || Height <= 0 ? NormalHeight : Height);
        if (saved is { } b && Screens.All.Any(s => s.WorkingArea.Contains(new PixelPoint(b.X + 40, b.Y + 10))))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = new PixelPoint(b.X, b.Y);
        }
    }

    // 搜索结果中的聊天记录：打开该会话的聊天记录窗口并预填搜索词
    private void OnSearchHistoryRequested(object? sender, (ConversationKey Key, string Query) e)
    {
        if (_vm is not null)
            HistoryWindow.Show(this, _vm, e.Key, e.Query);
    }

    // Esc 关闭搜索结果
    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || _vm is not { Search.IsOpen: true } vm)
            return;
        e.Handled = true;
        vm.Search.Close();
    }

    // 用户关闭主面板时按设置最小化到托盘或退出程序；退出账号、程序退出或系统关机时由 App 关闭，不拦截
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (App.IsEndingSession || e.CloseReason is WindowCloseReason.ApplicationShutdown or WindowCloseReason.OSShutdown)
        {
            SaveBounds();
            return;
        }
        e.Cancel = true;
        App.Current.RequestCloseMain();
    }

    private void OnMinimizeClick(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximizeClick(object? sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnCloseClick(object? sender, RoutedEventArgs e) => App.Current.RequestCloseMain();

    private void OnFavoritesClick(object? sender, RoutedEventArgs e)
    {
        MainMenuButton.Flyout?.Hide();
        if (DataContext is MainViewModel vm)
            FavoritesWindow.ShowSingle(this, vm);
    }

    private void OnFilesClick(object? sender, RoutedEventArgs e)
    {
        MainMenuButton.Flyout?.Hide();
        if (DataContext is MainViewModel vm)
            FilesWindow.ShowSingle(this, vm);
    }

    private void OnPaletteClick(object? sender, RoutedEventArgs e)
    {
        MainMenuButton.Flyout?.Hide();
        if (DataContext is MainViewModel vm)
            SettingsWindow.ShowSingle(this, vm, SettingsPage.Palette);
    }

    private void OnSettingsClick(object? sender, RoutedEventArgs e)
    {
        MainMenuButton.Flyout?.Hide();
        if (DataContext is MainViewModel vm)
            SettingsWindow.ShowSingle(this, vm);
    }

    private void OnLogoutClick(object? sender, RoutedEventArgs e)
    {
        MainMenuButton.Flyout?.Hide();
        (DataContext as MainViewModel)?.LogoutCommand.Execute(null);
    }

    // “+”菜单：没有权限的项不显示，所以每次打开时生成
    private void OnAddClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm || sender is not Control button)
            return;
        var flyout = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedRight };
        flyout.Items.Add(ConversationMenus.Item("发起群聊", vm.ShowGroupChatPicker));
        flyout.Items.Add(ConversationMenus.Item("新建话题", vm.ShowNewTopicDialog));
        flyout.Items.Add(ConversationMenus.Item("加入频道", vm.ShowBrowseChannels));
        if (vm.CanCreateChannel)
            flyout.Items.Add(ConversationMenus.Item("创建频道", vm.ShowCreateChannelDialog));
        flyout.ShowAt(button);
    }
}
