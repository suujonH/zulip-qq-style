using ZulipQQ.Models;

namespace ZulipQQ.ViewModels;

public class GroupItemViewModel(ChatGroup group) : ViewModelBase
{
    public ChatGroup Group { get; } = group;

    public string Name => Group.Name;

    public string MemberCountText => $"{Group.MemberCount}人";
}
