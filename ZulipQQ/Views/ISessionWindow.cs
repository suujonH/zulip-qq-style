using System.Runtime.CompilerServices;
using Avalonia.Controls;
using ZulipQQ.Controls;
using ZulipQQ.Services;

namespace ZulipQQ.Views;

/// <summary>
/// 属于某个账号的窗口：退出该账号时由 App 关闭；没有用 <see cref="SessionWindow.Bind"/> 绑定账号的窗口
/// （如插件日志）在所有账号都退出、或退出程序时关闭。
/// </summary>
public interface ISessionWindow;

/// <summary>可以在窗口中切换账号的窗口（收藏、文件）：显示的账号退出时先尝试切换到另一个在线账号。</summary>
public interface IAccountSwitchWindow : ISessionWindow
{
    /// <summary>切换到 <paramref name="ending"/> 以外的在线账号；没有时返回 false，窗口随后被关闭。</summary>
    bool SwitchAway(ZulipSession ending);
}

/// <summary>窗口所属的账号。</summary>
public static class SessionWindow
{
    private static readonly ConditionalWeakTable<Window, ZulipSession> Sessions = new();

    /// <summary>记录窗口所属的账号，并让窗口中的头像、图片使用该账号的客户端下载。</summary>
    public static void Bind(Window window, ZulipSession session)
    {
        Sessions.AddOrUpdate(window, session);
        RealmImage.SetClient(window, session.Client);
    }

    public static ZulipSession? Of(Window? window) =>
        window is not null && Sessions.TryGetValue(window, out var session) ? session : null;
}
