using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using ZulipQQ.Core;

namespace ZulipQQ.Services;

/// <summary>
/// 主窗口的边缘自动隐藏（Windows）：窗口贴住屏幕上、左、右边缘，鼠标离开 300ms 后缩进边缘只露出 4 像素，
/// 鼠标移到露出的部分时移回。拖动中（左键按下）、有弹出菜单时不隐藏。每 100ms 轮询一次鼠标位置。
/// </summary>
public sealed class EdgeHideController : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan HideDelay = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan AnimationDuration = TimeSpan.FromMilliseconds(150);
    private const int AnimationSteps = 8;
    private const int PointerMargin = 8;
    private const int RevealMargin = 2;
    private const int VkLButton = 0x01;
    private const int DwmwaExtendedFrameBounds = 9;

    // 打开中的弹出层（右键菜单、Flyout 等）；属于本窗口的弹出层打开时不隐藏
    private static readonly HashSet<Popup> OpenPopups = [];

    // 缩进边缘中的窗口：仍是活动窗口，但看不到内容
    private static readonly HashSet<Window> HiddenWindows = [];

    private readonly Window _window;
    private readonly Func<bool> _enabled;
    private readonly DispatcherTimer _pollTimer = new() { Interval = PollInterval };
    private DispatcherTimer? _animationTimer;
    private DateTime? _outsideSince;
    private PixelPoint _shownPosition;
    private PixelPoint _animationTarget;

    static EdgeHideController()
    {
        Popup.IsOpenProperty.Changed.AddClassHandler<Popup>((popup, e) =>
        {
            if (e.GetNewValue<bool>())
                OpenPopups.Add(popup);
            else
                OpenPopups.Remove(popup);
        });
    }

    public EdgeHideController(Window window, Func<bool> enabled)
    {
        _window = window;
        _enabled = enabled;
        _pollTimer.Tick += (_, _) => Poll();
        _window.Activated += OnActivated;
        if (OperatingSystem.IsWindows())
            _pollTimer.Start();
    }

    /// <summary>窗口已缩进边缘。</summary>
    public bool IsHidden
    {
        get => HiddenWindows.Contains(_window);
        private set
        {
            if (value)
            {
                HiddenWindows.Add(_window);
            }
            else if (HiddenWindows.Remove(_window))
            {
                Revealed?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>缩进边缘的窗口开始移回。</summary>
    public event EventHandler? Revealed;

    /// <summary>窗口缩进在屏幕边缘（此时即使是活动窗口也看不到其中的聊天）。</summary>
    public static bool IsHiddenAtEdge(Window window) => HiddenWindows.Contains(window);

    /// <summary>窗口的正常位置：隐藏时为移回后的位置（保存窗口位置时使用）。</summary>
    public PixelPoint RestingPosition => IsHidden ? _shownPosition : _window.Position;

    /// <summary>
    /// 移回（不再满足隐藏条件、窗口被激活等）。<paramref name="immediate"/> 为 true 时不播放动画，
    /// 并先结束进行中的动画：之后要直接设置窗口位置时使用，否则动画会覆盖设置的位置。
    /// </summary>
    public void Reveal(bool immediate = false)
    {
        if (immediate && _animationTimer is { IsEnabled: true })
        {
            _animationTimer.Stop();
            _window.Position = _animationTarget;
        }
        if (!IsHidden)
            return;
        if (immediate)
        {
            IsHidden = false;
            _window.Position = _shownPosition;
        }
        else
        {
            Animate(_shownPosition, hidden: false);
        }
    }

    public void Dispose()
    {
        _pollTimer.Stop();
        _animationTimer?.Stop();
        HiddenWindows.Remove(_window);
        _window.Activated -= OnActivated;
    }

    // 通过任务栏、托盘、通知等激活窗口时移回
    private void OnActivated(object? sender, EventArgs e) => Reveal();

    private void Poll()
    {
        if (_animationTimer is { IsEnabled: true })
            return;
        if (!_enabled() || !_window.IsVisible || _window.WindowState != WindowState.Normal)
        {
            _outsideSince = null;
            Reveal();
            return;
        }
        if (!GetCursorPos(out var cursor))
            return;

        var (box, inset) = WindowBox();
        if (IsHidden)
        {
            if (box.Contains(cursor.X, cursor.Y, RevealMargin))
                Reveal();
            return;
        }

        // 拖动窗口或调整大小时不隐藏；松开后才判断是否贴边
        if ((GetAsyncKeyState(VkLButton) & 0x8000) != 0 || _window.Screens.ScreenFromWindow(_window) is not { } screen)
        {
            _outsideSince = null;
            return;
        }
        var work = ToBox(screen.WorkingArea);
        var edge = EdgeDock.Detect(box, work);
        // 多显示器之间的边界不隐藏
        if (edge != DockEdge.None && EdgeDock.HasNeighbor(edge, ToBox(screen.Bounds),
                _window.Screens.All.Where(s => s.Bounds != screen.Bounds).Select(s => ToBox(s.Bounds))))
            edge = DockEdge.None;
        if (edge == DockEdge.None || box.Contains(cursor.X, cursor.Y, PointerMargin) || HasOpenPopup())
        {
            _outsideSince = null;
            return;
        }

        var now = DateTime.UtcNow;
        _outsideSince ??= now;
        if (now - _outsideSince < HideDelay)
            return;
        _outsideSince = null;
        // 计算得到的是可见边框的位置，窗口位置要减去不可见边框
        var (sx, sy) = EdgeDock.ShownPosition(edge, box, work);
        _shownPosition = new PixelPoint(sx - inset.X, sy - inset.Y);
        var (hx, hy) = EdgeDock.HiddenPosition(edge, box, work);
        Animate(new PixelPoint(hx - inset.X, hy - inset.Y), hidden: true);
    }

    // 窗口的可见边框，以及它相对窗口位置的偏移。Windows 10/11 的窗口矩形包含左、右、下的不可见缩放边框（约 7 像素），
    // 按窗口矩形计算时露出的 4 像素正好是不可见边框，所以取 DWM 的可见边框；取不到时退回窗口矩形
    private (PixelBox Box, PixelPoint Inset) WindowBox()
    {
        var position = _window.Position;
        if (_window.TryGetPlatformHandle()?.Handle is { } hwnd && hwnd != IntPtr.Zero
            && DwmGetWindowAttribute(hwnd, DwmwaExtendedFrameBounds, out var r, Marshal.SizeOf<Rect>()) == 0
            && r.Right > r.Left && r.Bottom > r.Top)
        {
            return (new PixelBox(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top), new PixelPoint(r.Left - position.X, r.Top - position.Y));
        }
        var scale = _window.RenderScaling;
        var size = _window.FrameSize ?? _window.Bounds.Size;
        return (new PixelBox(position.X, position.Y, (int)Math.Round(size.Width * scale), (int)Math.Round(size.Height * scale)), default);
    }

    private static PixelBox ToBox(PixelRect r) => new(r.X, r.Y, r.Width, r.Height);

    private bool HasOpenPopup() =>
        OpenPopups.Any(p => TopLevel.GetTopLevel((Visual?)p.PlacementTarget ?? p) == _window);

    // 分 8 步在 150ms 内移动到目标位置
    private void Animate(PixelPoint target, bool hidden)
    {
        _animationTimer?.Stop();
        IsHidden = hidden;
        _animationTarget = target;
        var start = _window.Position;
        var step = 0;
        var timer = new DispatcherTimer { Interval = AnimationDuration / AnimationSteps };
        timer.Tick += (_, _) =>
        {
            step++;
            var t = (double)step / AnimationSteps;
            _window.Position = new PixelPoint(
                start.X + (int)Math.Round((target.X - start.X) * t),
                start.Y + (int)Math.Round((target.Y - start.Y) * t));
            if (step >= AnimationSteps)
                timer.Stop();
        };
        _animationTimer = timer;
        timer.Start();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point point);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out Rect value, int size);
}
