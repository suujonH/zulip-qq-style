using ZulipQQ.Core;

namespace ZulipQQ.Tests.Core;

public sealed class AvatarUrlsTests
{
    [Theory]
    [InlineData("/user_avatars/2/abc.png?version=3", "/user_avatars/2/abc-medium.png?version=3")]
    [InlineData("/user_avatars/2/abc.png", "/user_avatars/2/abc-medium.png")]
    [InlineData("https://cdn.example.com/2/abc.png?x=x&version=3", "https://cdn.example.com/2/abc-medium.png?x=x&version=3")]
    public void Large_UsesMediumImage(string url, string expected) =>
        Assert.Equal(expected, AvatarUrls.Large(url));

    [Theory]
    [InlineData("/user_avatars/2/abc-medium.png?version=3")]
    [InlineData("https://secure.gravatar.com/avatar/abc?d=identicon&version=1")]
    [InlineData("https://example.com/a?file=abc.png")]
    public void Large_LeavesOtherUrlsUnchanged(string url) =>
        Assert.Equal(url, AvatarUrls.Large(url));
}
