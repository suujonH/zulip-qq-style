using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace ZulipQQ.Services;

/// <summary>
/// 监听系统剪贴板的变化（Windows 的 AddClipboardFormatListener）：任何程序（包括自己）修改剪贴板后调用回调。
/// 每个监听使用自己的仅消息窗口，互不影响。必须在 UI 线程上创建和释放，回调也在 UI 线程上执行。
/// </summary>
public sealed class ClipboardWatcher : IDisposable
{
    private const uint WmClipboardUpdate = 0x031D;
    private static readonly IntPtr HwndMessage = new(-3);
    private const string ClassName = "ZulipQQClipboardWatcher";

    // 窗口过程必须一直被引用，否则会被回收
    private static readonly WndProc Proc = WindowProc;
    private static readonly Dictionary<IntPtr, ClipboardWatcher> Watchers = [];
    private static bool _registered;

    private readonly Action _onChanged;
    private IntPtr _hwnd;

    private ClipboardWatcher(Action onChanged, IntPtr hwnd)
    {
        _onChanged = onChanged;
        _hwnd = hwnd;
    }

    /// <summary>开始监听；不是 Windows 或创建失败时返回 null（不监听）。</summary>
    public static ClipboardWatcher? Start(Action onChanged)
    {
        if (!OperatingSystem.IsWindows())
            return null;
        try
        {
            var instance = GetModuleHandleW(null);
            if (!_registered)
            {
                var wc = new WndClassEx
                {
                    Size = (uint)Marshal.SizeOf<WndClassEx>(),
                    WndProc = Marshal.GetFunctionPointerForDelegate(Proc),
                    Instance = instance,
                    ClassName = ClassName,
                };
                if (RegisterClassExW(ref wc) == 0)
                {
                    Log.Warn($"注册剪贴板监听窗口失败：{Marshal.GetLastWin32Error()}");
                    return null;
                }
                _registered = true;
            }
            var hwnd = CreateWindowExW(0, ClassName, "", 0, 0, 0, 0, 0, HwndMessage, IntPtr.Zero, instance, IntPtr.Zero);
            if (hwnd == IntPtr.Zero)
            {
                Log.Warn($"创建剪贴板监听窗口失败：{Marshal.GetLastWin32Error()}");
                return null;
            }
            if (!AddClipboardFormatListener(hwnd))
            {
                Log.Warn($"监听剪贴板失败：{Marshal.GetLastWin32Error()}");
                DestroyWindow(hwnd);
                return null;
            }
            var watcher = new ClipboardWatcher(onChanged, hwnd);
            Watchers[hwnd] = watcher;
            return watcher;
        }
        catch (Exception ex)
        {
            Log.Warn("监听剪贴板失败", ex);
            return null;
        }
    }

    public void Dispose()
    {
        if (_hwnd == IntPtr.Zero)
            return;
        Watchers.Remove(_hwnd);
        RemoveClipboardFormatListener(_hwnd);
        DestroyWindow(_hwnd);
        _hwnd = IntPtr.Zero;
    }

    private static IntPtr WindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WmClipboardUpdate && Watchers.TryGetValue(hwnd, out var watcher))
        {
            try
            {
                watcher._onChanged();
            }
            catch (Exception ex)
            {
                Log.Warn("处理剪贴板变化失败", ex);
            }
            return IntPtr.Zero;
        }
        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    private delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClassEx
    {
        public uint Size;
        public uint Style;
        public IntPtr WndProc;
        public int ClsExtra;
        public int WndExtra;
        public IntPtr Instance;
        public IntPtr Icon;
        public IntPtr Cursor;
        public IntPtr Background;
        public string? MenuName;
        public string ClassName;
        public IntPtr IconSm;
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassExW(ref WndClassEx wc);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProcW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? moduleName);
}
