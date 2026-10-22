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

    /// <summary>
    /// 在显示之前调用：放在 <paramref name="previous"/> 右下方错开一点，避免完全重叠；
    /// previous 不可见、不是普通状态，或错开后超出它所在屏幕的工作区时，保持原来的启动位置。
    /// </summary>
    public static void Cascade(Window window, Window? previous)
    {
        if (previous is not { IsVisible: true, WindowState: WindowState.Normal }
            || previous.Screens.ScreenFromWindow(previous) is not { } screen)
            return;
        var scale = previous.RenderScaling;
        var offset = (int)(30 * scale);
        var position = new PixelPoint(previous.Position.X + offset, previous.Position.Y + offset);
        var area = screen.WorkingArea;
        if (position.X + window.Width * scale > area.Right || position.Y + window.Height * scale > area.Bottom)
            return;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Position = position;
    }

    /// <summary>还原并激活窗口。</summary>
    public static void BringToFront(Window? window)
    {
        if (window is null)
            return;
        if (!window.IsVisible)
            window.Show();
        if (window.WindowState == WindowState.Minimized)
            window.WindowState = WindowState.Normal;
        window.Activate();
    }
}
