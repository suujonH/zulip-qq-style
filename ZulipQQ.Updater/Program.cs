using System.Globalization;
using ZulipQQ.Update;

namespace ZulipQQ.Updater;

/// <summary>
/// 独立更新器。命令行：<c>--pid 旧进程 --app-dir 程序目录 --update-root 更新工作目录 --from 旧版本 --to 新版本 [--log 日志文件]</c>。
/// 没有界面；由主程序复制到更新工作目录后启动，流程见 <see cref="UpdateApplier"/>。
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        var options = ParseArgs(args);
        var logPath = options.GetValueOrDefault("--log");
        if (!options.TryGetValue("--app-dir", out var appDir)
            || !options.TryGetValue("--update-root", out var root)
            || !options.TryGetValue("--from", out var from)
            || !options.TryGetValue("--to", out var to)
            || !options.TryGetValue("--pid", out var pidText)
            || !int.TryParse(pidText, NumberStyles.None, CultureInfo.InvariantCulture, out var pid))
        {
            Log(logPath, "参数不完整：" + string.Join(' ', args));
            return 2;
        }

        var applier = new UpdateApplier(new UpdateRequest(pid, appDir, root, from, to), new SystemProcessHost(), m => Log(logPath, m));
        return applier.Run() == ApplyResult.Updated ? 0 : 1;
    }

    private static Dictionary<string, string> ParseArgs(string[] args)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i + 1 < args.Length; i += 2)
            options[args[i]] = args[i + 1];
        return options;
    }

    private static void Log(string? path, string message)
    {
        if (path is null)
            return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
            File.AppendAllText(path, $"{time} {message}{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 日志写不进去不应影响更新
        }
    }
}
