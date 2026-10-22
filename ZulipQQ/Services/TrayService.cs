using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;

namespace ZulipQQ.Services;

/// <summary>
/// 托盘图标（登录后创建，退出账号或退出程序时释放）。单击打开主面板；菜单：打开主面板、退出账号、退出。
/// 有新消息而主面板不在前台时闪烁，直到主面板被激活或没有未读消息。
/// </summary>
public sealed class TrayService : IDisposable
{
    private static readonly TimeSpan FlashInterval = TimeSpan.FromMilliseconds(500);

    private readonly TrayIcon _icon;
    private readonly WindowIcon _appIcon;
    private readonly WindowIcon _blankIcon;
    private readonly DispatcherTimer _flashTimer = new() { Interval = FlashInterval };
    private bool _blank;

    public TrayService(string toolTip, Action showMain, Action logout, Action exit)
    {
        _appIcon = new WindowIcon(AssetLoader.Open(new Uri("avares://ZulipQQ/Assets/avalonia-logo.ico")));
        // 闪烁时与应用图标交替显示的透明图标
        _blankIcon = new WindowIcon(new WriteableBitmap(new PixelSize(16, 16), new Vector(96, 96),
            Avalonia.Platform.PixelFormat.Bgra8888, AlphaFormat.Premul));

        var menu = new NativeMenu();
        menu.Items.Add(MenuItem("打开主面板", showMain));
        menu.Items.Add(MenuItem("退出账号", logout));
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(MenuItem("退出", exit));

        _icon = new TrayIcon { Icon = _appIcon, ToolTipText = toolTip, Menu = menu };
        _icon.Clicked += (_, _) =>
        {
            StopFlashing();
            showMain();
        };
        _flashTimer.Tick += (_, _) =>
        {
            _blank = !_blank;
            _icon.Icon = _blank ? _blankIcon : _appIcon;
        };
        TrayIcon.SetIcons(Application.Current!, [_icon]);
    }

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

    public void StartFlashing()
    {
        if (_icon.IsVisible && !_flashTimer.IsEnabled)
            _flashTimer.Start();
    }

    public void StopFlashing()
    {
        _flashTimer.Stop();
        _blank = false;
        _icon.Icon = _appIcon;
    }

    public void Dispose()
    {
        _flashTimer.Stop();
        _icon.IsVisible = false;
        TrayIcon.SetIcons(Application.Current!, []);
        _icon.Dispose();
    }

    private static NativeMenuItem MenuItem(string header, Action action)
    {
        var item = new NativeMenuItem(header);
        item.Click += (_, _) => action();
        return item;
    }
}
