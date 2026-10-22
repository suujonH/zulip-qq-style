using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using ZulipQQ.Core;
using ZulipQQ.ViewModels;

namespace ZulipQQ.Views;

/// <summary>文件窗口：只允许打开一个，从不同入口再次打开时切换到对应视图。</summary>
public partial class FilesWindow : Window, ISessionWindow
{
    private const double LoadMoreDistance = 300;

    private static FilesWindow? _current;

    private FilesViewModel? _vm;

    public FilesWindow()
    {
        InitializeComponent();
        Scroller.ScrollChanged += (_, _) => CheckLoadMore();
        FilterButton.Click += (_, _) => ShowFilterMenu();
        SortButton.Click += (_, _) => ShowSortMenu();
        UploadButton.Click += (_, _) => _ = PickUploadAsync();
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
    }

    /// <summary>
    /// 打开文件窗口并切换到 <paramref name="view"/>；<paramref name="key"/> 为要选中的会话
    /// （群文件按钮传入话题，私聊的“文件传输列表”传入私聊）。已打开时还原并激活。
    /// </summary>
    public static void ShowSingle(Window? owner, MainViewModel main, FilesView view = FilesView.All, ConversationKey? key = null)
    {
        if (_current is { _vm: { } existing })
        {
            WindowPlacement.BringToFront(_current);
            existing.Navigate(view, key);
            return;
        }

        var vm = new FilesViewModel(main.Session, main);
        var window = new FilesWindow { DataContext = vm, _vm = vm };
        vm.MainWindowRequested += OnMainWindowRequested;
        vm.SaveAsRequested += window.OnSaveAsRequested;
        _current = window;
        WindowPlacement.CenterOn(window, owner);
        window.Show();
        vm.Navigate(view, key);
        Dispatcher.UIThread.Post(() => window.SearchBox.Focus());
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        if (_vm is not null)
        {
            _vm.MainWindowRequested -= OnMainWindowRequested;
            _vm.SaveAsRequested -= OnSaveAsRequested;
            _vm.Close();
        }
        if (_current == this)
            _current = null;
    }

    private static void OnMainWindowRequested(object? sender, EventArgs e) => App.Current.ShowMainWindow();

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
            _vm?.Open(entry);
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
