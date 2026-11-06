using Microsoft.Win32;
using ZulipQQ.Services;

namespace ZulipQQ.Tests.Services;

public sealed class LaunchAtStartupTests : IDisposable
{
    private const string Exe = @"C:\Program Files\ZulipQQ\ZulipQQ.exe";

    // 只用测试自己的键，不碰真实的启动项
    private readonly string _keyPath = @"Software\ZulipQQ.Tests\" + Guid.NewGuid().ToString("N");

    public void Dispose() => Registry.CurrentUser.DeleteSubKeyTree(@"Software\ZulipQQ.Tests", throwOnMissingSubKey: false);

    private LaunchAtStartup Startup(string exe = Exe) => new(_keyPath, "ZulipQQ", exe);

    private object? RawValue()
    {
        using var key = Registry.CurrentUser.OpenSubKey(_keyPath);
        return key?.GetValue("ZulipQQ");
    }

    [Fact]
    public void IsDisabled_WhenKeyDoesNotExist() => Assert.False(Startup().IsEnabled);

    [Fact]
    public void Enable_WritesQuotedPath()
    {
        Startup().Set(true);

        Assert.True(Startup().IsEnabled);
        Assert.Equal($"\"{Exe}\"", RawValue());
    }

    [Fact]
    public void Disable_RemovesValue()
    {
        Startup().Set(true);
        Startup().Set(false);

        Assert.False(Startup().IsEnabled);
        Assert.Null(RawValue());
    }

    [Fact]
    public void Disable_WhenAlreadyDisabled_DoesNothing()
    {
        Startup().Set(false);

        Assert.False(Startup().IsEnabled);
    }

    [Fact]
    public void IsDisabled_WhenValuePointsToAnotherLocation()
    {
        Startup(@"D:\Old\ZulipQQ.exe").Set(true);

        Assert.False(Startup().IsEnabled);
    }

    [Fact]
    public void Enable_RepointsToCurrentLocation()
    {
        Startup(@"D:\Old\ZulipQQ.exe").Set(true);
        Startup().Set(true);

        Assert.True(Startup().IsEnabled);
    }
}
