using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZulipQQ.Models;
using ZulipQQ.Services;

namespace ZulipQQ.ViewModels;

/// <summary>
/// 好友管理器：左侧按分组筛选，右侧好友表格（昵称 / 备注 / 分组），支持多选后批量设置分组。
/// 选中状态只存在于当前可见的行上：切换左侧分组时清空选择；被搜索或移动操作隐藏的行，其选中状态随行一起丢弃。
/// </summary>
public partial class FriendManagerViewModel : ViewModelBase
{
    // 昵称按中文区域规则排序（汉字按拼音），与 QQ 一致
    private static readonly StringComparer NicknameComparer =
        StringComparer.Create(CultureInfo.GetCultureInfo("zh-CN"), CompareOptions.None);

    private readonly FriendStore _store;

    public FriendManagerViewModel() : this(FriendStore.Instance)
    {
    }

    public FriendManagerViewModel(FriendStore store)
    {
        _store = store;
        _store.Changed += OnStoreChanged;
        RefreshGroupFilters();
        RefreshRows();
    }

    /// <summary>左侧分组列表（不含“全部好友”，“全部好友”对应 <see cref="SelectedGroupFilter"/> 为 null）。</summary>
    public ObservableCollection<GroupFilterItemViewModel> GroupFilters { get; } = [];

    /// <summary>当前筛选条件下的好友行，按昵称排序。</summary>
    public ObservableCollection<FriendRowViewModel> Rows { get; } = [];

    /// <summary>打开中的对话框，后打开的显示在上层。</summary>
    public ObservableCollection<ViewModelBase> Dialogs { get; } = [];

    public int TotalCount => _store.Friends.Count;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAllFriendsFilter))]
    public partial GroupFilterItemViewModel? SelectedGroupFilter { get; set; }

    public bool IsAllFriendsFilter => SelectedGroupFilter is null;

    [ObservableProperty]
    public partial string SearchText { get; set; } = "";

    public int SelectedCount => Rows.Count(r => r.IsSelected);

    public bool HasSelection => Rows.Any(r => r.IsSelected);

    public string SelectedCountText => $"已选 {SelectedCount} 人";

    /// <summary>表头复选框：所有可见行都已选中时为勾选状态。</summary>
    public bool IsAllVisibleSelected => Rows.Count > 0 && Rows.All(r => r.IsSelected);

    /// <summary>界面关闭时调用，停止监听数据变化。</summary>
    public void Detach() => _store.Changed -= OnStoreChanged;

    partial void OnSelectedGroupFilterChanged(GroupFilterItemViewModel? value)
    {
        ClearSelection();
        RefreshRows();
    }

    partial void OnSearchTextChanged(string value) => RefreshRows();

    [RelayCommand]
    private void ShowAllFriends() => SelectedGroupFilter = null;

    [RelayCommand]
    private void ToggleSelectAll()
    {
        var select = !IsAllVisibleSelected;
        foreach (var row in Rows)
            row.IsSelected = select;
    }

    [RelayCommand]
    private void ClearSelection()
    {
        foreach (var row in Rows)
            row.IsSelected = false;
    }

    [RelayCommand]
    private void OpenAddGroupDialog() => ShowAddGroupDialog(null);

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void OpenSetGroupDialog()
    {
        SetGroupDialogViewModel dialog = null!;
        dialog = new SetGroupDialogViewModel(_store, Rows.Where(r => r.IsSelected).Select(r => r.UserId).ToList(),
            ShowAddGroupDialog,
            confirmed =>
            {
                Dialogs.Remove(dialog);
                if (confirmed)
                    ClearSelection();
            });
        Dialogs.Add(dialog);
    }

    /// <summary>打开“添加分组”对话框；创建成功后调用 <paramref name="onCreated"/>。</summary>
    private void ShowAddGroupDialog(Action<FriendGroup>? onCreated)
    {
        AddGroupDialogViewModel dialog = null!;
        dialog = new AddGroupDialogViewModel(_store, group =>
        {
            Dialogs.Remove(dialog);
            if (group is not null)
                onCreated?.Invoke(group);
        });
        Dialogs.Add(dialog);
    }

    /// <summary>由 <see cref="FriendRowViewModel"/> 在选中状态变化时调用。</summary>
    internal void OnRowSelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectedCountText));
        OnPropertyChanged(nameof(IsAllVisibleSelected));
        OpenSetGroupDialogCommand.NotifyCanExecuteChanged();
    }

    private void OnStoreChanged(object? sender, EventArgs e)
    {
        RefreshGroupFilters();
        RefreshRows();
    }

    // 分组只会新增、不会删除或重排，按位置更新即可
    private void RefreshGroupFilters()
    {
        var groups = _store.Groups;
        for (var i = 0; i < groups.Count; i++)
        {
            var count = _store.CountInGroup(groups[i].Id);
            if (i < GroupFilters.Count)
                GroupFilters[i].Update(groups[i], count);
            else
                GroupFilters.Add(new GroupFilterItemViewModel(groups[i], count));
        }

        OnPropertyChanged(nameof(TotalCount));
    }

    /// <summary>
    /// 按当前分组与搜索条件计算应显示的行，并就地同步到 <see cref="Rows"/>：
    /// 仍然可见的行复用原对象（保留选中、编辑状态和滚动位置），不可见的行移除，新出现的行插入到排序位置。
    /// </summary>
    private void RefreshRows()
    {
        var keyword = SearchText.Trim();
        var groupId = SelectedGroupFilter?.GroupId;
        var existing = Rows.ToDictionary(r => r.UserId);

        var desired = _store.Friends
            .Where(f => groupId is null || f.GroupId == groupId)
            .Where(f => keyword.Length == 0
                        || f.User.Nickname.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                        || (f.Remark?.Contains(keyword, StringComparison.OrdinalIgnoreCase) ?? false))
            .OrderBy(f => f.User.Nickname, NicknameComparer)
            .ThenBy(f => f.User.Id)
            .Select(f =>
            {
                if (!existing.TryGetValue(f.User.Id, out var row))
                    return new FriendRowViewModel(this, _store, f);
                row.Update(f);
                return row;
            })
            .ToList();

        var desiredIds = desired.Select(r => r.UserId).ToHashSet();
        for (var i = Rows.Count - 1; i >= 0; i--)
        {
            if (!desiredIds.Contains(Rows[i].UserId))
                Rows.RemoveAt(i);
        }

        for (var i = 0; i < desired.Count; i++)
        {
            if (i < Rows.Count && ReferenceEquals(Rows[i], desired[i]))
                continue;

            var oldIndex = Rows.IndexOf(desired[i]);
            if (oldIndex >= 0)
                Rows.Move(oldIndex, i);
            else
                Rows.Insert(i, desired[i]);
        }

        OnRowSelectionChanged();
    }
}
