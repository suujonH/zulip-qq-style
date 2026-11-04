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

    private const string Said = "<a href=\"http://x/#narrow/near/9\">said</a>";
    private const string Sent = "<a href=\"http://x/#narrow/near/9\">sent</a>";
    private const string User2 = "<span class=\"user-mention silent\" data-user-id=\"9\">小王</span>";
    private const string User3 = "<span class=\"user-mention silent\" data-user-id=\"10\">小李</span>";
    private const string Quote = "<blockquote>\n<p>原文, and more</p>\n</blockquote>";

    [Fact]
    public void LocalizeReminder_ChannelMessage() =>
        Assert.Equal(
            $"<p>你为以下消息设置了提醒。</p>\n<p>{User} 在 {Topic} 中<a href=\"http://x/#narrow/near/9\">说</a>：</p>\n{Quote}",
            SystemNoticeText.LocalizeReminder(
                $"<p>You requested a reminder for the following message.</p>\n<p>{User} {Said} in {Topic}:</p>\n{Quote}"));

    [Fact]
    public void LocalizeReminder_DirectMessageWithNote() =>
        Assert.Equal(
            $"<p>你为以下私信设置了提醒。备注：</p>\n<blockquote>\n<p>记得回复</p>\n</blockquote>\n<p>{User} 对 {User2}、{User3} 和 {User} <a href=\"http://x/#narrow/near/9\">说</a>：</p>",
            SystemNoticeText.LocalizeReminder(
                $"<p>You requested a reminder for the following direct message. Note:</p>\n<blockquote>\n<p>记得回复</p>\n</blockquote>\n<p>{User} {Said} to {User2}, {User3}, and {User}:</p>"));

    [Theory]
    [InlineData($"<p>{User} {Sent} a poll in {Topic}.</p>", $"<p>{User} 在 {Topic} 中<a href=\"http://x/#narrow/near/9\">发起</a>了一个投票。</p>")]
    [InlineData($"<p>{User} {Sent} a todo list to {User2} and {User3}.</p>", $"<p>{User} 向 {User2} 和 {User3} <a href=\"http://x/#narrow/near/9\">发起</a>了一个待办清单。</p>")]
    [InlineData($"<p>You {Sent} yourself a poll.</p>", "<p>你给自己<a href=\"http://x/#narrow/near/9\">发起</a>了一个投票。</p>")]
    [InlineData($"<p>You {Said} a note to yourself:</p>", "<p>你给自己<a href=\"http://x/#narrow/near/9\">写</a>了一条笔记：</p>")]
    public void LocalizeReminder_Context(string html, string expected) =>
        Assert.Equal(expected, SystemNoticeText.LocalizeReminder(html));

    [Fact]
    public void LocalizeReminder_Unknown_Unchanged() =>
        Assert.Equal("<p>Hello, and bye</p>", SystemNoticeText.LocalizeReminder("<p>Hello, and bye</p>"));
}
