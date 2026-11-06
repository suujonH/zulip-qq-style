using ZulipQQ.Update;

namespace ZulipQQ.Tests.Update;

public class UpdateStateTests
{
    [Fact]
    public void Failures_below_limit_still_allow_auto_attempt()
    {
        var state = new UpdateState();
        for (var i = 1; i < UpdateState.MaxFailures; i++)
        {
            state = state.WithFailure("1.0.0", "x");
            Assert.True(state.AllowsAutoAttempt("1.0.0"));
        }
        state = state.WithFailure("1.0.0", "x");
        Assert.False(state.AllowsAutoAttempt("1.0.0"));
    }

    [Fact]
    public void Blocked_version_is_skipped_after_first_failure()
    {
        var state = new UpdateState().WithFailure("1.0.0", "启动失败", block: true);
        Assert.False(state.AllowsAutoAttempt("1.0.0"));
        Assert.False(state.AllowsAutoAttempt("v1.0.0"));
    }

    [Fact]
    public void Higher_version_is_allowed_and_resets_the_count()
    {
        var state = new UpdateState().WithFailure("1.0.0", "x", block: true);
        Assert.True(state.AllowsAutoAttempt("1.0.1"));

        state = state.WithFailure("1.0.1", "y");
        Assert.Equal(1, state.Failures);
        Assert.False(state.Blocked);
    }

    [Fact]
    public void Clear_allows_retry_and_keeps_pending_report()
    {
        var state = new UpdateState { PendingReport = "r" }.WithFailure("1.0.0", "x", block: true).Cleared();
        Assert.True(state.AllowsAutoAttempt("1.0.0"));
        Assert.Null(state.LastError);
        Assert.Equal("r", state.PendingReport);
    }

    [Fact]
    public void Persists_and_treats_corrupt_file_as_empty()
    {
        using var temp = new TempDir();
        var path = temp.Sub("state.json");
        Assert.Equal(new UpdateState(), UpdateState.Load(path));

        var state = new UpdateState().WithFailure("1.0.0", "原因", block: true);
        state.Save(path);
        Assert.Equal(state, UpdateState.Load(path));

        File.WriteAllText(path, "{ broken");
        Assert.Equal(new UpdateState(), UpdateState.Load(path));
    }
}
