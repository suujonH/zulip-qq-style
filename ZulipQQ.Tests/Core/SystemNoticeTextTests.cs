using ZulipQQ.Core;

namespace ZulipQQ.Tests.Core;

public sealed class SystemNoticeTextTests
{
    private const string User = "<span class=\"user-mention silent\" data-user-id=\"8\">群主大大</span>";
    private const string Topic = "<a class=\"stream-topic\" data-stream-id=\"4\" href=\"/#narrow/channel/4/topic/x\">#开发 &gt; 发布计划</a>";

    [Theory]
    [InlineData($"<p>This topic was moved here from {Topic} by {User}.</p>", $"<p>{User} 将此话题从 {Topic} 移到了这里。</p>")]
    [InlineData($"<p>This topic was moved to {Topic} by {User}.</p>", $"<p>{User} 将此话题移到了 {Topic}。</p>")]
    [InlineData($"<p>A message was moved here from {Topic} by {User}.</p>", $"<p>{User} 从 {Topic} 移来了 1 条消息。</p>")]
    [InlineData($"<p>3 messages were moved from this topic to {Topic} by {User}.</p>", $"<p>{User} 将 3 条消息 移到了 {Topic}。</p>")]
    [InlineData($"<p>{User} has marked this topic as resolved.</p>", $"<p>{User} 将此话题标记为已解决。</p>")]
    [InlineData($"<p>{User} has marked this topic as unresolved.</p>", $"<p>{User} 将此话题标记为未解决。</p>")]
    public void Localize_KnownNotices(string html, string expected) => Assert.Equal(expected, SystemNoticeText.Localize(html));

    [Fact]
    public void Localize_LinkedCount_TranslatesLinkText() =>
        Assert.Equal($"<p>{User} 从 {Topic} 移来了 <a href=\"/#narrow/near/5\">2 条消息</a>。</p>",
            SystemNoticeText.Localize($"<p><a href=\"/#narrow/near/5\">2 messages</a> were moved here from {Topic} by {User}.</p>"));

    [Theory]
    [InlineData("<p>Some other notice.</p>")]
    [InlineData("<p>a</p><p>b</p>")]
    public void Localize_Unknown_Unchanged(string html) => Assert.Equal(html, SystemNoticeText.Localize(html));
}
