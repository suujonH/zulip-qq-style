using CommunityToolkit.Mvvm.Input;
using ZulipQQ.Models;

namespace ZulipQQ.ViewModels;

/// <summary>某一行的“移动到分组”选项；<paramref name="group"/> 为 null 表示“我的好友”（移出自定义分组）。</summary>
public partial class GroupOptionViewModel(FriendGroup? group, bool isCurrent, FriendRowViewModel row) : ViewModelBase
{
    public string Name => group?.Name ?? "我的好友";

    public bool IsCurrent { get; } = isCurrent;

    [RelayCommand]
    private void Select() => row.MoveTo(group?.Id);
}
