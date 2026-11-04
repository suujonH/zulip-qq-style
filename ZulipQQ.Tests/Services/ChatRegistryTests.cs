using ZulipQQ.Core;
using ZulipQQ.Services;
using ZulipQQ.ViewModels;

namespace ZulipQQ.Tests.Services;

public class ChatRegistryTests
{
    // 滚动位置的记录不使用会话
    private static ChatRegistry Registry() => new(null!);

    private static readonly TopicKey General = new(6, "general chat");

    [Fact]
    public void ScrollPosition_SavedAndRestored()
    {
        var registry = Registry();
        registry.SaveScrollPosition(General, new ScrollPosition(42, -12.5));

        Assert.Equal(new ScrollPosition(42, -12.5), registry.GetScrollPosition(General));
        Assert.Null(registry.GetScrollPosition(new TopicKey(6, "other")));
    }

    // 离开时停在底部：下次从底部开始
    [Fact]
    public void ScrollPosition_NullClearsSaved()
    {
        var registry = Registry();
        registry.SaveScrollPosition(General, new ScrollPosition(42, 0));
        registry.SaveScrollPosition(General, null);

        Assert.Null(registry.GetScrollPosition(General));
    }

    [Fact]
    public void ScrollPosition_Forget()
    {
        var registry = Registry();
        registry.SaveScrollPosition(General, new ScrollPosition(42, 0));
        registry.ForgetScrollPosition(General);

        Assert.Null(registry.GetScrollPosition(General));
    }
}
