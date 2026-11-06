using System.Net;
using ZulipQQ.Services;
using ZulipQQ.Update;

namespace ZulipQQ.Tests.Update;

public sealed class UpdateServiceTests : IDisposable
{
    private static readonly Uri Feed = new("https://example.test/releases/");

    private readonly TempDir _temp = new();
    private readonly UpdateLayout _layout;
    private readonly FakeHandler _handler = new();

    public UpdateServiceTests()
    {
        _layout = new UpdateLayout(_temp.Sub("update"));
        Directory.CreateDirectory(_temp.Sub("app"));
    }

    public void Dispose() => _temp.Dispose();

    private UpdateService Service(string current = "1.0.0", bool canSelfUpdate = true) =>
        new(new HttpClient(_handler), Feed, _layout, _temp.Sub("app"), AppVersion.TryParse(current), canSelfUpdate);

    private UpdateManifest Publish(string version, byte[]? corruptZip = null)
    {
        var (zip, manifest) = PackageBuilder.Zip(version, PackageBuilder.Version(version), _temp.Path);
        _handler.Respond("latest/download/ZulipQQ-update.json", manifest.ToJson());
        _handler.Respond($"download/v{version}/{manifest.Package}", corruptZip ?? zip);
        return manifest;
    }

    private UpdateState State => UpdateState.Load(_layout.StatePath);

    [Fact]
    public async Task Same_or_older_release_is_up_to_date()
    {
        Publish("1.0.0");
        Assert.Equal(UpdateCheckStatus.UpToDate, (await Service().CheckAsync(false, TestContext.Current.CancellationToken)).Status);
        Assert.Equal(UpdateCheckStatus.UpToDate, (await Service("2.0.0").CheckAsync(false, TestContext.Current.CancellationToken)).Status);
        Assert.False(File.Exists(_layout.ReadyPath));
    }

    [Fact]
    public async Task Missing_release_is_reported()
    {
        Assert.Equal(UpdateCheckStatus.NoRelease, (await Service().CheckAsync(true, TestContext.Current.CancellationToken)).Status);
    }

    [Fact]
    public async Task Server_error_and_network_failure_do_not_count_against_the_version()
    {
        _handler.Fail = new HttpRequestException("offline");
        Assert.Equal(UpdateCheckStatus.Failed, (await Service().CheckAsync(false, TestContext.Current.CancellationToken)).Status);
        Assert.Equal(0, State.Failures);
    }

    [Fact]
    public async Task Invalid_manifest_fails_without_download()
    {
        _handler.Respond("latest/download/ZulipQQ-update.json", "not json");
        Assert.Equal(UpdateCheckStatus.Failed, (await Service().CheckAsync(false, TestContext.Current.CancellationToken)).Status);
    }

    [Fact]
    public async Task Newer_release_is_downloaded_verified_and_staged()
    {
        var manifest = Publish("1.1.0");
        var service = Service();
        var ready = 0;
        service.UpdateReady += (_, _) => ready++;

        var result = await service.CheckAsync(false, TestContext.Current.CancellationToken);

        Assert.Equal(UpdateCheckStatus.Ready, result.Status);
        Assert.Equal(1, ready);
        Assert.Equal(manifest, UpdateManifest.Parse(File.ReadAllText(_layout.ReadyPath)));
        PackageVerifier.VerifyTree(_layout.StagedDir);
        Assert.False(Directory.Exists(_layout.WorkDir));
        Assert.Contains("1.1.0", service.StatusText);
    }

    [Fact]
    public async Task Already_staged_version_is_not_downloaded_again()
    {
        var manifest = Publish("1.1.0");
        await Service().CheckAsync(false, TestContext.Current.CancellationToken);
        _handler.Requests.Clear();

        Assert.Equal(UpdateCheckStatus.Ready, (await Service().CheckAsync(false, TestContext.Current.CancellationToken)).Status);
        Assert.DoesNotContain(_handler.Requests, r => r.EndsWith(manifest.Package, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Without_self_update_only_reports_the_new_version()
    {
        Publish("1.1.0");
        var result = await Service(canSelfUpdate: false).CheckAsync(true, TestContext.Current.CancellationToken);
        Assert.Equal(UpdateCheckStatus.ManualOnly, result.Status);
        Assert.Contains("1.1.0", result.Message);
        Assert.False(File.Exists(_layout.ReadyPath));
    }

    [Fact]
    public async Task Hash_mismatch_is_not_staged_and_stops_after_max_failures()
    {
        Publish("1.1.0", corruptZip: new byte[10]);
        // 大小相同内容不同：SHA-256 不符
        var (_, manifest) = PackageBuilder.Zip("1.1.0", PackageBuilder.Version("1.1.0"), _temp.Path);
        _handler.Respond("latest/download/ZulipQQ-update.json", (manifest with { Size = 10 }).ToJson());

        for (var i = 0; i < UpdateState.MaxFailures; i++)
            Assert.Equal(UpdateCheckStatus.Failed, (await Service().CheckAsync(false, TestContext.Current.CancellationToken)).Status);

        Assert.False(File.Exists(_layout.ReadyPath));
        Assert.False(Directory.Exists(_layout.StagedDir));
        Assert.Equal(UpdateState.MaxFailures, State.Failures);

        _handler.Requests.Clear();
        Assert.Equal(UpdateCheckStatus.Skipped, (await Service().CheckAsync(false, TestContext.Current.CancellationToken)).Status);
        Assert.Single(_handler.Requests);
    }

    [Fact]
    public async Task Package_larger_than_manifest_is_rejected()
    {
        var manifest = Publish("1.1.0");
        _handler.Respond("latest/download/ZulipQQ-update.json", (manifest with { Size = 5 }).ToJson());

        Assert.Equal(UpdateCheckStatus.Failed, (await Service().CheckAsync(false, TestContext.Current.CancellationToken)).Status);
        Assert.Equal(1, State.Failures);
    }

    [Fact]
    public async Task Blocked_version_is_skipped_automatically_but_manual_check_retries()
    {
        Publish("1.1.0");
        new UpdateState().WithFailure("1.1.0", "启动失败", block: true).Save(_layout.StatePath);

        var auto = await Service().CheckAsync(false, TestContext.Current.CancellationToken);
        Assert.Equal(UpdateCheckStatus.Skipped, auto.Status);
        Assert.False(File.Exists(_layout.ReadyPath));

        Assert.Equal(UpdateCheckStatus.Ready, (await Service().CheckAsync(true, TestContext.Current.CancellationToken)).Status);
        Assert.True(State.AllowsAutoAttempt("1.1.0"));
    }

    [Fact]
    public async Task Higher_release_than_blocked_version_is_attempted()
    {
        Publish("1.2.0");
        new UpdateState().WithFailure("1.1.0", "启动失败", block: true).Save(_layout.StatePath);

        Assert.Equal(UpdateCheckStatus.Ready, (await Service().CheckAsync(false, TestContext.Current.CancellationToken)).Status);
    }

    [Fact]
    public void Staged_update_is_not_applied_when_app_cannot_self_update_or_during_probe()
    {
        Assert.False(Service(canSelfUpdate: false).TryApplyStaged([]));
        Assert.False(Service().TryApplyStaged([UpdateLayout.ProbeArg, "x"]));
        // 没有准备好的版本
        Assert.False(Service().TryApplyStaged([]));
    }

    [Fact]
    public void Staged_version_no_longer_needed_is_discarded()
    {
        Directory.CreateDirectory(_layout.StagedDir);
        File.WriteAllText(_layout.ReadyPath, new UpdateManifest("1.0.0", "p.zip", new string('a', 64), 1).ToJson());

        Assert.False(Service("1.0.0").TryApplyStaged([]));

        Assert.False(File.Exists(_layout.ReadyPath));
        Assert.False(Directory.Exists(_layout.StagedDir));
    }

    [Fact]
    public void Staged_blocked_version_is_discarded()
    {
        Directory.CreateDirectory(_layout.StagedDir);
        File.WriteAllText(_layout.ReadyPath, new UpdateManifest("1.1.0", "p.zip", new string('a', 64), 1).ToJson());
        new UpdateState().WithFailure("1.1.0", "x", block: true).Save(_layout.StatePath);

        Assert.False(Service().TryApplyStaged([]));
        Assert.False(File.Exists(_layout.ReadyPath));
    }

    [Fact]
    public void Probe_reports_version_without_touching_leftovers()
    {
        var probe = _temp.Sub("started.txt");
        var backup = Path.Combine(_temp.Sub("app"), UpdateLayout.BackupDirName);
        Directory.CreateDirectory(backup);

        Service("1.1.0").OnStarted([UpdateLayout.ProbeArg, probe]);

        Assert.Equal("1.1.0", File.ReadAllText(probe));
        Assert.True(Directory.Exists(backup));
    }

    [Fact]
    public void Normal_start_removes_leftover_backup_and_consumes_report()
    {
        var backup = Path.Combine(_temp.Sub("app"), UpdateLayout.BackupDirName);
        Directory.CreateDirectory(backup);
        new UpdateState { PendingReport = "结果" }.Save(_layout.StatePath);

        Service().OnStarted([]);

        Assert.False(Directory.Exists(backup));
        Assert.Null(State.PendingReport);
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, byte[]> _responses = [];

        public Exception? Fail { get; set; }

        public List<string> Requests { get; } = [];

        public void Respond(string relative, string content) => Respond(relative, System.Text.Encoding.UTF8.GetBytes(content));

        public void Respond(string relative, byte[] content) => _responses[new Uri(Feed, relative).ToString()] = content;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.ToString());
            if (Fail is not null)
                throw Fail;
            return Task.FromResult(_responses.TryGetValue(request.RequestUri.ToString(), out var content)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
