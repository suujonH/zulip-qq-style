using Avalonia;
using Avalonia.Controls;

namespace ZulipQQ.Views;

/// <summary>
/// 不设 Owner 的附属窗口（聊天记录、收藏、文件）共用的定位方法：附属窗口总在 Owner 之上，
/// “定位到聊天”后会挡住主窗口，所以改为手动居中于主窗口。
/// </summary>
internal static class WindowPlacement
{
    /// <summary>在显示之前调用：把窗口居中于 <paramref name="owner"/>；owner 为 null 时保持原来的启动位置。</summary>
    public static void CenterOn(Window window, Window? owner)
    {
        if (owner is null)
            return;
        var scale = owner.RenderScaling;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Position = new PixelPoint(
            owner.Position.X + (int)((owner.Bounds.Width - window.Width) * scale / 2),
            owner.Position.Y + (int)((owner.Bounds.Height - window.Height) * scale / 2));
    }

    /// <summary>还原并激活窗口。</summary>
    public static void BringToFront(Window? window)
    {
        if (window is null)
            return;
        if (window.WindowState == WindowState.Minimized)
            window.WindowState = WindowState.Normal;
        window.Activate();
    }
}
