using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using ZulipQQ.Core;
using ZulipQQ.Services;
using ZulipQQ.ViewModels;

namespace ZulipQQ.Views;

/// <summary>文件窗口：只允许打开一个，从不同入口再次打开时切换到对应视图。</summary>
public partial class FilesWindow : Window, IAccountSwitchWindow
{
    private const double LoadMoreDistance = 300;

    private static FilesWindow? _current;

    private FilesViewModel? _vm;
    private MainViewModel? _main;

    public FilesWindow()
    {
        InitializeComponent();
        Scroller.ScrollChanged += (_, _) => CheckLoadMore();
        FilterButton.Click += (_, _) => ShowFilterMenu();
        SortButton.Click += (_, _) => ShowSortMenu();
        UploadButton.Click += (_, _) => _ = PickUploadAsync();
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        Accounts.AccountSelected += OnAccountSelected;
    }

    /// <summary>
    /// 打开文件窗口并切换到 <paramref name="view"/>；<paramref name="key"/> 为要选中的会话
    /// （群文件按钮传入话题，私聊的“文件传输列表”传入私聊）。已打开时还原并激活。
    /// </summary>
    public static void ShowSingle(Window? owner, MainViewModel main, FilesView view = FilesView.All, ConversationKey? key = null)
    {
        if (_current is { } existing)
        {
            // 打开中的是其他账号：切换到这个账号
            if (SessionWindow.Of(existing) != main.Session)
                existing.Attach(main);
            WindowPlacement.BringToFront(existing);
            existing._vm!.Navigate(view, key);
            return;
        }

        var window = new FilesWindow();
        window.Attach(main);
        _current = window;
        WindowPlacement.CenterOn(window, owner);
        window.Show();
        window._vm!.Navigate(view, key);
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

    // 窗口中切换账号：换成该账号的文件，显示全部文件
    private void ShowAccount(MainViewModel main)
    {
        Attach(main);
        _vm!.Navigate(FilesView.All, null);
    }

    // 绑定账号：换掉视图模型（不加载），回到顶部
    private void Attach(MainViewModel main)
    {
        DetachViewModel();
        _main = main;
        var vm = new FilesViewModel(main.Session, main);
        _vm = vm;
        SessionWindow.Bind(this, main.Session);
        DataContext = vm;
        vm.MainWindowRequested += OnMainWindowRequested;
        vm.SaveAsRequested += OnSaveAsRequested;
        Accounts.Show(main.Session);
        Scroller.Offset = default;
    }

    private void DetachViewModel()
    {
        if (_vm is null)
            return;
        _vm.MainWindowRequested -= OnMainWindowRequested;
        _vm.SaveAsRequested -= OnSaveAsRequested;
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

    private async void OnSaveAsRequested(object? sender, FileEntry entry)
    {
        if (_vm is not { } vm)
            return;
        if (!await vm.Session.Files.SaveAsAsync(entry.Link, this) && !vm.Session.IsDisposed)
            vm.ShowToast("文件下载失败");
    }

    // Esc 关闭窗口；菜单、弹出层打开时由它们自己处理
    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
            return;
        e.Handled = true;
        Close();
    }

    private void OnRowTapped(object? sender, TappedEventArgs e)
    {
        if ((sender as Control)?.DataContext is FileEntry entry)
            _vm?.Click(entry);
    }

    private void CheckLoadMore()
    {
        if (_vm is not null && Scroller.Extent.Height - Scroller.Viewport.Height - Scroller.Offset.Y < LoadMoreDistance)
            _vm.LoadOlder();
    }

    private void ShowFilterMenu()
    {
        if (_vm is not { } vm)
            return;
        var menu = new ContextMenu { Placement = PlacementMode.BottomEdgeAlignedRight };
        menu.Items.Add(Radio("全部", vm.Filter is null, () => vm.SetFilter(null)));
        foreach (var kind in Enum.GetValues<FileKind>())
            menu.Items.Add(Radio(FilesViewModel.KindName(kind), vm.Filter == kind, () => vm.SetFilter(kind)));
        menu.Open(FilterButton);
    }

    private void ShowSortMenu()
    {
        if (_vm is not { } vm)
            return;
        var menu = new ContextMenu { Placement = PlacementMode.BottomEdgeAlignedRight };
        foreach (var sort in Enum.GetValues<FileSort>())
            menu.Items.Add(Radio(FilesViewModel.SortName(sort), vm.Sort == sort, () => vm.SetSort(sort)));
        menu.Open(SortButton);
    }

    private static MenuItem Radio(string header, bool isChecked, Action action)
    {
        var item = ConversationMenus.Item(header, action);
        item.ToggleType = MenuItemToggleType.Radio;
        item.IsChecked = isChecked;
        return item;
    }

    private async System.Threading.Tasks.Task PickUploadAsync()
    {
        if (_vm is not { CanUpload: true } vm)
            return;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "上传文件", AllowMultiple = false });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path)
            await vm.UploadAsync(path);
    }
}
