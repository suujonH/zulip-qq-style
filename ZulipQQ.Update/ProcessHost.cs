using System.Diagnostics;

namespace ZulipQQ.Update;

/// <summary>更新器启动的程序。</summary>
public interface IStartedApp : IDisposable
{
    bool HasExited { get; }

    void Kill();
}

/// <summary>更新器对进程的操作；测试中用模拟实现代替。</summary>
public interface IProcessHost
{
    /// <summary>等待进程退出，超时后强制结束它。</summary>
    void WaitForExit(int processId, TimeSpan timeout);

    /// <summary>有没有从 <paramref name="appDir"/> 运行的主程序（<paramref name="exceptProcessId"/> 除外）。</summary>
    bool IsAppRunning(string appDir, int exceptProcessId);

    IStartedApp Start(string exePath, params string[] args);
}

public sealed class SystemProcessHost : IProcessHost
{
    public void WaitForExit(int processId, TimeSpan timeout)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            if (process.WaitForExit(timeout))
                return;
            // 主程序已经结束了所有会话，只是没能退出：结束它，否则文件一直被占用
            process.Kill(entireProcessTree: false);
            process.WaitForExit(TimeSpan.FromSeconds(10));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // 进程已经退出；结束不了时后面的替换会失败并回滚
        }
    }

    public bool IsAppRunning(string appDir, int exceptProcessId)
    {
        var name = Path.GetFileNameWithoutExtension(UpdateLayout.AppExeName);
        var expected = Path.GetFullPath(Path.Combine(appDir, UpdateLayout.AppExeName));
        foreach (var process in Process.GetProcessesByName(name))
        {
            using (process)
            {
                try
                {
                    if (process.Id != exceptProcessId
                        && string.Equals(process.MainModule?.FileName, expected, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
                {
                    // 进程已退出或属于其他用户：不是这个程序目录在运行
                }
            }
        }
        return false;
    }

    public IStartedApp Start(string exePath, params string[] args)
    {
        var info = new ProcessStartInfo(exePath)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(exePath)!,
        };
        foreach (var arg in args)
            info.ArgumentList.Add(arg);
        return new StartedProcess(Process.Start(info) ?? throw new InvalidOperationException($"无法启动 {exePath}"));
    }

    private sealed class StartedProcess(Process process) : IStartedApp
    {
        public bool HasExited => process.HasExited;

        public void Kill()
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit(TimeSpan.FromSeconds(10));
        }

        public void Dispose() => process.Dispose();
    }
}
