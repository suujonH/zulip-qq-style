using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using ZulipQQ.Core;
using ZulipQQ.ViewModels.Settings;

namespace ZulipQQ.ViewModels;

/// <summary>联系人页“群聊”标签中的一个话题：文字头像（频道颜色）加名称，以及强化推送的效果与图标。数据刷新时就地更新。</summary>
public partial class TopicItemViewModel(TopicKey key) : ViewModelBase
{
    public TopicKey Key { get; } = key;

    [ObservableProperty]
    public partial string Title { get; private set; } = "";

    [ObservableProperty]
    public partial IBrush? AvatarFill { get; private set; }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    /// <summary>话题名下方的推送效果与右侧的强化推送图标（与设置的“强化推送”相同）。</summary>
    [ObservableProperty]
    public partial PushTopicRowViewModel? Push { get; set; }

    public void Update(string title, string? color)
    {
        Title = title;
        AvatarFill = ConversationItemViewModel.BrushOf(color);
    }
}
