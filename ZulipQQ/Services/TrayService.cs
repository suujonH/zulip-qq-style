using System;
using Avalonia;
using Avalonia.Platform;
using Avalonia.Threading;

namespace ZulipQQ.Services;

/// <summary>
/// 托盘图标（登录后创建，退出账号或退出程序时释放）。单击打开主面板；菜单：打开主面板、退出账号、退出。
/// 有新消息而主面板不在前台时闪烁，直到主面板被激活或没有未读消息；闪烁时鼠标悬停不显示文字提示，
/// 而是触发 <see cref="Hovered"/>（显示托盘提醒列表）。
/// </summary>
public sealed class TrayService : IDisposable
{
    private static readonly TimeSpan FlashInterval = TimeSpan.FromMilliseconds(500);

    private readonly Win32NotifyIcon _icon = new();
    private readonly IntPtr _appIcon;
    private readonly IntPtr _blankIcon;
    private readonly DispatcherTimer _flashTimer = new() { Interval = FlashInterval };
    private bool _blank;

    public TrayService(string toolTip, Action showMain, Action logout, Action exit)
    {
        using (var ico = AssetLoader.Open(new Uri("avares://ZulipQQ/Assets/avalonia-logo.ico")))
            _appIcon = Win32NotifyIcon.CreateIcon(ico);
        // 闪烁时与应用图标交替显示的透明图标
        _blankIcon = Win32NotifyIcon.CreateBlankIcon();

        _icon.AddMenuItem("打开主面板", showMain);
        _icon.AddMenuItem("退出账号", logout);
        _icon.AddMenuItem(null);
        _icon.AddMenuItem("退出", exit);
        _icon.Icon = _appIcon;
        _icon.ToolTip = toolTip;
        _icon.Clicked += (_, _) =>
        {
            StopFlashing();
            showMain();
        };
        _icon.Hovered += (_, _) =>
        {
            if (IsFlashing)
                Hovered?.Invoke(this, EventArgs.Empty);
        };
        _flashTimer.Tick += (_, _) =>
        {
            _blank = !_blank;
            _icon.Icon = _blank ? _blankIcon : _appIcon;
        };
    }

    /// <summary>闪烁时鼠标悬停在图标上。</summary>
    public event EventHandler? Hovered;

    /// <summary>停止闪烁（主面板被激活、单击图标、没有未读消息等）。</summary>
    public event EventHandler? FlashingStopped;

    public bool IsVisible
    {
        get => _icon.IsVisible;
        set
        {
            _icon.IsVisible = value;
            if (!value)
                StopFlashing();
        }
    }

    public bool IsFlashing => _flashTimer.IsEnabled;

    /// <summary>鼠标悬停时显示的文字（在线账号变化时更新）。</summary>
    public void SetToolTip(string text) => _icon.ToolTip = text;

    /// <summary>图标在屏幕上的位置（物理像素）；取不到时为 null。</summary>
    public PixelRect? GetIconRect() => _icon.GetRect();

    public void StartFlashing()
    {
        if (!_icon.IsVisible || _flashTimer.IsEnabled)
            return;
        _flashTimer.Start();
        _icon.ShowToolTip = false;
    }

    public void StopFlashing()
    {
        var wasFlashing = _flashTimer.IsEnabled;
        _flashTimer.Stop();
        _blank = false;
        _icon.Icon = _appIcon;
        _icon.ShowToolTip = true;
        if (wasFlashing)
            FlashingStopped?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        StopFlashing();
        _icon.Dispose();
        Win32NotifyIcon.DestroyIconHandle(_appIcon);
        Win32NotifyIcon.DestroyIconHandle(_blankIcon);
    }
}
