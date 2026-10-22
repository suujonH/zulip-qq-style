using ZulipQQ.Core;

namespace ZulipQQ.Tests.Core;

public class TimeTextTests
{
    // 2026-10-22 是星期四
    private static readonly DateTime Now = new(2026, 10, 22, 15, 30, 0);

    [Theory]
    [InlineData("2026-10-22 09:05", "09:05")]
    [InlineData("2026-10-21 23:59", "昨天23:59")]
    [InlineData("2026-01-02 08:00", "01/02")]
    [InlineData("2025-12-31 08:00", "2025/12/31")]
    public void ListTime(string time, string expected) =>
        Assert.Equal(expected, TimeText.ListTime(DateTime.Parse(time), Now));

    [Theory]
    [InlineData("2026-10-22 09:05", "09:05")]
    [InlineData("2026-10-21 23:59", "昨天 23:59")]
    [InlineData("2026-10-20 08:00", "星期二 08:00")]
    [InlineData("2026-10-18 08:00", "星期日 08:00")]
    [InlineData("2026-10-16 08:00", "星期五 08:00")]
    [InlineData("2026-10-15 08:00", "2026/10/15 08:00")]
    public void SeparatorTime(string time, string expected) =>
        Assert.Equal(expected, TimeText.SeparatorTime(DateTime.Parse(time), Now));

    [Theory]
    [InlineData("2026-03-04 05:06:07", "03-04 05:06:07")]
    [InlineData("2025-03-04 05:06:07", "2025-03-04 05:06:07")]
    public void PreciseTime(string time, string expected) =>
        Assert.Equal(expected, TimeText.PreciseTime(DateTime.Parse(time), Now));

    [Theory]
    [InlineData("2026-10-22 00:01", "今天")]
    [InlineData("2026-10-21 23:59", "10月")]
    [InlineData("2026-06-01 08:00", "6月")]
    [InlineData("2025-12-31 08:00", "2025年12月")]
    public void MonthGroup(string time, string expected) =>
        Assert.Equal(expected, TimeText.MonthGroup(DateTime.Parse(time), Now));

    [Fact]
    public void NeedsSeparator_At30Minutes()
    {
        Assert.False(TimeText.NeedsSeparator(1000, 1000 + 1799));
        Assert.True(TimeText.NeedsSeparator(1000, 1000 + 1800));
    }
}
