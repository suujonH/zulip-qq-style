using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;

namespace ZulipQQ.Plugins;

/// <summary>插件的“打开链接 / 运行程序”：白名单判断与执行。</summary>
public static class Launcher
{
    /// <summary>插件有 launch 权限且目标在其白名单中（可以不经用户点击直接打开）。</summary>
    public static bool IsWhitelisted(NativePlugin plugin, string target) =>
        plugin.HasPermission("launch") && plugin.Manifest is { } m && m.Launch.Any(r => r.Matches(target));

    /// <summary>
    /// 打开 URI（交给系统，参数忽略）或运行程序。目标格式不对时抛出 <see cref="ArgumentException"/>，
    /// 启动失败时抛出 <see cref="Win32Exception"/> 等。
    /// </summary>
    public static void Launch(string target, string? args)
    {
        target = target.Trim();
        if (target.Length == 0 || target.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("目标不合法", nameof(target));
        var info = LaunchRule.IsUri(target, out var uri)
            ? new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }
            : new ProcessStartInfo(target, args ?? "") { UseShellExecute = true };
        using var _ = Process.Start(info);
    }

    /// <summary>按钮上显示的完整目标（程序带参数）。</summary>
    public static string Describe(string target, string args) => args.Length == 0 ? target : $"{target} {args}";
}
