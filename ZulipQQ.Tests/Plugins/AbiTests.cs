using System.Runtime.InteropServices;
using ZulipQQ.Plugins;

namespace ZulipQQ.Tests.Plugins;

/// <summary>C# 结构体与 sdk/zulipqq_plugin.h 的 x64 布局一致（按自然对齐逐字段计算的值）。</summary>
public class AbiTests
{
    [Theory]
    [InlineData(typeof(ZqConversation), 40)]
    [InlineData(typeof(ZqMessage), 104)]
    [InlineData(typeof(ZqUser), 48)]
    [InlineData(typeof(ZqChannel), 32)]
    [InlineData(typeof(ZqTopic), 32)]
    [InlineData(typeof(ZqOutgoing), 56)]
    [InlineData(typeof(ZqHostApi), 168)]
    public void Size(Type type, int expected) => Assert.Equal(expected, Marshal.SizeOf(type));

    [Theory]
    [InlineData(typeof(ZqConversation), nameof(ZqConversation.StreamId), 8)]
    [InlineData(typeof(ZqConversation), nameof(ZqConversation.DmUserIds), 32)]
    [InlineData(typeof(ZqMessage), nameof(ZqMessage.Conv), 48)]
    [InlineData(typeof(ZqMessage), nameof(ZqMessage.ContentHtml), 88)]
    [InlineData(typeof(ZqMessage), nameof(ZqMessage.ContentText), 96)]
    [InlineData(typeof(ZqUser), nameof(ZqUser.IsBot), 32)]
    [InlineData(typeof(ZqUser), nameof(ZqUser.Remark), 40)]
    [InlineData(typeof(ZqChannel), nameof(ZqChannel.Name), 16)]
    [InlineData(typeof(ZqTopic), nameof(ZqTopic.Name), 24)]
    [InlineData(typeof(ZqOutgoing), nameof(ZqOutgoing.Conv), 8)]
    [InlineData(typeof(ZqOutgoing), nameof(ZqOutgoing.Content), 48)]
    [InlineData(typeof(ZqHostApi), nameof(ZqHostApi.Ctx), 8)]
    [InlineData(typeof(ZqHostApi), nameof(ZqHostApi.AddLog), 16)]
    [InlineData(typeof(ZqHostApi), nameof(ZqHostApi.SetOutgoingContent), 160)]
    public void Offset(Type type, string field, int expected) => Assert.Equal(expected, (int)Marshal.OffsetOf(type, field));
}
