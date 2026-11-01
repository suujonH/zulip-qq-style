using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Threading;

namespace ZulipQQ.Services;

/// <summary>
/// Windows 的通知区域图标（Shell_NotifyIcon）。Avalonia 的 TrayIcon 只有单击事件，托盘提醒列表需要鼠标悬停，
/// 所以由隐藏窗口直接接收图标的鼠标消息。只在 UI 线程上使用。
/// </summary>
public sealed class Win32NotifyIcon : IDisposable
{
    private const uint WmNull = 0x0000;
    private const uint WmApp = 0x8000;
    private const uint WmMouseMove = 0x0200;
    private const uint WmLButtonUp = 0x0202;
    private const uint WmContextMenu = 0x007B;
    private const uint NinPopupOpen = 0x0406;
    private const uint CallbackMessage = WmApp + 1;

    private const uint NimAdd = 0;
    private const uint NimModify = 1;
    private const uint NimDelete = 2;
    private const uint NimSetVersion = 4;
    private const uint NifMessage = 0x01;
    private const uint NifIcon = 0x02;
    private const uint NifTip = 0x04;
    private const uint NifShowTip = 0x80;
    private const uint NotifyIconVersion4 = 4;

    private const uint MfString = 0x0000;
    private const uint MfSeparator = 0x0800;
    private const uint TpmRightButton = 0x0002;
    private const uint TpmReturnCmd = 0x0100;
    private const int SmCxSmIcon = 49;
    private const int SmCySmIcon = 50;

    private const string ClassName = "ZulipQQ.NotifyIcon";

    // 窗口类只注册一次；消息按窗口句柄分发到实例。委托保存在静态字段中，避免被回收
    private static readonly WndProc Proc = OnMessage;
    private static readonly Dictionary<IntPtr, Win32NotifyIcon> Instances = [];
    private static readonly uint TaskbarCreated = RegisterWindowMessageW("TaskbarCreated");
    private static bool _classRegistered;

    private readonly IntPtr _hwnd;
    private readonly List<(string? Text, Action? Action)> _menu = [];
    private IntPtr _icon;
    private string _toolTip = "";
    private bool _showToolTip = true;
    private bool _visible;
    private bool _disposed;

    public Win32NotifyIcon()
    {
        var instance = GetModuleHandleW(null);
        if (!_classRegistered)
        {
            var wc = new WndClassEx
            {
                Size = (uint)Marshal.SizeOf<WndClassEx>(),
                WndProc = Marshal.GetFunctionPointerForDelegate(Proc),
                Instance = instance,
                ClassName = ClassName,
            };
            RegisterClassExW(ref wc);
            _classRegistered = true;
        }
        // 普通的隐藏顶层窗口（不是 message-only 窗口），才能收到任务栏重建时广播的 TaskbarCreated
        _hwnd = CreateWindowExW(0, ClassName, "", 0, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
        Instances[_hwnd] = this;
    }

    /// <summary>单击图标（左键）。</summary>
    public event EventHandler? Clicked;

    /// <summary>鼠标在图标上移动（悬停）。</summary>
    public event EventHandler? Hovered;

    /// <summary>当前显示的图标（不负责释放）。</summary>
    public IntPtr Icon
    {
        get => _icon;
        set
        {
            _icon = value;
            Update(NimModify);
        }
    }

    public string ToolTip
    {
        get => _toolTip;
        set
        {
            _toolTip = value;
            Update(NimModify);
        }
    }

    /// <summary>是否显示系统的文字提示；不显示时悬停只触发 <see cref="Hovered"/>（由调用方显示自己的弹出窗口）。</summary>
    public bool ShowToolTip
    {
        get => _showToolTip;
        set
        {
            _showToolTip = value;
            Update(NimModify);
        }
    }

    public bool IsVisible
    {
        get => _visible;
        set
        {
            if (_visible == value || _disposed)
                return;
            _visible = value;
            if (value)
                Add();
            else
                Delete();
        }
    }

    /// <summary>右键菜单的一项；<paramref name="text"/> 为 null 时是分隔线。</summary>
    public void AddMenuItem(string? text, Action? action = null) => _menu.Add((text, action));

    /// <summary>图标在屏幕上的位置（物理像素）；图标收在溢出区且溢出区未展开等情况下取不到时为 null。</summary>
    public PixelRect? GetRect()
    {
        var id = new NotifyIconIdentifier
        {
            Size = (uint)Marshal.SizeOf<NotifyIconIdentifier>(),
            Hwnd = _hwnd,
            Id = 1,
        };
        if (!_visible || Shell_NotifyIconGetRect(ref id, out var r) != 0)
            return null;
        return new PixelRect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
    }

    /// <summary>鼠标指针的屏幕位置（物理像素）。</summary>
    public static PixelPoint GetCursorPosition() =>
        GetCursorPos(out var p) ? new PixelPoint(p.X, p.Y) : default;

    /// <summary>
    /// 从 .ico 文件的数据中创建最接近通知区域图标大小的图标，用完后用 <see cref="DestroyIconHandle"/> 释放。
    /// </summary>
    public static IntPtr CreateIcon(Stream ico)
    {
        using var ms = new MemoryStream();
        ico.CopyTo(ms);
        var data = ms.ToArray();
        int cx = GetSystemMetrics(SmCxSmIcon), cy = GetSystemMetrics(SmCySmIcon);
        // ICONDIR（6 字节）后是 16 字节的 ICONDIRENTRY：宽、高（0 表示 256）、…、数据大小、数据偏移。
        // 选不小于目标大小的最小一个，都更小时选最大的
        var count = BitConverter.ToUInt16(data, 4);
        int best = -1, bestSize = 0;
        for (var i = 0; i < count; i++)
        {
            var size = data[6 + i * 16] == 0 ? 256 : data[6 + i * 16];
            var better = best < 0
                || (size >= cx ? bestSize < cx || size < bestSize : bestSize < cx && size > bestSize);
            if (better)
            {
                best = i;
                bestSize = size;
            }
        }
        if (best < 0)
            return IntPtr.Zero;
        var length = BitConverter.ToInt32(data, 6 + best * 16 + 8);
        var offset = BitConverter.ToInt32(data, 6 + best * 16 + 12);
        unsafe
        {
            fixed (byte* bits = &data[offset])
                return CreateIconFromResourceEx(bits, (uint)length, true, 0x00030000, cx, cy, 0);
        }
    }

    /// <summary>与通知区域图标同样大小的全透明图标（闪烁时与应用图标交替显示）。</summary>
    public static IntPtr CreateBlankIcon()
    {
        int cx = GetSystemMetrics(SmCxSmIcon), cy = GetSystemMetrics(SmCySmIcon);
        // 32 位、alpha 全为 0 的图标资源（单色的透明图标在通知区域中不会被画成透明，图标看起来不闪烁）：
        // BITMAPINFOHEADER（高度为两倍：颜色 + AND 掩码）、全 0 的 BGRA 像素、全 1 的 AND 掩码（每行按 DWORD 对齐）
        var maskStride = (cx + 31) / 32 * 4;
        var data = new byte[40 + cx * cy * 4 + maskStride * cy];
        BitConverter.GetBytes(40).CopyTo(data, 0);
        BitConverter.GetBytes(cx).CopyTo(data, 4);
        BitConverter.GetBytes(cy * 2).CopyTo(data, 8);
        BitConverter.GetBytes((ushort)1).CopyTo(data, 12);
        BitConverter.GetBytes((ushort)32).CopyTo(data, 14);
        Array.Fill(data, (byte)0xFF, 40 + cx * cy * 4, maskStride * cy);
        unsafe
        {
            fixed (byte* bits = data)
                return CreateIconFromResourceEx(bits, (uint)data.Length, true, 0x00030000, cx, cy, 0);
        }
    }

    public static void DestroyIconHandle(IntPtr icon)
    {
        if (icon != IntPtr.Zero)
            DestroyIcon(icon);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        IsVisible = false;
        _disposed = true;
        Instances.Remove(_hwnd);
        DestroyWindow(_hwnd);
    }

    private void Add()
    {
        Update(NimAdd);
        var data = NewData(0);
        data.TimeoutOrVersion = NotifyIconVersion4;
        Shell_NotifyIconW(NimSetVersion, ref data);
    }

    private void Delete()
    {
        var data = NewData(0);
        Shell_NotifyIconW(NimDelete, ref data);
    }

    private void Update(uint message)
    {
        if (!_visible || _disposed)
            return;
        var data = NewData(NifMessage | NifIcon | NifTip | (_showToolTip ? NifShowTip : 0));
        Shell_NotifyIconW(message, ref data);
    }

    private NotifyIconData NewData(uint flags) => new()
    {
        Size = (uint)Marshal.SizeOf<NotifyIconData>(),
        Hwnd = _hwnd,
        Id = 1,
        Flags = flags,
        CallbackMessage = CallbackMessage,
        Icon = _icon,
        Tip = _toolTip.Length > 127 ? _toolTip[..127] : _toolTip,
        Info = "",
        InfoTitle = "",
    };

    private static IntPtr OnMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (Instances.TryGetValue(hwnd, out var icon))
        {
            if (msg == CallbackMessage)
            {
                // NOTIFYICON_VERSION_4：lParam 的低位是事件，wParam 是鼠标的屏幕坐标
                switch ((uint)(lParam.ToInt64() & 0xFFFF))
                {
                    case WmLButtonUp:
                        icon.Clicked?.Invoke(icon, EventArgs.Empty);
                        break;
                    case WmMouseMove or NinPopupOpen:
                        icon.Hovered?.Invoke(icon, EventArgs.Empty);
                        break;
                    case WmContextMenu:
                        icon.ShowMenu((short)(wParam.ToInt64() & 0xFFFF), (short)((wParam.ToInt64() >> 16) & 0xFFFF));
                        break;
                }
                return IntPtr.Zero;
            }
            // 资源管理器重启后重新添加图标
            if (msg == TaskbarCreated && icon._visible)
                icon.Add();
        }
        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    private void ShowMenu(int x, int y)
    {
        var menu = CreatePopupMenu();
        for (var i = 0; i < _menu.Count; i++)
        {
            if (_menu[i].Text is { } text)
                AppendMenuW(menu, MfString, (UIntPtr)(i + 1), text);
            else
                AppendMenuW(menu, MfSeparator, UIntPtr.Zero, null);
        }
        // 先把隐藏窗口设为前台，点击菜单外时菜单才会关闭（Shell_NotifyIcon 文档的要求）
        SetForegroundWindow(_hwnd);
        var command = TrackPopupMenuEx(menu, TpmRightButton | TpmReturnCmd, x, y, _hwnd, IntPtr.Zero);
        PostMessageW(_hwnd, WmNull, IntPtr.Zero, IntPtr.Zero);
        DestroyMenu(menu);
        if (command > 0 && command <= _menu.Count && _menu[command - 1].Action is { } action)
            Dispatcher.UIThread.Post(action);
    }

    private delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClassEx
    {
        public uint Size;
        public uint Style;
        public IntPtr WndProc;
        public int ClassExtra;
        public int WindowExtra;
        public IntPtr Instance;
        public IntPtr Icon;
        public IntPtr Cursor;
        public IntPtr Background;
        public string? MenuName;
        public string ClassName;
        public IntPtr SmallIcon;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size;
        public IntPtr Hwnd;
        public uint Id;
        public uint Flags;
        public uint CallbackMessage;
        public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Tip;
        public uint State;
        public uint StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string Info;
        public uint TimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string InfoTitle;
        public uint InfoFlags;
        public Guid Guid;
        public IntPtr BalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NotifyIconIdentifier
    {
        public uint Size;
        public IntPtr Hwnd;
        public uint Id;
        public Guid Guid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? name);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassExW(ref WndClassEx wc);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProcW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessageW(string name);

    [DllImport("user32.dll")]
    private static extern bool PostMessageW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenuW(IntPtr menu, uint flags, UIntPtr id, string? text);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr hwnd, IntPtr param);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll")]
    private static extern unsafe IntPtr CreateIconFromResourceEx(byte* bits, uint size, bool icon, uint version,
        int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIconW(uint message, ref NotifyIconData data);

    [DllImport("shell32.dll")]
    private static extern int Shell_NotifyIconGetRect(ref NotifyIconIdentifier id, out Rect rect);
}
