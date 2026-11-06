using System;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Media;

namespace ZulipQQ.Controls;

/// <summary>
/// 弹出层（右键菜单等）的磨砂玻璃效果：弹出窗口使用系统的亚克力模糊，Windows 11 上圆角（与菜单的 8px 圆角一致）。
/// 系统不支持（或关闭了透明效果）时返回 false，由调用方换成不透明的底色。
/// </summary>
public static class GlassPopup
{
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwcpRound = 2;

    /// <summary>在弹出层打开后调用：<paramref name="content"/> 是弹出层中的控件。返回是否启用了模糊。</summary>
    public static bool Apply(Control content)
    {
        // 只在 Windows 11 上启用：更早的系统不能给弹出窗口圆角，模糊会露出直角
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000) || TopLevel.GetTopLevel(content) is not { } popup || popup is Window
            || popup.TryGetPlatformHandle()?.Handle is not { } hwnd || hwnd == IntPtr.Zero)
            return false;
        popup.TransparencyLevelHint = [WindowTransparencyLevel.AcrylicBlur];
        if (popup.ActualTransparencyLevel != WindowTransparencyLevel.AcrylicBlur)
            return false;
        popup.Background = Brushes.Transparent;
        var preference = DwmwcpRound;
        DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref preference, sizeof(int));
        return true;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
