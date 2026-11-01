using System;
using Avalonia;
using Avalonia.Controls;
using ZulipQQ.Services;

namespace ZulipQQ.Views;

/// <summary>请求详情窗口：只有一个，点击其它请求时切换显示的内容。第一次打开时放在设置窗口的右侧。</summary>
public partial class RequestDetailWindow : Window, ISessionWindow
{
    private static RequestDetailWindow? _current;

    public RequestDetailWindow()
    {
        InitializeComponent();
    }

    public static void ShowFor(RequestLogEntry entry, Window? owner)
    {
        if (_current is { } existing)
        {
            existing.DataContext = entry;
            WindowPlacement.BringToFront(existing);
            return;
        }
        var window = new RequestDetailWindow { DataContext = entry };
        _current = window;
        window.Closed += (_, _) => _current = null;
        PlaceBeside(window, owner);
        window.Show();
    }

    // 与设置窗口顶端对齐放在右侧；右侧放不下时放在左侧，都放不下时保持默认位置
    private static void PlaceBeside(Window window, Window? owner)
    {
        if (owner?.Screens.ScreenFromWindow(owner) is not { } screen)
            return;
        var scale = owner.RenderScaling;
        var gap = (int)(8 * scale);
        var width = (int)(window.Width * scale);
        var area = screen.WorkingArea;
        var right = owner.Position.X + (int)(owner.Bounds.Width * scale) + gap;
        var left = owner.Position.X - gap - width;
        int x;
        if (right + width <= area.Right)
            x = right;
        else if (left >= area.X)
            x = left;
        else
            return;
        window.Height = Math.Min(Math.Max(owner.Bounds.Height, window.MinHeight), area.Height / scale);
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Position = new PixelPoint(x, Math.Max(owner.Position.Y, area.Y));
    }
}
