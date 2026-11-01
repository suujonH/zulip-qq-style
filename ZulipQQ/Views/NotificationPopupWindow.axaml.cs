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
/// 新消息通知弹窗：主屏幕工作区右下角，同时只显示一条，其余排队依次显示。每条至少显示 3 秒，
/// 之后有排队的通知时换成下一条，没有时再显示 3 秒后关闭；鼠标悬停时暂停，移开后重新计时 3 秒。
/// 点击打开对应会话。不抢占焦点、不显示在任务栏。
/// </summary>
public partial class NotificationPopupWindow : Window, ISessionWindow
{
    private const double Gap = 12;
    private static readonly TimeSpan MinDuration = TimeSpan.FromSeconds(3);

    // 等待显示的通知，先到先显示
    private static readonly List<Pending> Queue = [];
    private static NotificationPopupWindow? _current;

    private readonly DispatcherTimer _timer = new() { Interval = MinDuration };
    private Action? _activate;
    // 已显示满 3 秒（悬停时不计）：有排队的通知时立即换成下一条
    private bool _shownEnough;
    private bool _hovered;

    public NotificationPopupWindow()
    {
        InitializeComponent();
        _timer.Tick += (_, _) => OnTick();
        PointerEntered += (_, _) =>
        {
            _hovered = true;
            _timer.Stop();
        };
        PointerExited += (_, _) =>
        {
            _hovered = false;
            _shownEnough = false;
            _timer.Start();
        };
        CloseButton.Click += (_, _) => Close();
        Closed += (_, _) =>
        {
            _timer.Stop();
            if (_current != this)
                return;
            _current = null;
            // 退出账号或程序时关闭了弹窗：丢弃排队的通知，不再显示
            if (App.IsEndingSession)
                Queue.Clear();
            ShowNext();
        };
    }

    /// <summary>
    /// 显示 <paramref name="session"/> 的一条通知；正在显示其他通知时排队。同一账号的同一槽位（同一会话，或都是免打扰的会话）
    /// 已在排队时替换排队中的那条。<paramref name="accountName"/> 不为 null 时（多个账号在线）在正文下方显示账号。
    /// </summary>
    public static void Show(ZulipSession session, string? accountName, NotificationRequest request, Action activate)
    {
        var pending = new Pending(session, accountName, request, activate);
        var same = Queue.FindIndex(p => p.Session == session && p.Request.Slot.Equals(request.Slot));
        if (same >= 0)
            Queue[same] = pending;
        else
            Queue.Add(pending);
        if (_current is null)
            ShowNext();
        else if (_current is { _shownEnough: true, _hovered: false } current)
            current.Close();
    }

    /// <summary>关闭正在显示的通知，并丢弃排队的通知（托盘提醒列表打开时）。</summary>
    public static void CloseAll()
    {
        Queue.Clear();
        _current?.Close();
    }

    // 显示排队中的下一条（跳过已退出的账号的通知）
    private static void ShowNext()
    {
        while (Queue.Count > 0)
        {
            var next = Queue[0];
            Queue.RemoveAt(0);
            if (!next.Session.IsDisposed)
            {
                Open(next);
                return;
            }
        }
    }

    private static void Open(Pending pending)
    {
        var (session, accountName, request, activate) = pending;
        var popup = new NotificationPopupWindow { _activate = activate };
        SessionWindow.Bind(popup, session);
        popup.DataContext = request;
        if (accountName is not null)
        {
            popup.AccountView.Text = accountName;
            popup.AccountView.IsVisible = true;
            popup.Height += 18;
        }
        popup.AvatarView.Fill = request.AvatarColor is { } color && Color.TryParse(color, out var parsed)
            ? new SolidColorBrush(parsed)
            : null;
        _current = popup;
        popup.Show();
        popup._timer.Start();
        if (popup.Screens.Primary is { } screen)
        {
            // 工作区右下角
            var area = screen.WorkingArea;
            var scale = screen.Scaling;
            popup.Position = new PixelPoint(area.Right - (int)Math.Round((popup.Width + Gap) * scale),
                area.Bottom - (int)Math.Round((popup.Height + Gap) * scale));
        }
    }

    // 满 3 秒：有排队的通知时换成下一条，否则再显示 3 秒
    private void OnTick()
    {
        if (_shownEnough || Queue.Count > 0)
        {
            Close();
            return;
        }
        _shownEnough = true;
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

    private sealed record Pending(ZulipSession Session, string? AccountName, NotificationRequest Request, Action Activate);
}
