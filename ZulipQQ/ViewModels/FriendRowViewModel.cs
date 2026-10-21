using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZulipQQ.Models;
using ZulipQQ.Services;

namespace ZulipQQ.ViewModels;

/// <summary>好友管理器表格中的一行。</summary>
public partial class FriendRowViewModel : ViewModelBase
{
    private readonly FriendManagerViewModel _owner;
    private readonly FriendStore _store;

    public FriendRowViewModel(FriendManagerViewModel owner, FriendStore store, Friend friend)
    {
        _owner = owner;
        _store = store;
        Friend = friend;
    }

    public long UserId => Friend.User.Id;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Nickname), nameof(Remark))]
    public partial Friend Friend { get; private set; }

    public string Nickname => Friend.User.Nickname;

    public string Remark => Friend.Remark ?? "";

    public string GroupName => _store.Groups.FirstOrDefault(g => g.Id == Friend.GroupId)?.Name ?? "";

    /// <summary>分组下拉框与右键“移动联系人至”共用的选项，当前所在分组带勾。</summary>
    public IReadOnlyList<GroupOptionViewModel> GroupOptions =>
        _store.Groups.Select(g => new GroupOptionViewModel(g, g.Id == Friend.GroupId, this)).ToList();

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    [ObservableProperty]
    public partial bool IsEditingRemark { get; set; }

    [ObservableProperty]
    public partial string EditingRemark { get; set; } = "";

    /// <summary>数据刷新后更新为最新的好友数据。分组可能新增，选项总是重新生成。</summary>
    public void Update(Friend friend)
    {
        Friend = friend;
        OnPropertyChanged(nameof(GroupName));
        OnPropertyChanged(nameof(GroupOptions));
    }

    internal void MoveTo(long groupId) => _store.MoveFriends([UserId], groupId);

    partial void OnIsSelectedChanged(bool value) => _owner.OnRowSelectionChanged();

    [RelayCommand]
    private void ToggleSelected() => IsSelected = !IsSelected;

    [RelayCommand]
    private void BeginEditRemark()
    {
        EditingRemark = Remark;
        IsEditingRemark = true;
    }

    // 先退出编辑再保存：隐藏编辑框会触发失去焦点，再次调用时直接返回
    [RelayCommand]
    private void CommitRemark()
    {
        if (!IsEditingRemark)
            return;

        IsEditingRemark = false;
        _store.SetRemark(UserId, EditingRemark);
    }

    [RelayCommand]
    private void CancelRemark() => IsEditingRemark = false;
}
