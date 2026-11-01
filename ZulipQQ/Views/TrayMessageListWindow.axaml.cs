using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using ZulipQQ.Services;
using ZulipQQ.ViewModels;

namespace ZulipQQ.Views;

/// <summary>
/// 托盘提醒列表：托盘图标闪烁时鼠标悬停显示在图标上方（任务栏在顶部时在下方），列出有未读的会话与消息提醒。
/// 鼠标离开图标和列表一会儿后关闭；停止闪烁时由 App 关闭。不抢占焦点、不显示在任务栏。同时只有一个。
/// </summary>
public partial class TrayMessageListWindow : Window
{
    private const int Gap = 8;
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(150);
    // 鼠标离开图标和列表超过该时间后关闭
    private static readonly TimeSpan LeaveDelay = TimeSpan.FromMilliseconds(500);
    // 刷新倒计时
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(15);

    private static TrayMessageListWindow? _current;

    private readonly DispatcherTimer _pollTimer = new() { Interval = PollInterval };
    private readonly DispatcherTimer _refreshTimer = new() { Interval = RefreshInterval };
    private Func<IReadOnlyList<TrayMessageItemViewModel>> _build = () => [];
    // 托盘图标的位置（物理像素）
    private PixelRect _anchor;
    private TimeSpan _outside;

    public TrayMessageListWindow()
    {
        InitializeComponent();
        _pollTimer.Tick += (_, _) => Poll();
        _refreshTimer.Tick += (_, _) => Reload();
        Opened += (_, _) => Arrange();
        SizeChanged += (_, _) => Arrange();
        Closed += (_, _) =>
        {
            _pollTimer.Stop();
            _refreshTimer.Stop();
            if (_current == this)
                _current = null;
        };
    }

    /// <summary>
    /// 显示列表（已显示时更新位置并刷新）。<paramref name="iconRect"/> 为托盘图标的位置，取不到时用鼠标位置。
    /// 没有列表项时不显示。
    /// </summary>
    public static void Show(Func<IReadOnlyList<TrayMessageItemViewModel>> build, PixelRect? iconRect)
    {
        var cursor = Win32NotifyIcon.GetCursorPosition();
        var anchor = iconRect ?? new PixelRect(cursor.X - 8, cursor.Y - 8, 16, 16);
        if (_current is { } existing)
        {
            existing._anchor = anchor;
            existing._outside = TimeSpan.Zero;
            existing.Reload();
            return;
        }
        var items = build();
        if (items.Count == 0)
            return;
        // 通知弹窗与列表都在屏幕右下角：关闭弹窗并丢弃排队的通知（它们都在列表中）
        NotificationPopupWindow.CloseAll();
        var window = new TrayMessageListWindow { _build = build, _anchor = anchor };
        window.ItemsView.ItemsSource = items;
        // 先放在图标处，打开后按实际大小重新定位
        window.Position = new PixelPoint(anchor.X, anchor.Y);
        _current = window;
        window.Show();
        window._pollTimer.Start();
        window._refreshTimer.Start();
    }

    public static bool IsOpen => _current is not null;

    /// <summary>未读或消息提醒变化：重新生成列表，没有列表项时关闭。</summary>
    public static void Refresh() => _current?.Reload();

    public static void CloseCurrent() => _current?.Close();

    private void Reload()
    {
        var items = _build();
        if (items.Count == 0)
        {
            Close();
            return;
        }
        ItemsView.ItemsSource = items;
    }

    // 水平方向以图标为中心；图标在屏幕下半部分时显示在图标上方，否则在下方；不超出工作区
    private void Arrange()
    {
        if ((Screens.ScreenFromPoint(_anchor.Center) ?? Screens.Primary) is not { } screen)
            return;
        var area = screen.WorkingArea;
        var scale = screen.Scaling;
        var w = (int)Math.Round(ClientSize.Width * scale);
        var h = (int)Math.Round(ClientSize.Height * scale);
        var gap = (int)Math.Round(Gap * scale);
        var x = Math.Clamp(_anchor.Center.X - w / 2, area.X, Math.Max(area.X, area.Right - w));
        var y = _anchor.Center.Y > area.Center.Y
            ? Math.Min(_anchor.Y, area.Bottom) - h - gap
            : Math.Max(_anchor.Bottom, area.Y) + gap;
        y = Math.Clamp(y, area.Y, Math.Max(area.Y, area.Bottom - h));
        Position = new PixelPoint(x, y);
    }

    // 系统不通知鼠标离开托盘图标：定时检查鼠标是否还在图标或列表上
    private void Poll()
    {
        var cursor = Win32NotifyIcon.GetCursorPosition();
        var scale = Screens.ScreenFromWindow(this)?.Scaling ?? 1;
        var self = new PixelRect(Position, PixelSize.FromSize(ClientSize, scale));
        if (Inflate(_anchor, 2).Contains(cursor) || Inflate(self, 2).Contains(cursor))
        {
            _outside = TimeSpan.Zero;
            return;
        }
        _outside += PollInterval;
        if (_outside >= LeaveDelay)
            Close();
    }

    private static PixelRect Inflate(PixelRect r, int d) => new(r.X - d, r.Y - d, r.Width + d * 2, r.Height + d * 2);

    private void OnItemReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Left || (sender as Control)?.DataContext is not TrayMessageItemViewModel item)
            return;
        Close();
        item.Activate();
    }
}
