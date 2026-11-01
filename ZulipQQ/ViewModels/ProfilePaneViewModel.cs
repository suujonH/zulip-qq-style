namespace ZulipQQ.ViewModels;

/// <summary>右侧资料面板：在内容区居中显示一张名片（<see cref="UserCardViewModel"/> 或 <see cref="TopicCardViewModel"/>）。</summary>
public class ProfilePaneViewModel(ViewModelBase card) : ViewModelBase
{
    public ViewModelBase Card { get; } = card;
}
