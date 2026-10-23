using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using ZulipQQ.Services;

namespace ZulipQQ.Views;

/// <summary>
/// 新消息通知弹窗：主屏幕工作区右下角，最多同时显示 3 个，新的在最下面、旧的向上堆叠。
/// 6 秒后自动关闭，鼠标悬停时暂停计时；点击打开对应会话。不抢占焦点、不显示在任务栏。
/// </summary>
public partial class NotificationPopupWindow : Window, ISessionWindow
{
    private const int MaxOpen = 3;
    private const double Gap = 12;

    // 按打开顺序排列，最后一个是最新的
    private static readonly List<NotificationPopupWindow> OpenPopups = [];

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(6) };
    private Action? _activate;

    public NotificationPopupWindow()
    {
        InitializeComponent();
        _timer.Tick += (_, _) => Close();
        PointerEntered += (_, _) => _timer.Stop();
        PointerExited += (_, _) => _timer.Start();
        CloseButton.Click += (_, _) => Close();
        Closed += (_, _) =>
        {
            _timer.Stop();
            if (OpenPopups.Remove(this))
                Arrange();
        };
    }

    /// <summary>显示一条通知。同一槽位（同一会话，或都是免打扰的会话）已有通知时替换它；超过 3 个时关闭最旧的。</summary>
    public static void Show(NotificationRequest request, Action activate)
    {
        foreach (var same in OpenPopups.Where(w => w.DataContext is NotificationRequest r && r.Slot.Equals(request.Slot)).ToList())
            same.Close();
        while (OpenPopups.Count >= MaxOpen)
            OpenPopups[0].Close();

        var popup = new NotificationPopupWindow { DataContext = request, _activate = activate };
        popup.AvatarView.Fill = request.AvatarColor is { } color && Color.TryParse(color, out var parsed)
            ? new SolidColorBrush(parsed)
            : null;
        OpenPopups.Add(popup);
        popup.Show();
        popup._timer.Start();
        Arrange();
    }

    // 从工作区右下角往上排列，最新的在最下面
    private static void Arrange()
    {
        if (OpenPopups.Count == 0 || OpenPopups[0].Screens.Primary is not { } screen)
            return;
        var area = screen.WorkingArea;
        var scale = screen.Scaling;
        for (var i = 0; i < OpenPopups.Count; i++)
        {
            var popup = OpenPopups[OpenPopups.Count - 1 - i];
            var x = area.Right - (int)Math.Round((popup.Width + Gap) * scale);
            var y = area.Bottom - (int)Math.Round(((popup.Height + Gap) * (i + 1)) * scale);
            popup.Position = new PixelPoint(x, y);
        }
    }

    // 点击关闭按钮以外的地方：打开会话
    private void OnPanelReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Left)
            return;
        var activate = _activate;
        Close();
        activate?.Invoke();
    }
}
