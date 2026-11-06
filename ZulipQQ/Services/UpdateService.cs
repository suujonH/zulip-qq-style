using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using ZulipQQ.Update;

namespace ZulipQQ.Services;

/// <summary>检查更新的结果。</summary>
public enum UpdateCheckStatus
{
    /// <summary>已是最新版本。</summary>
    UpToDate,

    /// <summary>新版本已下载、校验并解压好，等待替换。</summary>
    Ready,

    /// <summary>有新版本，但这个程序不是发布包安装的（或目录不可写），不能自动更新。</summary>
    ManualOnly,

    /// <summary>有新版本，但它之前更新失败过，自动更新不再尝试；手动检查时会重试。</summary>
    Skipped,

    /// <summary>GitHub 上还没有发布版本。</summary>
    NoRelease,

    /// <summary>检查或下载失败。</summary>
    Failed,
}

public sealed record UpdateCheckResult(UpdateCheckStatus Status, string Message);

/// <summary>
/// 自动更新（主程序一侧）：从 GitHub Releases 检查最新版本，下载、校验并解压到更新工作目录，然后启动独立更新器。
/// 替换和回滚由更新器完成（见 <see cref="UpdateApplier"/>）。
/// </summary>
public sealed class UpdateService(HttpClient http, Uri releasesBase, UpdateLayout layout, string appDir, AppVersion? current,
    bool? canSelfUpdate = null)
{
    /// <summary>
    /// 本仓库的 Releases。<c>latest/download</c> 只指向最新的正式版（不含预发布版和草稿），也不受 GitHub API 的请求频率限制。
    /// </summary>
    private const string ReleasesUrl = "https://github.com/suujonH/zulip-qq-style/releases/";

    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(60);

    private readonly object _gate = new();
    private Task<UpdateCheckResult>? _running;
    private bool? _canSelfUpdate = canSelfUpdate;

    private static UpdateService? _instance;

    public static UpdateService Instance => _instance ??= Create();

    /// <summary>程序的版本号（程序集的 InformationalVersion，发布时由 tag 注入）。</summary>
    public static string CurrentVersionText { get; } =
        typeof(UpdateService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";

    /// <summary>检查的进度和结果（可能在后台线程触发）。</summary>
    public event EventHandler? StatusChanged;

    /// <summary>新版本已准备好。</summary>
    public event EventHandler? UpdateReady;

    public string StatusText { get; private set; } = "";

    public bool IsBusy { get; private set; }

    /// <summary>上次更新失败的原因；已经发布了更高的版本或更新成功后为 null。</summary>
    public string? LastError => UpdateState.Load(layout.StatePath).LastError;

    private static UpdateService Create()
    {
#if UPDATE_E2E
        // 验证用的构建（-p:UpdateE2E=true）：从本地模拟的 Release 更新，工作目录放在测试目录下。发布的程序不包含这段代码
        var feed = new Uri(Environment.GetEnvironmentVariable("ZULIPQQ_E2E_FEED")!);
        var root = Path.Combine(Environment.GetEnvironmentVariable("ZULIPQQ_E2E_ROOT")!, "update");
#else
        var feed = new Uri(ReleasesUrl);
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZulipQQ.Update");
#endif
        var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan, MaxResponseContentBufferSize = 1024 * 1024 };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ZulipQQ-Updater");
        return new UpdateService(client, feed, new UpdateLayout(root), AppContext.BaseDirectory, AppVersion.TryParse(CurrentVersionText));
    }

    /// <summary>
    /// 这个程序能否自动更新：必须是发布包安装的（程序目录中有更新器和文件清单）且程序目录可写。Debug 构建不自动更新。
    /// </summary>
    public bool CanSelfUpdate => _canSelfUpdate ??= DetectCanSelfUpdate();

    private bool DetectCanSelfUpdate()
    {
#if DEBUG
        return false;
#else
        return File.Exists(Path.Combine(appDir, UpdateLayout.UpdaterExeName))
            && File.Exists(Path.Combine(appDir, UpdateLayout.FileListName))
            && IsDirectoryWritable(appDir);
#endif
    }

    /// <summary>
    /// 程序入口的第一步（Avalonia 启动之前）：上次已经准备好新版本时启动更新器，返回 true，调用方应立即退出，
    /// 用户看不到旧版本的界面。作为更新器启动的新版本确认启动成功之前不做任何事。
    /// </summary>
    public bool TryApplyStaged(string[] args)
    {
        if (args.Contains(UpdateLayout.ProbeArg) || !CanSelfUpdate || current is null)
            return false;
        Cleanup();
        var manifest = LoadReady();
        if (manifest is null || AppVersion.TryParse(manifest.Version) is not { } ready)
            return false;
        // 已经不需要了（版本不再更新，或失败过不再尝试）
        if (!ready.IsNewerThan(current) || !UpdateState.Load(layout.StatePath).AllowsAutoAttempt(manifest.Version))
        {
            ClearStaged();
            return false;
        }
        if (new SystemProcessHost().IsAppRunning(appDir, Environment.ProcessId))
        {
            Log.Info("其它实例正在运行，本次启动不应用更新");
            return false;
        }
        return StartUpdater();
    }

    /// <summary>
    /// 启动独立更新器（复制到更新工作目录后运行，它会等本进程退出）。返回是否启动成功；失败时本进程应继续运行或重启自己。
    /// </summary>
    public bool StartUpdater()
    {
        try
        {
            var manifest = LoadReady() ?? throw new InvalidOperationException("没有准备好的新版本");
            Directory.CreateDirectory(layout.RunnerDir);
            var updater = Path.Combine(layout.RunnerDir, UpdateLayout.UpdaterExeName);
            File.Copy(Path.Combine(appDir, UpdateLayout.UpdaterExeName), updater, overwrite: true);
            new SystemProcessHost().Start(updater,
                "--pid", Environment.ProcessId.ToString(CultureInfo.InvariantCulture),
                "--app-dir", Path.TrimEndingDirectorySeparator(appDir),
                "--update-root", layout.Root,
                "--from", current?.ToString() ?? "",
                "--to", manifest.Version,
                "--log", Path.Combine(AppPaths.LogDir, "updater.log")).Dispose();
            Log.Info($"已启动更新器，准备更新到 {manifest.Version}");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Log.Error("启动更新器失败", ex);
            return false;
        }
    }

    /// <summary>
    /// 启动后调用：作为更新器启动的新版本报告启动成功；把更新器的结果写入日志，清理上次更新的残留。
    /// </summary>
    public void OnStarted(string[] args)
    {
        var index = Array.IndexOf(args, UpdateLayout.ProbeArg);
        if (index >= 0 && index + 1 < args.Length)
        {
            try
            {
                File.WriteAllText(args[index + 1], current?.ToString() ?? CurrentVersionText);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Error("无法报告启动成功", ex);
            }
            return;
        }
        Cleanup();
        var state = UpdateState.Load(layout.StatePath);
        if (state.PendingReport is { } report)
        {
            Log.Info("自动更新：" + report);
            try
            {
                (state with { PendingReport = null }).Save(layout.StatePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn("无法保存更新状态", ex);
            }
        }
    }

    /// <summary>
    /// 检查最新版本；有新版本且可以自动更新时下载、校验并解压。同时只运行一次，运行中再次调用得到同一个结果。
    /// <paramref name="manual"/> 为 true 时（用户点击“确认版本”）重试之前失败过的版本。
    /// </summary>
    public Task<UpdateCheckResult> CheckAsync(bool manual, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_running is { IsCompleted: false })
                return _running;
            // 在后台线程运行：下载和校验不能占用 UI 线程
            IsBusy = true;
            return _running = Task.Run(() => RunCheckAsync(manual, ct), CancellationToken.None);
        }
    }

    private async Task<UpdateCheckResult> RunCheckAsync(bool manual, CancellationToken ct)
    {
        SetStatus("正在检查更新…");
        UpdateCheckResult result;
        try
        {
            result = await CheckCoreAsync(manual, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            result = new UpdateCheckResult(UpdateCheckStatus.Failed, "已取消");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            Log.Warn("检查更新失败", ex);
            result = new UpdateCheckResult(UpdateCheckStatus.Failed, "检查更新失败，请检查网络后重试");
        }
        Log.Info($"检查更新：{result.Status}，{result.Message}");
        IsBusy = false;
        SetStatus(result.Message);
        if (result.Status == UpdateCheckStatus.Ready)
            UpdateReady?.Invoke(this, EventArgs.Empty);
        return result;
    }

    private async Task<UpdateCheckResult> CheckCoreAsync(bool manual, CancellationToken ct)
    {
        if (current is null)
            return new UpdateCheckResult(UpdateCheckStatus.Failed, $"无法识别当前版本 {CurrentVersionText}");

        var manifest = await FetchManifestAsync(ct);
        if (manifest is null)
            return new UpdateCheckResult(UpdateCheckStatus.NoRelease, "GitHub 上还没有发布版本");
        var latest = AppVersion.TryParse(manifest.Version)!;
        if (!latest.IsNewerThan(current))
            return new UpdateCheckResult(UpdateCheckStatus.UpToDate, $"已是最新版本（{current}）");
        if (!CanSelfUpdate)
            return new UpdateCheckResult(UpdateCheckStatus.ManualOnly,
                $"发现新版本 {manifest.Version}。当前程序不支持自动更新（不是发布包安装或程序目录不可写），请到 GitHub Releases 下载");

        var state = UpdateState.Load(layout.StatePath);
        if (!state.AllowsAutoAttempt(manifest.Version))
        {
            if (!manual)
                return new UpdateCheckResult(UpdateCheckStatus.Skipped,
                    $"新版本 {manifest.Version} 更新失败过，不再自动尝试，点击“确认版本”可以重试");
            state.Cleared().Save(layout.StatePath);
        }

        if (IsReady(manifest))
            return Ready(manifest);

        try
        {
            await PrepareAsync(manifest, ct);
        }
        catch (PackageInvalidException ex)
        {
            Log.Warn($"更新包校验失败：{manifest.Version}", ex);
            UpdateState.Load(layout.StatePath).WithFailure(manifest.Version, ex.Message).Save(layout.StatePath);
            return new UpdateCheckResult(UpdateCheckStatus.Failed, $"新版本 {manifest.Version} 校验失败：{ex.Message}");
        }
        finally
        {
            TryDeleteDirectory(layout.WorkDir);
        }
        return Ready(manifest);
    }

    private static UpdateCheckResult Ready(UpdateManifest manifest) =>
        new(UpdateCheckStatus.Ready, $"新版本 {manifest.Version} 已下载，空闲时自动重启完成更新");

    // 没有发布版本（404）时返回 null；清单内容不合法时抛出异常
    private async Task<UpdateManifest?> FetchManifestAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(CheckTimeout);
        using var response = await http.GetAsync(new Uri(releasesBase, "latest/download/" + UpdateManifest.FileName), timeout.Token);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(timeout.Token);
        return UpdateManifest.Parse(json) ?? throw new IOException("更新清单内容不合法");
    }

    // 下载 → 校验 → 解压到临时目录 → 校验 → 移到 staged → 写入 ready.json；任何一步失败都不会留下“已准备好”的状态
    private async Task PrepareAsync(UpdateManifest manifest, CancellationToken ct)
    {
        ClearStaged();
        Directory.CreateDirectory(layout.WorkDir);
        var zip = Path.Combine(layout.WorkDir, "package.zip");
        await DownloadAsync(manifest, zip, ct);
        PackageVerifier.VerifyArchive(zip, manifest);

        var extracted = Path.Combine(layout.WorkDir, "package");
        await Task.Run(() =>
        {
            PackageVerifier.ExtractSafely(zip, extracted);
            PackageVerifier.VerifyTree(extracted);
        }, ct);
        Directory.Move(extracted, layout.StagedDir);
        await File.WriteAllTextAsync(layout.ReadyPath, manifest.ToJson(), ct);
    }

    private async Task DownloadAsync(UpdateManifest manifest, string path, CancellationToken ct)
    {
        var url = new Uri(releasesBase, $"download/v{manifest.Version}/{manifest.Package}");
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stall.CancelAfter(StallTimeout);
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, stall.Token);
        response.EnsureSuccessStatusCode();
        await using var source = await response.Content.ReadAsStreamAsync(stall.Token);
        await using var target = File.Create(path);

        var buffer = new byte[81920];
        long done = 0;
        var lastPercent = -1L;
        while (true)
        {
            // 连续一段时间读不到数据才算超时：大文件在慢速网络上可以下载很久
            stall.CancelAfter(StallTimeout);
            var read = await source.ReadAsync(buffer, stall.Token);
            if (read == 0)
                break;
            done += read;
            // 比清单声明的大就不用继续了
            if (done > manifest.Size)
                throw new PackageInvalidException($"更新包大小不符：应为 {manifest.Size}");
            await target.WriteAsync(buffer.AsMemory(0, read), stall.Token);
            var percent = done * 100 / manifest.Size;
            if (percent != lastPercent)
            {
                lastPercent = percent;
                SetStatus($"正在下载新版本 {manifest.Version}（{percent}%）");
            }
        }
    }

    private UpdateManifest? LoadReady()
    {
        try
        {
            return File.Exists(layout.ReadyPath) ? UpdateManifest.Parse(File.ReadAllText(layout.ReadyPath)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private bool IsReady(UpdateManifest manifest) =>
        Directory.Exists(layout.StagedDir)
        && LoadReady() is { } ready
        && string.Equals(ready.Sha256, manifest.Sha256, StringComparison.OrdinalIgnoreCase);

    private void ClearStaged()
    {
        try
        {
            File.Delete(layout.ReadyPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("清理更新文件失败", ex);
        }
        TryDeleteDirectory(layout.StagedDir);
        TryDeleteDirectory(layout.WorkDir);
    }

    // 上次更新的残留：更新器副本（它运行时不能删除自己），更新器被中断时留下的备份。
    // 还有其它实例在运行时不清理：可能是更新器正在等它确认启动，备份还要用于回滚
    private void Cleanup()
    {
        if (!CanSelfUpdate || new SystemProcessHost().IsAppRunning(appDir, Environment.ProcessId))
            return;
        TryDeleteDirectory(layout.RunnerDir);
        try
        {
            FileSwap.RemoveLeftoverBackup(appDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("清理更新备份失败", ex);
        }
    }

    private static void TryDeleteDirectory(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"清理更新文件失败：{dir}", ex);
        }
    }

    private static bool IsDirectoryWritable(string dir)
    {
        try
        {
            var probe = Path.Combine(dir, ".update-write-test");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private void SetStatus(string text)
    {
        StatusText = text;
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>用户多久没有操作键盘鼠标（整个系统）。</summary>
    public static TimeSpan UserIdleTime()
    {
        var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        if (!GetLastInputInfo(ref info))
            return TimeSpan.Zero;
        return TimeSpan.FromMilliseconds((uint)Environment.TickCount - info.Time);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint Size;
        public uint Time;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LastInputInfo info);
}
