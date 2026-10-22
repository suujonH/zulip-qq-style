using CommunityToolkit.Mvvm.ComponentModel;

namespace ZulipQQ.ViewModels;

/// <summary>聊天中的时间分隔条。</summary>
public partial class TimeSeparatorItem : ViewModelBase
{
    [ObservableProperty]
    public partial string Text { get; set; } = "";
}

/// <summary>“以下为新消息”分隔线，位于打开会话时的第一条未读消息之前。</summary>
public sealed class NewMessagesDividerItem : ViewModelBase;

/// <summary>正在加载更早的消息。</summary>
public sealed class LoadingItem : ViewModelBase;

/// <summary>视口中可见的消息范围，由聊天视图在滚动后报告。</summary>
/// <param name="TopMessageId">视口中第一条服务器消息的 id；没有可见消息时为 0。</param>
public sealed record ViewportInfo(long TopMessageId, long BottomMessageId, bool IsAtBottom, bool IsNearTop, bool IsNearBottom);

public enum ScrollAlign
{
    /// <summary>消息居中。</summary>
    Center,

    /// <summary>“以下为新消息”分隔线位于视口顶部。</summary>
    TopWithDivider,
}

/// <summary>滚动请求；<see cref="MessageId"/> 为 null 表示滚动到底部。</summary>
public sealed record ScrollRequest(long? MessageId, ScrollAlign Align = ScrollAlign.Center)
{
    public static ScrollRequest Bottom { get; } = new((long?)null);
}
