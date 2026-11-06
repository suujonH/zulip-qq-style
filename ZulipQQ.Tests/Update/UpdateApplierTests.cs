using ZulipQQ.Update;

namespace ZulipQQ.Tests.Update;

public sealed class UpdateApplierTests : IDisposable
{
    private readonly TempDir _temp = new();
    private readonly UpdateLayout _layout;
    private readonly List<string> _log = [];
    private readonly FakeHost _host = new();

    public UpdateApplierTests()
    {
        _layout = new UpdateLayout(_temp.Sub("update"));
        Directory.CreateDirectory(AppDir);
        Directory.CreateDirectory(_layout.Root);
        PackageBuilder.WriteTree(AppDir, PackageBuilder.Version("old", "old-only.dll", @"plugins\builtin\old.dll", "shared.dll"));
        // 不属于程序的文件：用户把 zip 解压到了放着其它文件的目录，或自己安装了插件
        File.WriteAllText(Path.Combine(AppDir, "user-notes.txt"), "mine");
        Directory.CreateDirectory(Path.Combine(AppDir, @"plugins\my-plugin"));
        File.WriteAllText(Path.Combine(AppDir, @"plugins\my-plugin\plugin.dll"), "mine");

        PackageBuilder.WriteTree(_layout.StagedDir, PackageBuilder.Version("new", "new-only.dll", @"plugins\builtin\new.dll", "shared.dll"));
        File.WriteAllText(_layout.ReadyPath, new UpdateManifest("2.0.0", "p.zip", new string('a', 64), 1).ToJson());
        _host.OnStart = (_, args) => WriteProbe(args, "2.0.0");
    }

    public void Dispose() => _temp.Dispose();

    private string AppDir => _temp.Sub("app");

    private static void WriteProbe(string[] args, string version)
    {
        if (args.Length == 2)
            File.WriteAllText(args[1], version);
    }

    private UpdateApplier Applier() => new(new UpdateRequest(1234, AppDir, _layout.Root, "1.0.0", "2.0.0"), _host, _log.Add)
    {
        ProbeTimeout = TimeSpan.FromSeconds(5),
        StableTime = TimeSpan.Zero,
        PollInterval = TimeSpan.FromMilliseconds(5),
        FileRetryDelay = TimeSpan.Zero,
    };

    private string App(string relative) => File.ReadAllText(Path.Combine(AppDir, relative));

    private bool AppHas(string relative) => File.Exists(Path.Combine(AppDir, relative));

    private void AssertOldVersionIntact()
    {
        Assert.Equal("app old", App(UpdateLayout.AppExeName));
        Assert.Equal("old-only.dll old", App("old-only.dll"));
        Assert.Equal("shared.dll old", App("shared.dll"));
        Assert.Equal(@"plugins\builtin\old.dll old", App(@"plugins\builtin\old.dll"));
        Assert.False(AppHas("new-only.dll"));
        Assert.False(AppHas(@"plugins\builtin\new.dll"));
        Assert.False(Directory.Exists(Path.Combine(AppDir, UpdateLayout.BackupDirName)));
        PackageVerifier.VerifyFiles(AppDir, FileEntry.ParseList(File.ReadAllLines(Path.Combine(AppDir, UpdateLayout.FileListName))));
        AssertUserFilesIntact();
    }

    private void AssertUserFilesIntact()
    {
        Assert.Equal("mine", App("user-notes.txt"));
        Assert.Equal("mine", App(@"plugins\my-plugin\plugin.dll"));
    }

    [Fact]
    public void Success_installs_new_version_and_removes_old_files()
    {
        Assert.Equal(ApplyResult.Updated, Applier().Run());

        Assert.Equal("app new", App(UpdateLayout.AppExeName));
        Assert.Equal("shared.dll new", App("shared.dll"));
        Assert.Equal("new-only.dll new", App("new-only.dll"));
        Assert.Equal(@"plugins\builtin\new.dll new", App(@"plugins\builtin\new.dll"));
        Assert.False(AppHas("old-only.dll"));
        Assert.False(AppHas(@"plugins\builtin\old.dll"));
        Assert.False(Directory.Exists(Path.Combine(AppDir, UpdateLayout.BackupDirName)));
        AssertUserFilesIntact();

        Assert.Equal(1234, _host.WaitedFor);
        var start = Assert.Single(_host.Starts);
        Assert.Equal(Path.Combine(AppDir, UpdateLayout.AppExeName), start.Exe);
        Assert.Equal([UpdateLayout.ProbeArg, _layout.ProbePath], start.Args);

        var state = UpdateState.Load(_layout.StatePath);
        Assert.True(state.AllowsAutoAttempt("2.0.0"));
        Assert.Contains("2.0.0", state.PendingReport);
        Assert.False(Directory.Exists(_layout.StagedDir));
        Assert.False(File.Exists(_layout.ReadyPath));
    }

    [Fact]
    public void Removes_old_plugin_directory_only_when_empty()
    {
        Applier().Run();
        Assert.True(Directory.Exists(Path.Combine(AppDir, @"plugins\my-plugin")));
        Assert.True(Directory.Exists(Path.Combine(AppDir, @"plugins\builtin")));
    }

    [Fact]
    public void New_version_crashing_rolls_back_starts_old_and_blocks_version()
    {
        _host.OnStart = (app, args) =>
        {
            if (args.Length == 2)
                app.Exited = true;
        };

        Assert.Equal(ApplyResult.RolledBack, Applier().Run());

        AssertOldVersionIntact();
        AssertStartedOldAfterProbe();
        var state = UpdateState.Load(_layout.StatePath);
        Assert.True(state.Blocked);
        Assert.False(state.AllowsAutoAttempt("2.0.0"));
        Assert.True(state.AllowsAutoAttempt("2.0.1"));
        Assert.False(File.Exists(_layout.ReadyPath));
    }

    [Fact]
    public void New_version_never_confirming_is_killed_and_rolled_back()
    {
        _host.OnStart = (_, _) => { };
        var applier = new UpdateApplier(new UpdateRequest(1234, AppDir, _layout.Root, "1.0.0", "2.0.0"), _host, _log.Add)
        {
            ProbeTimeout = TimeSpan.FromMilliseconds(50),
            PollInterval = TimeSpan.FromMilliseconds(5),
            FileRetryDelay = TimeSpan.Zero,
        };

        Assert.Equal(ApplyResult.RolledBack, applier.Run());

        Assert.True(_host.Started[0].Killed);
        AssertOldVersionIntact();
        AssertStartedOldAfterProbe();
    }

    [Fact]
    public void New_version_reporting_wrong_version_is_rolled_back()
    {
        // 否则新程序认为自己仍是旧版本，每次启动都会再更新
        _host.OnStart = (_, args) => WriteProbe(args, "1.0.0");

        Assert.Equal(ApplyResult.RolledBack, Applier().Run());

        Assert.True(_host.Started[0].Killed);
        AssertOldVersionIntact();
        Assert.True(UpdateState.Load(_layout.StatePath).Blocked);
    }

    [Fact]
    public void New_version_exiting_right_after_confirming_is_rolled_back()
    {
        _host.OnStart = (app, args) =>
        {
            WriteProbe(args, "2.0.0");
            app.Exited = args.Length == 2;
        };
        var applier = new UpdateApplier(new UpdateRequest(1234, AppDir, _layout.Root, "1.0.0", "2.0.0"), _host, _log.Add)
        {
            ProbeTimeout = TimeSpan.FromSeconds(5),
            StableTime = TimeSpan.FromMilliseconds(20),
            PollInterval = TimeSpan.FromMilliseconds(5),
            FileRetryDelay = TimeSpan.Zero,
        };

        Assert.Equal(ApplyResult.RolledBack, applier.Run());
        AssertOldVersionIntact();
    }

    [Fact]
    public void New_version_failing_to_launch_is_rolled_back()
    {
        _host.OnStart = (_, args) =>
        {
            if (args.Length == 2)
                throw new InvalidOperationException("无法启动");
        };

        Assert.Equal(ApplyResult.RolledBack, Applier().Run());

        AssertOldVersionIntact();
        Assert.Empty(_host.Starts[^1].Args);
    }

    [Fact]
    public void Corrupted_staged_files_fail_before_touching_the_app()
    {
        File.WriteAllText(Path.Combine(_layout.StagedDir, "new-only.dll"), "tampered");

        Assert.Equal(ApplyResult.RolledBack, Applier().Run());

        AssertOldVersionIntact();
        var start = Assert.Single(_host.Starts);
        Assert.Empty(start.Args);
        var state = UpdateState.Load(_layout.StatePath);
        Assert.Equal(1, state.Failures);
        Assert.False(state.Blocked);
    }

    [Fact]
    public void Staged_version_differing_from_request_is_refused()
    {
        File.WriteAllText(_layout.ReadyPath, new UpdateManifest("3.0.0", "p.zip", new string('a', 64), 1).ToJson());

        Assert.Equal(ApplyResult.RolledBack, Applier().Run());
        AssertOldVersionIntact();
    }

    [Fact]
    public void Locked_file_during_swap_rolls_back_and_counts_failure()
    {
        using var locked = new FileStream(Path.Combine(AppDir, "shared.dll"), FileMode.Open, FileAccess.Read, FileShare.None);

        Assert.Equal(ApplyResult.RolledBack, Applier().Run());
        locked.Dispose();

        AssertOldVersionIntact();
        Assert.Empty(Assert.Single(_host.Starts).Args);
        var state = UpdateState.Load(_layout.StatePath);
        Assert.Equal(1, state.Failures);
        Assert.False(state.Blocked);
    }

    [Fact]
    public void Another_instance_running_skips_without_changes()
    {
        _host.AppRunning = true;

        Assert.Equal(ApplyResult.Skipped, Applier().Run());

        Assert.Empty(_host.Starts);
        Assert.Equal("app old", App(UpdateLayout.AppExeName));
        Assert.True(File.Exists(_layout.ReadyPath));
        Assert.True(UpdateState.Load(_layout.StatePath).AllowsAutoAttempt("2.0.0"));
    }

    [Fact]
    public void Failure_is_recorded_before_old_version_starts()
    {
        // 旧程序启动时要能读到失败记录，否则会立即再次尝试
        UpdateState? seen = null;
        _host.OnStart = (app, args) =>
        {
            if (args.Length == 2)
                app.Exited = true;
            else
                seen = UpdateState.Load(_layout.StatePath);
        };

        Applier().Run();

        Assert.NotNull(seen);
        Assert.False(seen.AllowsAutoAttempt("2.0.0"));
    }

    private void AssertStartedOldAfterProbe()
    {
        Assert.Equal(2, _host.Starts.Count);
        Assert.Equal(2, _host.Starts[0].Args.Length);
        Assert.Empty(_host.Starts[1].Args);
    }

    private sealed class FakeApp : IStartedApp
    {
        public bool Exited { get; set; }

        public bool Killed { get; private set; }

        public bool HasExited => Exited || Killed;

        public void Kill() => Killed = true;

        public void Dispose()
        {
        }
    }

    private sealed class FakeHost : IProcessHost
    {
        public Action<FakeApp, string[]> OnStart { get; set; } = (_, _) => { };

        public bool AppRunning { get; set; }

        public int? WaitedFor { get; private set; }

        public List<(string Exe, string[] Args)> Starts { get; } = [];

        public List<FakeApp> Started { get; } = [];

        public void WaitForExit(int processId, TimeSpan timeout) => WaitedFor = processId;

        public bool IsAppRunning(string appDir, int exceptProcessId) => AppRunning;

        public IStartedApp Start(string exePath, params string[] args)
        {
            Starts.Add((exePath, args));
            var app = new FakeApp();
            Started.Add(app);
            OnStart(app, args);
            return app;
        }
    }
}
