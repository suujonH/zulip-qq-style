using System;
using System.Globalization;
using System.IO;

namespace ZulipQQ.Services;

/// <summary>简单的文件日志：追加写入 <c>logs\yyyyMMdd.log</c>，写入失败时忽略。</summary>
public static class Log
{
    private static readonly object Gate = new();

    /// <summary>日志目录；测试中可以改为临时目录。</summary>
    public static string Directory { get; set; } = AppPaths.LogDir;

    public static void Debug(string message, Exception? ex = null) => Write("DEBUG", message, ex);

    public static void Info(string message, Exception? ex = null) => Write("INFO", message, ex);

    public static void Warn(string message, Exception? ex = null) => Write("WARN", message, ex);

    public static void Error(string message, Exception? ex = null) => Write("ERROR", message, ex);

    private static void Write(string level, string message, Exception? ex)
    {
        var now = DateTime.Now;
        var line = $"{now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)} [{level}] {message}";
        if (ex is not null)
            line += Environment.NewLine + ex;

        lock (Gate)
        {
            try
            {
                System.IO.Directory.CreateDirectory(Directory);
                var path = Path.Combine(Directory, now.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".log");
                File.AppendAllText(path, line + Environment.NewLine);
            }
            catch
            {
                // 日志写不进去不应影响程序
            }
        }
    }
}
