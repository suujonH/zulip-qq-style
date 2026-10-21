using CommunityToolkit.Mvvm.ComponentModel;
using ZulipQQ.Models;

namespace ZulipQQ.ViewModels;

/// <summary>好友管理器左侧的一个分组筛选项。</summary>
public partial class GroupFilterItemViewModel : ViewModelBase
{
    public GroupFilterItemViewModel(FriendGroup group, int count)
    {
        GroupId = group.Id;
        Name = group.Name;
        Count = count;
    }

    public long GroupId { get; }

    [ObservableProperty]
    public partial string Name { get; private set; }

    [ObservableProperty]
    public partial int Count { get; private set; }

    public void Update(FriendGroup group, int count)
    {
        Name = group.Name;
        Count = count;
    }
}
