using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using ZulipQQ.Core;
using ZulipQQ.Services;

namespace ZulipQQ.Controls;

/// <summary>
/// 聊天中的文件卡片：文件名、大小与下载状态、类型图标。点击打开，右键可另存为或打开所在文件夹。
/// 下载状态来自 <see cref="MessageContent.FilesProperty"/>（由聊天视图设置，向下继承）。
/// </summary>
public class FileCard : Border
{
    private static readonly Cursor HandCursor = new(StandardCursorType.Hand);

    private readonly UploadLink _link;
    private readonly TextBlock _status;
    private FileService? _files;
    private long? _size;
    private bool _sizeRequested;

    public FileCard(UploadLink link, bool isVideo)
    {
        _link = link;
        Classes.Add("fileCard");
        Cursor = HandCursor;
        Width = 240;
        Padding = new Thickness(12, 10);
        CornerRadius = new CornerRadius(8);
        BorderThickness = new Thickness(1);
        Bind(BackgroundProperty, this.GetResourceObservable("CardBrush"));
        Bind(BorderBrushProperty, this.GetResourceObservable("DividerBrush"));

        var name = new TextBlock
        {
            Text = link.FileName,
            FontSize = 14,
            TextWrapping = TextWrapping.Wrap,
            MaxLines = 2,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        _status = new TextBlock { FontSize = 12, Margin = new Thickness(0, 6, 0, 0) };
        _status.Bind(TextBlock.ForegroundProperty, _status.GetResourceObservable("TextSecondaryBrush"));

        var icon = new TdIcon { Width = 32, Height = 32 };
        icon.Bind(TdIcon.IconProperty, icon.GetResourceObservable(isVideo ? "Icon.Video" : "Icon.File"));
        icon.Bind(TdIcon.ForegroundProperty, icon.GetResourceObservable("BrandBrush"));

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { name, _status } };
        Grid.SetColumn(icon, 1);
        icon.Margin = new Thickness(12, 0, 0, 0);
        icon.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(text);
        grid.Children.Add(icon);
        Child = grid;
        ContextRequested += OnContextRequested;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _files = GetValue(MessageContent.FilesProperty);
        if (_files is not null)
            _files.StateChanged += OnStateChanged;
        UpdateStatus();
        RequestSize();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_files is not null)
            _files.StateChanged -= OnStateChanged;
        _files = null;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (e.InitialPressMouseButton != MouseButton.Left)
            return;
        e.Handled = true;
        Raise(FileAction.Open);
    }

    private void OnContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        var menu = new ContextMenu();
        menu.Items.Add(MenuItem("打开", FileAction.Open));
        menu.Items.Add(MenuItem("另存为", FileAction.SaveAs));
        if (_files?.GetState(_link.Path) == DownloadState.Downloaded)
            menu.Items.Add(MenuItem("打开所在文件夹", FileAction.RevealInFolder));
        menu.Open(this);
        e.Handled = true;
    }

    private MenuItem MenuItem(string header, FileAction action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => Raise(action);
        return item;
    }

    private void Raise(FileAction action) =>
        RaiseEvent(new MessageLinkEventArgs(MessageContent.LinkClickedEvent, new FileTarget(_link, action)));

    private void OnStateChanged(object? sender, string path)
    {
        if (path == _link.Path)
            UpdateStatus();
    }

    private async void RequestSize()
    {
        if (_sizeRequested || _files is null)
            return;
        _sizeRequested = true;
        _size = await _files.GetSizeAsync(_link.Path);
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        var state = _files?.GetState(_link.Path) ?? DownloadState.NotDownloaded;
        var stateText = state switch
        {
            DownloadState.Downloading => _files?.GetProgress(_link.Path) is { } p ? $"下载中 {p}%" : "下载中",
            DownloadState.Downloaded => "已下载",
            _ => "未下载",
        };
        _status.Text = _size is { } size ? $"{FormatSize(size)} · {stateText}" : stateText;
    }

    public static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0
            ? $"{bytes}B"
            : value.ToString(value >= 100 ? "0" : "0.0", CultureInfo.InvariantCulture) + units[unit];
    }
}
