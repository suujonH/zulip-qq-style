using CommunityToolkit.Mvvm.Input;
using ZulipQQ.Models;

namespace ZulipQQ.ViewModels;

/// <summary>某一行的“移动到分组”选项。</summary>
public partial class GroupOptionViewModel(FriendGroup group, bool isCurrent, FriendRowViewModel row) : ViewModelBase
{
    public string Name => group.Name;

    public bool IsCurrent { get; } = isCurrent;

    [RelayCommand]
    private void Select() => row.MoveTo(group.Id);
}
