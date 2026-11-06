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
/// 聊天中的文件卡片：文件名、类型图标。点击打开，右键可另存为或打开所在文件夹。
/// 下载状态（右键菜单是否有“打开所在文件夹”）来自 <see cref="MessageContent.FilesProperty"/>（由聊天视图设置，向下继承）。
/// </summary>
public class FileCard : Border
{
    private static readonly Cursor HandCursor = new(StandardCursorType.Hand);

    private readonly UploadLink _link;
    private FileService? _files;

    public FileCard(UploadLink link, bool isVideo)
    {
        _link = link;
        Classes.Add("fileCard");
        Cursor = HandCursor;
        Width = 240;
        Padding = new Thickness(12, 8);
        CornerRadius = new CornerRadius(8);
        BorderThickness = new Thickness(1);
        Bind(BackgroundProperty, this.GetResourceObservable("CardBrush"));
        Bind(BorderBrushProperty, this.GetResourceObservable("DividerBrush"));

        var name = new TextBlock
        {
            Text = link.FileName,
            TextWrapping = TextWrapping.Wrap,
            MaxLines = 2,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        name.Bind(TextBlock.FontSizeProperty, name.GetResourceObservable("FontSizeBody"));

        var icon = new TdIcon { Width = 32, Height = 32 };
        icon.Bind(TdIcon.IconProperty, icon.GetResourceObservable(isVideo ? "Icon.Video" : FileKinds.IconKey(link.Path)));
        icon.Bind(TdIcon.ForegroundProperty, icon.GetResourceObservable("BrandBrush"));

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        name.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(icon, 1);
        icon.Margin = new Thickness(12, 0, 0, 0);
        icon.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(name);
        grid.Children.Add(icon);
        Child = grid;
        ContextRequested += OnContextRequested;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _files = GetValue(MessageContent.FilesProperty);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _files = null;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (e.InitialPressMouseButton != MouseButton.Left)
            return;
        e.Handled = true;
        Raise(FileAction.Click);
    }

    private void OnContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        var menu = new ContextMenu();
        menu.Items.Add(MenuItem("打开", FileAction.Open));
        menu.Items.Add(MenuItem("保存", FileAction.Save));
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
