using System;
using Microsoft.Win32;

namespace ZulipQQ.Services;

/// <summary>
/// 开机自动运行：当前用户的 Windows 启动项（注册表 Run 键）。状态以注册表为准，不另外保存。
/// 启动项指向其它位置（程序被移动过）时视为未开启，重新开启后指向当前程序。
/// </summary>
public sealed class LaunchAtStartup(string keyPath, string valueName, string exePath)
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static LaunchAtStartup Current { get; } = new(RunKey, "ZulipQQ", Environment.ProcessPath ?? "");

    public bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(keyPath);
                return key?.GetValue(valueName) is string command
                    && string.Equals(command, Command, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                Log.Warn("读取开机自动运行设置失败", ex);
                return false;
            }
        }
    }

    /// <summary>开启或关闭；失败时记录日志，状态不变。</summary>
    public void Set(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(keyPath);
            if (enabled)
                key.SetValue(valueName, Command);
            else
                key.DeleteValue(valueName, throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            Log.Warn("修改开机自动运行设置失败", ex);
        }
    }

    // 路径可能含空格
    private string Command => $"\"{exePath}\"";
}
