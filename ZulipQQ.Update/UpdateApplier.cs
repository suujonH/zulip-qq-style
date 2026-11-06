using System.Diagnostics;

namespace ZulipQQ.Update;

/// <summary>一次更新的参数，由主程序通过命令行传给更新器。</summary>
public sealed record UpdateRequest(int ProcessId, string AppDir, string UpdateRoot, string FromVersion, string ToVersion);

/// <summary>更新的结果。</summary>
public enum ApplyResult
{
    /// <summary>新版本已启动并确认正常，旧版本已移除。</summary>
    Updated,

    /// <summary>更新失败，已恢复并启动旧版本（或尝试启动）。</summary>
    RolledBack,

    /// <summary>程序目录中还有其它实例在运行，没有替换也没有启动程序，下次启动时再试。</summary>
    Skipped,
}

/// <summary>
/// 更新器的主流程：等旧程序退出 → 校验新版本 → 替换文件 → 启动新版本并等它确认启动成功 → 移除旧版本。
/// 任何一步失败都恢复旧文件、记录失败并启动旧程序。
/// </summary>
public sealed class UpdateApplier(UpdateRequest request, IProcessHost host, Action<string> log)
{
    private readonly UpdateLayout _layout = new(request.UpdateRoot);

    public TimeSpan ExitTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>新版本确认启动成功的最长等待时间。</summary>
    public TimeSpan ProbeTimeout { get; init; } = TimeSpan.FromSeconds(90);

    /// <summary>新版本确认启动后还要继续存活这么久，才算启动成功。</summary>
    public TimeSpan StableTime { get; init; } = TimeSpan.FromSeconds(3);

    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(200);

    public TimeSpan FileRetryDelay { get; init; } = TimeSpan.FromMilliseconds(300);

    public ApplyResult Run()
    {
        log($"开始更新：{request.FromVersion} → {request.ToVersion}");
        host.WaitForExit(request.ProcessId, ExitTimeout);
        if (host.IsAppRunning(request.AppDir, request.ProcessId))
        {
            log("程序目录中还有其它实例在运行，本次不更新");
            return ApplyResult.Skipped;
        }

        IReadOnlyList<FileEntry> oldEntries;
        IReadOnlyList<FileEntry> newEntries;
        try
        {
            (oldEntries, newEntries) = Prepare();
        }
        catch (Exception ex) when (FileSwap.IsFileError(ex) || ex is PackageInvalidException)
        {
            return Fail($"校验新版本失败：{ex.Message}", block: false);
        }

        var swap = new FileSwap(request.AppDir, log) { RetryDelay = FileRetryDelay };
        try
        {
            swap.Apply(oldEntries.Select(e => e.Path), _layout.StagedDir, newEntries);
        }
        catch (Exception ex) when (FileSwap.IsFileError(ex) || ex is PackageInvalidException)
        {
            RollBack(swap);
            return Fail($"替换文件失败：{ex.Message}", block: false);
        }

        var error = StartNewVersion();
        if (error is not null)
        {
            RollBack(swap);
            return Fail(error, block: true);
        }

        swap.Commit();
        CleanStaged();
        Report(UpdateState.Load(_layout.StatePath).Cleared(), $"已从 {request.FromVersion} 更新到 {request.ToVersion}");
        return ApplyResult.Updated;
    }

    private (IReadOnlyList<FileEntry> Old, IReadOnlyList<FileEntry> New) Prepare()
    {
        var manifest = File.Exists(_layout.ReadyPath) ? UpdateManifest.Parse(File.ReadAllText(_layout.ReadyPath)) : null;
        if (manifest is null || AppVersion.TryParse(manifest.Version) is not { } ready
            || AppVersion.TryParse(request.ToVersion) is not { } to || ready.CompareTo(to) != 0)
            throw new PackageInvalidException("没有准备好的新版本");
        var oldList = Path.Combine(request.AppDir, UpdateLayout.FileListName);
        if (!File.Exists(oldList))
            throw new PackageInvalidException($"程序目录中没有文件清单 {UpdateLayout.FileListName}");
        var oldEntries = FileEntry.ParseList(File.ReadAllLines(oldList));
        return (oldEntries, PackageVerifier.VerifyTree(_layout.StagedDir));
    }

    // 启动新版本并等它写入带版本号的启动标记；返回失败原因，成功时返回 null
    private string? StartNewVersion()
    {
        var exe = Path.Combine(request.AppDir, UpdateLayout.AppExeName);
        IStartedApp app;
        try
        {
            File.Delete(_layout.ProbePath);
            app = host.Start(exe, UpdateLayout.ProbeArg, _layout.ProbePath);
        }
        catch (Exception ex) when (FileSwap.IsFileError(ex) || ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return $"无法启动新版本：{ex.Message}";
        }

        using (app)
        {
            var error = WaitForProbe(app);
            if (error is not null)
                KillQuietly(app);
            return error;
        }
    }

    private string? WaitForProbe(IStartedApp app)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < ProbeTimeout)
        {
            var marker = ReadProbe();
            if (marker is not null)
            {
                // 程序实际版本与清单不符时必须回滚，否则新程序会认为自己仍是旧版本而一直更新
                if (AppVersion.TryParse(marker) is not { } actual || AppVersion.TryParse(request.ToVersion) is not { } expected
                    || actual.CompareTo(expected) != 0)
                    return $"新版本报告的版本号 {marker} 与 {request.ToVersion} 不符";
                return WaitStable(app);
            }
            if (app.HasExited)
                return "新版本启动后退出了";
            Thread.Sleep(PollInterval);
        }
        return $"新版本在 {ProbeTimeout.TotalSeconds:0} 秒内没有确认启动成功";
    }

    private string? WaitStable(IStartedApp app)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < StableTime)
        {
            if (app.HasExited)
                return "新版本确认启动后很快退出了";
            Thread.Sleep(PollInterval);
        }
        return app.HasExited ? "新版本确认启动后很快退出了" : null;
    }

    private string? ReadProbe()
    {
        try
        {
            // 写入尚未完成时内容可能是空的，下一轮再读
            return File.Exists(_layout.ProbePath) && File.ReadAllText(_layout.ProbePath).Trim() is { Length: > 0 } text ? text : null;
        }
        catch (Exception ex) when (FileSwap.IsFileError(ex))
        {
            return null;
        }
    }

    private void KillQuietly(IStartedApp app)
    {
        try
        {
            if (!app.HasExited)
                app.Kill();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            log($"无法结束新版本：{ex.Message}");
        }
    }

    private void RollBack(FileSwap swap)
    {
        log("正在恢复旧版本文件");
        if (!swap.Rollback())
            log("部分旧文件没能恢复，备份保留在程序目录的 " + UpdateLayout.BackupDirName);
    }

    private ApplyResult Fail(string reason, bool block)
    {
        log($"更新失败：{reason}");
        // 先记录失败再启动旧程序：旧程序启动时据此不再自动重试
        CleanStaged();
        Report(UpdateState.Load(_layout.StatePath).WithFailure(request.ToVersion, reason, block),
            $"更新到 {request.ToVersion} 失败，已恢复 {request.FromVersion}：{reason}");
        try
        {
            host.Start(Path.Combine(request.AppDir, UpdateLayout.AppExeName)).Dispose();
            log($"已启动旧版本 {request.FromVersion}");
        }
        catch (Exception ex) when (FileSwap.IsFileError(ex) || ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            log($"无法启动旧版本：{ex.Message}");
        }
        return ApplyResult.RolledBack;
    }

    private void Report(UpdateState state, string message)
    {
        log(message);
        try
        {
            (state with { PendingReport = message }).Save(_layout.StatePath);
        }
        catch (Exception ex) when (FileSwap.IsFileError(ex))
        {
            log($"无法保存更新状态：{ex.Message}");
        }
    }

    private void CleanStaged()
    {
        foreach (var file in new[] { _layout.ReadyPath, _layout.ProbePath })
        {
            try
            {
                File.Delete(file);
            }
            catch (Exception ex) when (FileSwap.IsFileError(ex))
            {
                log($"无法删除 {file}：{ex.Message}");
            }
        }
        foreach (var dir in new[] { _layout.StagedDir, _layout.WorkDir })
        {
            try
            {
                FileSwap.DeleteDirectory(dir);
            }
            catch (Exception ex) when (FileSwap.IsFileError(ex))
            {
                log($"无法删除 {dir}：{ex.Message}");
            }
        }
    }
}
