using System;
using System.Runtime.InteropServices;
using Avalonia.Controls;

namespace ZulipQQ.Services;

/// <summary>让窗口的任务栏按钮闪烁，直到窗口回到前台（Windows 的 FlashWindowEx）。</summary>
public static class TaskbarFlash
{
    private const uint FlashwTray = 0x00000002;
    private const uint FlashwTimerNoFg = 0x0000000C;

    public static void Start(Window window)
    {
        if (window.TryGetPlatformHandle()?.Handle is not { } hwnd || hwnd == IntPtr.Zero)
            return;
        var info = new FlashInfo
        {
            Size = (uint)Marshal.SizeOf<FlashInfo>(),
            Hwnd = hwnd,
            Flags = FlashwTray | FlashwTimerNoFg,
        };
        FlashWindowEx(ref info);
    }

    /// <summary>
    /// 窗口是否真正在前台（有键盘焦点）。不用 <see cref="WindowBase.IsActive"/>：程序在后台启动时 Windows 不允许它抢占前台，
    /// Avalonia 仍会把窗口标为激活，之后也收不到失去激活的消息，IsActive 会一直为 true。
    /// </summary>
    public static bool IsForeground(Window window) =>
        window.TryGetPlatformHandle()?.Handle is { } hwnd && hwnd != IntPtr.Zero && GetForegroundWindow() == hwnd;

    [StructLayout(LayoutKind.Sequential)]
    private struct FlashInfo
    {
        public uint Size;
        public IntPtr Hwnd;
        public uint Flags;
        public uint Count;
        public uint Timeout;
    }

    [DllImport("user32.dll")]
    private static extern bool FlashWindowEx(ref FlashInfo info);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
}
