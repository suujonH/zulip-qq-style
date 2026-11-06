using ZulipQQ.Core;

namespace ZulipQQ.Tests.Core;

public sealed class RemarkFormatTests
{
    [Theory]
    [InlineData("小王", "王大锤", "小王(王大锤)")]
    [InlineData(null, "王大锤", "王大锤")]
    [InlineData("", "王大锤", "王大锤")]
    [InlineData("  ", "王大锤", "王大锤")]
    // 备注与原名称相同时不重复显示
    [InlineData("王大锤", "王大锤", "王大锤")]
    [InlineData("发布", "release (v2)", "发布(release (v2))")]
    public void WithOriginal(string? remark, string original, string expected) =>
        Assert.Equal(expected, RemarkFormat.WithOriginal(remark, original));

    [Theory]
    [InlineData(" 备注 ", "备注")]
    [InlineData("", null)]
    [InlineData(" \t", null)]
    [InlineData(null, null)]
    public void Normalize(string? input, string? expected) =>
        Assert.Equal(expected, RemarkFormat.Normalize(input));
}
